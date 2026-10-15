using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Application.Catalog;
using FindEverything.Application.Sharing;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Runtime;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public sealed record WorkspaceSourceChoice(string Id, string DisplayName, ImportedWorkspaceSource? Source);
public sealed record WorkspaceExportContext(string RootPath, string MetadataDatabasePath,
    string? ProfileId = null, string? CatalogDatabasePath = null, CatalogProjectionSnapshot? CatalogSnapshot = null,
    bool RequireCatalog = false);
public interface IWorkspaceExportSource
{
    WorkspaceExportContext? CaptureWorkspaceExport();
}

/// <summary>One explicit data-source selection shared by the two result pages. It never persists imported paths as live scan settings.</summary>
public sealed partial class WorkspaceTransferViewModel : ObservableObject, IDisposable
{
    private readonly IProfileTransferService _profiles;
    private readonly IIndexedDatasetTransferService _datasets;
    private readonly IImportedWorkspaceRegistry _registry;
    private readonly IProfileAuthoringService _authoring;
    private readonly IDesktopPickerService _picker;
    private readonly IContentDialogService _dialogs;
    private readonly ISnackbarService _notifications;
    private readonly IApplicationOperationCoordinator _operations;
    private readonly IDraftProtectionService _drafts;
    private readonly IProfilePathCanonicalizer _canonicalizer;
    private readonly IAppLocalizer _localizer;
    private readonly IUiScheduler _scheduler;
    private readonly string _selectionFile;
    private CancellationTokenSource? _cancellation;
    private readonly SemaphoreSlim _selectionSaveGate = new(1, 1);
    private readonly SemaphoreSlim _rootProbeGate = new(1, 1);
    private bool _initialized;
    private bool _suppressSourceChange;
    private bool _disposed;
    private long _selectionRevision;
    private long _registryRevision;
    private ProfileTransferResult? _pendingProfileActivation;
    private readonly TaskCompletionSource _initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WorkspaceTransferViewModel(IProfileTransferService profiles, IIndexedDatasetTransferService datasets,
        IImportedWorkspaceRegistry registry, IProfileAuthoringService authoring, IDesktopPickerService picker,
        IContentDialogService dialogs, ISnackbarService notifications, IApplicationOperationCoordinator operations,
        IDraftProtectionService drafts, IProfilePathCanonicalizer canonicalizer, IAppLocalizer localizer,
        IUiScheduler scheduler, AppPaths paths)
    {
        (_profiles, _datasets, _registry, _authoring, _picker, _dialogs, _notifications, _operations,
            _drafts, _canonicalizer, _localizer, _scheduler) = (profiles, datasets, registry, authoring, picker,
            dialogs, notifications, operations, drafts, canonicalizer, localizer, scheduler);
        _selectionFile = Path.Combine(paths.LocalDataDirectory, "workspace-source.json");
        var live = new WorkspaceSourceChoice("local", L("Loc.Transfer.Source.Local", "로컬 스캔 결과"), null);
        Sources = [live];
        _selectedSource = live;
        _registry.Changed += OnRegistryChanged;
        _ = InitializeAsync();
    }

    public event EventHandler? SourceChanged;
    public event EventHandler<ProfileTransferResult>? ProfileImported;
    public ProfileTransferResult? TakePendingProfileActivation() => Interlocked.Exchange(ref _pendingProfileActivation, null);
    public ObservableCollection<WorkspaceSourceChoice> Sources { get; }
    [ObservableProperty] private WorkspaceSourceChoice? _selectedSource;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _progressText = string.Empty;
    public ImportedWorkspaceSource? CurrentSource => SelectedSource?.Source;
    public bool IsImported => CurrentSource is not null;
    public bool IsLocal => !IsImported;
    public bool IsInitialized => _initialized;
    public bool IsInitializing => !_initialized;
    public Task InitializationTask => _initialization.Task;
    public bool CanSelectSource => !IsBusy && _initialized;
    public string SourceDescription => CurrentSource is { } source
        ? $"{L("Loc.Transfer.Source.ReadOnly", "공유 데이터 · 읽기 전용")} · {source.EntryCount:N0} · {source.EffectiveRoot}"
        : L("Loc.Transfer.Source.Local.Help", "이 PC에서 스캔한 결과입니다.");

    partial void OnSelectedSourceChanged(WorkspaceSourceChoice? value)
    {
        OnPropertyChanged(nameof(CurrentSource)); OnPropertyChanged(nameof(IsImported));
        OnPropertyChanged(nameof(IsLocal)); OnPropertyChanged(nameof(SourceDescription));
        RelinkCommand.NotifyCanExecuteChanged(); UseLocalCommand.NotifyCanExecuteChanged();
        if (_suppressSourceChange) return;
        SourceChanged?.Invoke(this, EventArgs.Empty);
        if (_initialized) _ = SaveSelectionAsync(value?.Id ?? "local", Interlocked.Increment(ref _selectionRevision));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSelectSource));
        OpenCommand.NotifyCanExecuteChanged(); ExportProfileCommand.NotifyCanExecuteChanged();
        ExportPackageCommand.NotifyCanExecuteChanged(); RelinkCommand.NotifyCanExecuteChanged();
        UseLocalCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var sources = await _registry.ListAsync().ConfigureAwait(false);
            var selected = "local";
            if (File.Exists(_selectionFile) && new FileInfo(_selectionFile).Length < 4096)
                selected = JsonSerializer.Deserialize<string>(await File.ReadAllTextAsync(_selectionFile).ConfigureAwait(false)) ?? selected;
            var selectedId = selected;
            _scheduler.Post(() =>
            {
                if (_disposed) return;
                foreach (var source in sources) Sources.Add(ToChoice(source));
                SelectedSource = Sources.FirstOrDefault(x => x.Id == selectedId) ?? Sources[0];
                CompleteInitialization();
            });
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        { _scheduler.Post(() => { CompleteInitialization(); Notify(exception.Message, ControlAppearance.Caution); }); }
    }

    private void CompleteInitialization()
    {
        _initialized = true;
        _initialization.TrySetResult();
        OnPropertyChanged(nameof(IsInitialized)); OnPropertyChanged(nameof(IsInitializing));
        OnIsBusyChanged(IsBusy);
    }

    private async Task SaveSelectionAsync(string id, long revision)
    {
        await _selectionSaveGate.WaitAsync().ConfigureAwait(false);
        string? temporary = null;
        try
        {
            if (revision != Volatile.Read(ref _selectionRevision)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_selectionFile)!);
            temporary = _selectionFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(id)).ConfigureAwait(false);
            if (revision == Volatile.Read(ref _selectionRevision)) File.Move(temporary, _selectionFile, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { _scheduler.Post(() => Notify(exception.Message, ControlAppearance.Caution)); }
        finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); _selectionSaveGate.Release(); }
    }

    private WorkspaceSourceChoice ToChoice(ImportedWorkspaceSource source) => new(source.Id,
        $"{source.DisplayName} · {L("Loc.Transfer.Source.Shared", "공유")}", source);

    internal ICatalogProjectionStore OpenCatalogStore(ImportedWorkspaceSource source) => _datasets.OpenCatalogStore(source);
    internal Task<ImportedWorkspaceSource> EnsureSourceCultureAsync(ImportedWorkspaceSource source, CancellationToken token) =>
        _datasets.EnsureCultureAsync(source, new Progress<WorkspaceTransferProgress>(progress => ProgressText = $"{progress.Stage} · {progress.Completed:N0}"), token);

    private async void OnRegistryChanged(object? sender, EventArgs e)
    {
        var revision = Interlocked.Increment(ref _registryRevision);
        try
        {
            var sources = await _registry.ListAsync().ConfigureAwait(false);
            _scheduler.Post(() =>
            {
                if (_disposed || revision != Volatile.Read(ref _registryRevision)) return;
                var selectedId = SelectedSource?.Id;
                var old = CurrentSource;
                _suppressSourceChange = true;
                try
                {
                    for (var index = Sources.Count - 1; index > 0; index--) Sources.RemoveAt(index);
                    foreach (var source in sources) Sources.Add(ToChoice(source));
                    SelectedSource = Sources.FirstOrDefault(x => x.Id == selectedId) ?? Sources[0];
                }
                finally { _suppressSourceChange = false; }
                // Collection updates must not temporarily select Local and start an automatic load.
                if (selectedId != SelectedSource?.Id || old?.LinkRevision != CurrentSource?.LinkRevision)
                    OnSelectedSourceChanged(SelectedSource);
            });
        }
        catch (Exception exception) { _scheduler.Post(() => Notify(exception.Message, ControlAppearance.Caution)); }
    }

    private bool CanTransfer() => !IsBusy && !_disposed && _initialized;
    [RelayCommand(CanExecute = nameof(CanTransfer))]
    private async Task OpenAsync()
    {
        var path = _picker.PickWorkspaceImportPath();
        if (path is not null) await OpenPathAsync(path);
    }

    public async Task<ProfileTransferResult?> OpenPathAsync(string path)
    {
        if (!CanTransfer()) return null;
        if (!await _drafts.ConfirmAllAsync()) return null;
        ProfileTransferResult? importedProfile = null;
        await RunAsync(async token =>
        {
            if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                var inspection = await _profiles.InspectAsync(path, token);
                var choice = await ChooseConflictAsync(inspection.HasIdConflict, inspection.Manifest, token);
                if (choice == ProfileImportConflictChoice.Cancel) return;
                await _operations.RunAsync(ApplicationOperationKind.ProfileWrite,
                    async ct => { importedProfile = await _profiles.ImportManifestAsync(inspection.Manifest, choice, ct); }, token);
                if (importedProfile is not null)
                {
                    // Every entry point (button, sharing menu, drop, Home) activates the same installed ID.
                    _pendingProfileActivation = importedProfile;
                    ProfileImported?.Invoke(this, importedProfile);
                    Notify(L("Loc.Transfer.Profile.Imported", "프로필을 저장하고 적용했습니다."));
                }
            }
            else if (Path.GetExtension(path).Equals(".fepkg", StringComparison.OrdinalIgnoreCase))
            {
                await using var prepared = await _datasets.PrepareImportAsync(path, token);
                var inspection = prepared.Inspection;
                if (!inspection.HasCatalog)
                    throw new InvalidDataException(L("Loc.Transfer.Package.StructuredRequired", "이 패키지에는 구조화 결과가 없어 현재 앱에서 조회할 수 없습니다. 보낸 사람에게 구조화 결과를 포함해 다시 내보내도록 요청하세요. 아직 설치하지 않았습니다."));
                var editable = await _authoring.ListAsync(token);
                var choice = await ChooseConflictAsync(editable.Any(x => x.Id.Equals(inspection.Profile.Id, StringComparison.OrdinalIgnoreCase)), inspection.Profile, token);
                if (choice == ProfileImportConflictChoice.Cancel) return;
                var linked = await ChooseRootAsync(inspection.OriginalRoot, token);
                if (linked.Cancelled) return;
                ImportedWorkspaceSource? installed = null;
                await _operations.RunAsync(ApplicationOperationKind.ProfileWrite,
                    async ct => installed = await _datasets.ImportPreparedAsync(prepared, choice, linked.Root, Progress(), ct), token);
                if (installed is null) return;
                var choiceItem = ToChoice(installed);
                var existing = Sources.FirstOrDefault(x => x.Id == installed.Id);
                if (existing is null) Sources.Add(choiceItem); else choiceItem = existing;
                SelectedSource = choiceItem;
                Notify(L("Loc.Transfer.Package.Imported", "공유 데이터를 가져왔습니다. 원본 파일이 없어도 조회할 수 있습니다."));
            }
            else throw new InvalidDataException(L("Loc.Transfer.Unsupported", "프로필 JSON 또는 .fepkg 파일을 선택하세요."));
        });
        return importedProfile;
    }

    [RelayCommand(CanExecute = nameof(CanTransfer))]
    private async Task ExportProfileAsync(object? profile)
    {
        var id = profile switch
        {
            ProfileChoiceViewModel choice => choice.Id,
            ProfileSummaryViewModel summary => summary.Id,
            EditableProfileSummary editable => editable.Id,
            string value => value,
            _ => null,
        };
        await RunAsync(async token =>
        {
            var items = await _authoring.ListAsync(token);
            var selected = items.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (selected is null) { Notify(L("Loc.Transfer.Profile.Select", "저장된 JSON 프로필을 선택하세요. DLL 프로필은 JSON으로 내보낼 수 없습니다."), ControlAppearance.Caution); return; }
            var manifest = await _authoring.LoadAsync(selected, token) ?? throw new InvalidDataException("Profile is unavailable.");
            var destination = _picker.PickWorkspaceExportPath(selected.Id, false);
            if (destination is null) return;
            await _profiles.ExportAsync(manifest, destination, token);
            Notify(L("Loc.Transfer.Profile.Exported", "프로필 JSON을 내보냈습니다."));
        });
    }

    [RelayCommand(CanExecute = nameof(CanTransfer))]
    private async Task ExportPackageAsync(object? owner)
    {
        var context = (owner as IWorkspaceExportSource)?.CaptureWorkspaceExport();
        if (context is null) { Notify(L("Loc.Transfer.Package.Unavailable", "로컬 스캔이 완료된 데이터를 선택하세요. 진행 중인 데이터는 내보낼 수 없습니다."), ControlAppearance.Caution); return; }
        await RunAsync(async token =>
        {
            var profiles = await _authoring.ListAsync(token);
            if (context.RequireCatalog)
                profiles = profiles.Where(profile => string.Equals(profile.Id, context.ProfileId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (profiles.Count == 0) { Notify(L("Loc.Transfer.Profile.Select", "먼저 저장된 JSON 프로필을 선택하세요."), ControlAppearance.Caution); return; }
            var combo = new System.Windows.Controls.ComboBox { ItemsSource = profiles, DisplayMemberPath = "DisplayName", MinWidth = 280,
                SelectedItem = profiles.FirstOrDefault(x => x.Id == context.ProfileId) ?? profiles[0] };
            var notes = new System.Windows.Controls.CheckBox { Content = L("Loc.Transfer.IncludeNotes", "메모·사용자 입력값 포함"), IsChecked = false, Margin = new(0, 12, 0, 0) };
            var catalog = new System.Windows.Controls.CheckBox { Content = L("Loc.Transfer.IncludeCatalog", "구조화 결과 포함"), Margin = new(0, 8, 0, 0) };
            void UpdateCatalog()
            {
                var available = context.CatalogSnapshot is not null && (combo.SelectedItem as EditableProfileSummary)?.Id == context.ProfileId;
                catalog.IsEnabled = available && !context.RequireCatalog;
                catalog.IsChecked = available;
            }
            combo.SelectionChanged += (_, _) => UpdateCatalog(); UpdateCatalog();
            var content = new System.Windows.Controls.StackPanel();
            content.Children.Add(new TextBlock { Text = L("Loc.Workspace.Profile.Label", "프로필"), Margin = new(0, 0, 0, 4) });
            content.Children.Add(combo); content.Children.Add(catalog); content.Children.Add(notes);
            content.Children.Add(new TextBlock { Text = L("Loc.Transfer.Package.Privacy", "선택한 루트의 확정된 결과만 포함합니다. 메모는 기본적으로 제외합니다."), TextWrapping = System.Windows.TextWrapping.Wrap, MaxWidth = 420, Margin = new(0, 12, 0, 0) });
            var response = await _dialogs.ShowAsync(new ContentDialog { Title = L("Loc.Transfer.Export.Package", "프로필·인덱스 묶음 내보내기"), Content = content,
                PrimaryButtonText = L("Loc.Workspace.Selection.Export", "내보내기"), CloseButtonText = L("Loc.Common.Cancel", "취소"), DefaultButton = ContentDialogButton.Primary }, token);
            if (response != ContentDialogResult.Primary || combo.SelectedItem is not EditableProfileSummary selected) return;
            if (context.RequireCatalog && (context.CatalogSnapshot is null || catalog.IsChecked != true))
                throw new InvalidDataException(L("Loc.Transfer.Package.StructuredRequired", "구조화 결과를 포함한 패키지만 현재 앱에서 조회할 수 있습니다."));
            var manifest = await _authoring.LoadAsync(selected, token) ?? throw new InvalidDataException("Profile is unavailable.");
            var destination = _picker.PickWorkspaceExportPath(selected.Id, true);
            if (destination is null) return;
            var canonicalRoot = await Task.Run(() => _canonicalizer.Canonicalize(context.RootPath), token);
            token.ThrowIfCancellationRequested();
            var request = new WorkspacePackageExportRequest(manifest, context.MetadataDatabasePath, context.RootPath, destination)
            {
                CanonicalRootPath = canonicalRoot,
                CatalogDatabasePath = catalog.IsChecked == true ? context.CatalogDatabasePath : null,
                CatalogSnapshot = catalog.IsChecked == true ? context.CatalogSnapshot : null,
                IncludeUserInputs = notes.IsChecked == true,
                PresentationPath = Path.Combine(Path.GetDirectoryName(selected.ManifestPath)!, "presentation.json"),
            };
            await _operations.RunAsync(ApplicationOperationKind.Exclusive, ct => _datasets.ExportAsync(request, Progress(), ct), token);
            Notify(L("Loc.Transfer.Package.Exported", "프로필·인덱스 묶음을 내보냈습니다."));
        });
    }

    private bool CanRelink() => CanTransfer() && IsImported;
    [RelayCommand(CanExecute = nameof(CanRelink))]
    private async Task RelinkAsync()
    {
        if (CurrentSource is not { } source) return;
        var root = await _picker.PickRootDirectoryAsync(null);
        if (root is null || !CanRelink() || CurrentSource?.Id != source.Id) return;
        await RunAsync(async token =>
        {
            var canonicalRoot = await Task.Run(() => _canonicalizer.Canonicalize(root), token);
            token.ThrowIfCancellationRequested();
            var linked = await _datasets.RelinkAsync(source, canonicalRoot, Progress(), token);
            var current = Sources.FirstOrDefault(x => x.Id == linked.Id);
            if (current is not null) Sources.Remove(current);
            var choice = ToChoice(linked); Sources.Add(choice); SelectedSource = choice;
            Notify(L("Loc.Transfer.Root.Relinked", "조회 경로를 다시 연결했습니다. 구조화 값은 변경하지 않았습니다."));
        });
    }

    private bool CanUseLocal() => !IsBusy && IsImported;
    [RelayCommand(CanExecute = nameof(CanUseLocal))]
    private void UseLocal() => SelectedSource = Sources[0];

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancellation?.Cancel();
    private bool CanCancel() => IsBusy;

    private async Task<ProfileImportConflictChoice> ChooseConflictAsync(bool conflict, ProfileManifest profile, CancellationToken token)
    {
        if (!conflict) return ProfileImportConflictChoice.NewProfile;
        var result = await _dialogs.ShowAsync(new ContentDialog { Title = L("Loc.Transfer.Conflict.Title", "같은 ID의 프로필이 있습니다"),
            Content = $"{profile.DisplayName} ({profile.Id})" + Environment.NewLine +
                L("Loc.Transfer.Conflict.Message", "새 프로필로 가져오면 기존 프로필과 데이터가 유지됩니다. 교체하면 프로필 규칙과, JSON에 포함된 출력 포맷·선택 포맷·컬럼 표시 설정을 바꿉니다. 기존 스캔 DB는 삭제하지 않습니다."),
            PrimaryButtonText = L("Loc.Transfer.Conflict.New", "새 프로필로 가져오기"), SecondaryButtonText = L("Loc.Transfer.Conflict.Replace", "기존 프로필 교체"),
            CloseButtonText = L("Loc.Common.Cancel", "취소"), DefaultButton = ContentDialogButton.Primary }, token);
        if (result == ContentDialogResult.Secondary)
        {
            var confirm = await _dialogs.ShowAsync(new ContentDialog { Title = L("Loc.Transfer.Conflict.Replace", "기존 프로필 교체"),
                Content = $"{profile.DisplayName} ({profile.Id})" + Environment.NewLine +
                    L("Loc.Transfer.Conflict.Confirm", "이 프로필의 규칙과 포함된 출력 포맷·선택 포맷·컬럼 표시 설정을 가져온 내용으로 교체할까요? 기존 스캔 DB는 유지됩니다."), PrimaryButtonText = L("Loc.Transfer.Conflict.Replace", "교체"),
                CloseButtonText = L("Loc.Common.Cancel", "취소"), DefaultButton = ContentDialogButton.Close }, token);
            return confirm == ContentDialogResult.Primary ? ProfileImportConflictChoice.Replace : ProfileImportConflictChoice.Cancel;
        }
        return result == ContentDialogResult.Primary ? ProfileImportConflictChoice.NewProfile : ProfileImportConflictChoice.Cancel;
    }

    private async Task<(bool Cancelled, string? Root)> ChooseRootAsync(string original, CancellationToken token)
    {
        bool reachable;
        try
        {
            reachable = false;
            if (await _rootProbeGate.WaitAsync(0, token))
            {
                // An abandoned native network probe retains this gate until it actually
                // finishes, so repeated imports cannot accumulate blocked workers.
                var probe = Task.Run(() =>
                {
                    try { return Directory.Exists(original); }
                    finally { _rootProbeGate.Release(); }
                });
                reachable = await probe.WaitAsync(TimeSpan.FromSeconds(10), token);
            }
        }
        catch (TimeoutException) { reachable = false; }
        if (reachable) return (false, original);
        var result = await _dialogs.ShowAsync(new ContentDialog { Title = L("Loc.Transfer.Root.Title", "원래 경로에 연결할 수 없습니다"),
            Content = original + Environment.NewLine + L("Loc.Transfer.Root.Help", "이 PC의 해당 폴더를 연결하거나, 원본 파일 없이 저장된 데이터만 조회할 수 있습니다."),
            PrimaryButtonText = L("Loc.Transfer.Root.Connect", "다른 폴더 연결"), SecondaryButtonText = L("Loc.Transfer.Root.Offline", "오프라인으로 조회"),
            CloseButtonText = L("Loc.Common.Cancel", "취소"), DefaultButton = ContentDialogButton.Secondary }, token);
        if (result == ContentDialogResult.Secondary) return (false, null);
        if (result != ContentDialogResult.Primary) return (true, null);
        var root = await _picker.PickRootDirectoryAsync(null, token);
        token.ThrowIfCancellationRequested();
        if (root is null) return (true, null);
        var canonicalRoot = await Task.Run(() => _canonicalizer.Canonicalize(root), token);
        token.ThrowIfCancellationRequested();
        return (false, canonicalRoot);
    }

    private IProgress<WorkspaceTransferProgress> Progress() => new Progress<WorkspaceTransferProgress>(progress =>
        ProgressText = progress.Total is { } total ? $"{progress.Stage} · {progress.Completed:N0}/{total:N0}" : $"{progress.Stage} · {progress.Completed:N0}");

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (!CanTransfer()) return;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        IsBusy = true; ProgressText = L("Loc.Transfer.Working", "공유 데이터를 처리하는 중…");
        try
        {
            if (_operations.CurrentKind == ApplicationOperationKind.IndexLoad)
                await _operations.CancelAndWaitAsync(TimeSpan.FromSeconds(30), cancellation.Token);
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Notify(L("Loc.Common.Cancelled", "취소했습니다."), ControlAppearance.Caution); }
        catch (Exception exception) { Notify(exception.Message, ControlAppearance.Danger); }
        finally { _cancellation = null; IsBusy = false; ProgressText = string.Empty; }
    }

    private void Notify(string message, ControlAppearance appearance = ControlAppearance.Success) => _notifications.Show(
        L("Loc.Transfer.Title", "프로필·데이터 공유"), message, appearance, null, TimeSpan.FromSeconds(3));
    private string L(string key, string fallback) => _localizer.Get(key, fallback);
    public void Dispose() { _disposed = true; _initialization.TrySetCanceled(); _cancellation?.Cancel(); _registry.Changed -= OnRegistryChanged; }
}
