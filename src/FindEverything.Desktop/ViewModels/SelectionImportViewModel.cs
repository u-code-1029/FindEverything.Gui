using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Application.Catalog;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Runtime;
using Microsoft.Win32;

namespace FindEverything.Desktop.ViewModels;

public sealed partial class SelectionImportViewModel : ObservableObject, IDisposable
{
    private readonly IAppLocalizer _localizer;
    private readonly SelectionImportReader _reader;
    private readonly Func<ProfileSelectionMatchRuleManifest, SelectionImportDocument, CancellationToken, Task<CatalogSelectionMatchResult>> _match;
    private readonly CancellationToken _lifetime;
    private readonly CancellationTokenRegistration _lifetimeRegistration;
    private CancellationTokenSource? _cancellation;
    private SelectionImportDocument? _document;
    private CatalogSelectionMatchResult? _result;
    private string? _filePath;
    private bool _html;
    private bool _disposed;
    private long _revision;
    public SelectionImportViewModel(IReadOnlyList<ProfileSelectionMatchRuleManifest> rules, IAppLocalizer localizer,
        Func<ProfileSelectionMatchRuleManifest, SelectionImportDocument, CancellationToken, Task<CatalogSelectionMatchResult>> match,
        CancellationToken lifetime, SelectionImportReader? reader = null)
    {
        Rules = rules; _localizer = localizer; _match = match; _lifetime = lifetime; _reader = reader ?? new();
        _selectedRule = rules.FirstOrDefault();
        var context = SynchronizationContext.Current;
        _lifetimeRegistration = lifetime.Register(() =>
        {
            void CancelExpiredInput()
            {
                if (_disposed) return;
                _cancellation?.Cancel();
                Summary = L("Loc.SelectionImport.QueryChanged", "검색 결과가 변경되어 적용할 수 없습니다. 창을 닫고 다시 가져오세요.");
                NotifyCommands();
            }
            if (context is not null) context.Post(_ => CancelExpiredInput(), null); else CancelExpiredInput();
        });
    }
    public IReadOnlyList<ProfileSelectionMatchRuleManifest> Rules { get; }
    [ObservableProperty] private ProfileSelectionMatchRuleManifest? _selectedRule;
    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private string _sourceDescription = "";
    [ObservableProperty] private IReadOnlyList<string> _sheets = [];
    [ObservableProperty] private string? _selectedSheet;
    [ObservableProperty] private string _tableNumber = "1";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _error = "";
    public ObservableCollection<string> PreviewRows { get; } = [];
    public bool CanPreview => !IsBusy && SelectedRule is not null && (_filePath is not null || !string.IsNullOrWhiteSpace(InputText));
    public bool CanApply => !IsBusy && _result is not null && _result.Summary.ValidRows > 0 && !_lifetime.IsCancellationRequested;
    public bool HasSheets => Sheets.Count > 0;
    public string MappingDescription => SelectedRule is null ? "" : string.Join(" · ", SelectedRule.Columns.Select(column =>
        (column.InputHeader ?? (column.InputColumnIndex is { } index ? (index + 1).ToString() : column.RegexGroup ?? "한 줄")) + " → " + column.FieldId));
    public event EventHandler? ApplyRequested;
    public event EventHandler? CloseRequested;

    partial void OnSelectedRuleChanged(ProfileSelectionMatchRuleManifest? value)
    { Invalidate(); OnPropertyChanged(nameof(MappingDescription)); }
    partial void OnInputTextChanged(string value)
    { _filePath = null; _html = value.TrimStart().StartsWith("<", StringComparison.Ordinal) && value.Contains("<table", StringComparison.OrdinalIgnoreCase); Sheets = []; SourceDescription = ""; Invalidate(); }
    partial void OnSelectedSheetChanged(string? value) => Invalidate();
    partial void OnTableNumberChanged(string value) => Invalidate();
    partial void OnSheetsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasSheets));
    partial void OnIsBusyChanged(bool value)
    { NotifyCommands(); CancelCommand.NotifyCanExecuteChanged(); OpenFileCommand.NotifyCanExecuteChanged(); PasteCommand.NotifyCanExecuteChanged(); }
    private void NotifyCommands()
    { PreviewCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanPreview)); OnPropertyChanged(nameof(CanApply)); }
    private void Invalidate()
    {
        Interlocked.Increment(ref _revision); _cancellation?.Cancel();
        _result?.Dispose(); _result = null;
        _document?.Dispose(); _document = null;
        PreviewRows.Clear(); Summary = ""; Error = ""; NotifyCommands();
    }
    public void SetClipboardInput(string text, bool html)
    { InputText = text; _html = html; SourceDescription = L("Loc.SelectionImport.Clipboard", "클립보드"); }

    private bool CanChangeInput() => !IsBusy;
    [RelayCommand(CanExecute = nameof(CanChangeInput))]
    private void Paste()
    {
        try
        {
            if (Clipboard.ContainsData(DataFormats.Html) && Clipboard.GetData(DataFormats.Html) is string { Length: > 0 } html)
                SetClipboardInput(html, true);
            else if (Clipboard.ContainsText()) SetClipboardInput(Clipboard.GetText(), false);
            else Error = L("Loc.SelectionImport.Clipboard.Empty", "클립보드에 표 또는 텍스트가 없습니다.");
        }
        catch (ExternalException exception) { Error = exception.Message; }
    }
    [RelayCommand(CanExecute = nameof(CanChangeInput))]
    private async Task OpenFileAsync()
    {
        var picker = new OpenFileDialog
        {
            Title = L("Loc.SelectionImport.OpenFile", "선택할 데이터를 가져오기"),
            Filter = "XLSX / HTML / TXT / CSV / TSV|*.xlsx;*.html;*.htm;*.txt;*.csv;*.tsv", Multiselect = false,
        };
        if (picker.ShowDialog(System.Windows.Application.Current?.MainWindow) != true) return;
        Invalidate(); InputText = ""; _filePath = picker.FileName; SourceDescription = picker.FileName;
        var revision = Volatile.Read(ref _revision);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _cancellation = cancellation;
        try
        {
            if (Path.GetExtension(picker.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                IsBusy = true;
                var sheets = await Task.Run(() => SelectionImportReader.ReadSheetNames(picker.FileName), cancellation.Token)
                    .WaitAsync(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (_disposed || revision != Volatile.Read(ref _revision)) return;
                Sheets = sheets;
                SelectedSheet = Sheets.FirstOrDefault();
            }
            else Sheets = [];
        }
        catch (OperationCanceledException) { Summary = L("Loc.SelectionImport.Cancelled", "취소했습니다. 기존 체크는 유지됩니다."); }
        catch (Exception exception) { Error = exception.Message; }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            IsBusy = false; NotifyCommands();
        }
    }
    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        if (SelectedRule is not { } rule || _disposed) return;
        Invalidate();
        var revision = Volatile.Read(ref _revision);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _cancellation = cancellation;
        IsBusy = true; Summary = L("Loc.SelectionImport.Reading", "데이터를 해석하고 전체 검색 결과와 비교하는 중…");
        SelectionImportDocument? document = null;
        CatalogSelectionMatchResult? result = null;
        try
        {
            if (!int.TryParse(TableNumber, out var number) || number < 1 || number > 1000)
                throw new InvalidDataException(L("Loc.SelectionImport.Table.Invalid", "HTML 표 번호는 1–1000이어야 합니다."));
            document = _filePath is not null
                ? await _reader.ReadFileAsync(_filePath, rule, SelectedSheet, number - 1, cancellation.Token)
                : await _reader.ReadTextAsync(InputText, rule, _html, number - 1, cancellation.Token);
            result = await _match(rule, document, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed || revision != Volatile.Read(ref _revision)) return;
            _document = document; document = null; _result = result; result = null;
            foreach (var row in _document.PreviewRows)
                PreviewRows.Add($"{row.RowNumber:N0}  " + string.Join("  |  ", row.Values.Select((value, index) => rule.Columns[index].FieldId + ": " + (value ?? "∅")).Select(value => value.Replace("\r", " ").Replace("\n", " ↵ "))));
            var stats = _result.Summary;
            Summary = _localizer.Format("Loc.SelectionImport.Summary", "입력 {0:N0}행 · 일치 {1:N0}개 · 미일치 {2:N0}행 · 잘못된 입력 {3:N0}행 · 중복 입력 {4:N0}행 · 여러 항목에 일치 {5:N0}행",
                stats.InputRows, stats.MatchingItems, stats.UnmatchedInputRows, stats.InvalidRows, stats.DuplicateInputRows, stats.AmbiguousInputRows);
            if (rule.Columns.Any(column => !string.IsNullOrWhiteSpace(column.RegexPattern)))
                Summary = L("Loc.SelectionImport.Regex.PassedRows", "정규식 조건을 모두 통과한 행 기준 · ") + Summary;
            Error = string.Join(Environment.NewLine, stats.Issues.Select(issue => $"{issue.RowNumber:N0}: {issue.Message}"));
        }
        catch (OperationCanceledException)
        { if (!_disposed) Summary = L("Loc.SelectionImport.Cancelled", "취소했습니다. 기존 체크는 유지됩니다."); }
        catch (Exception exception)
        { if (!_disposed) { Summary = ""; Error = UserFacingExceptionLocalizer.TranslateOperationFailure(_localizer, exception); } }
        finally
        {
            document?.Dispose(); result?.Dispose();
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            IsBusy = false; NotifyCommands();
        }
    }
    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply() => ApplyRequested?.Invoke(this, EventArgs.Empty);
    private bool CanCancel() => IsBusy;
    [RelayCommand(CanExecute = nameof(CanCancel))] private void Cancel() => _cancellation?.Cancel();
    [RelayCommand] private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
    public CatalogSelectionMatchResult? TakeResult()
    { var result = _result; _result = null; return result; }
    public void Dispose()
    { if (_disposed) return; _disposed = true; _lifetimeRegistration.Dispose(); _cancellation?.Cancel(); _result?.Dispose(); _document?.Dispose(); }
    private string L(string key, string fallback) => _localizer.Get(key, fallback);
}
