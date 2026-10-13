using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using FindEverything.Application.Catalog;
using FindEverything.Application.FileSearch;
using FindEverything.Application.Indexing;
using FindEverything.Application.Options;
using FindEverything.Application.Results;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Xunit;

namespace FindEverything.Desktop.Tests;

[CollectionDefinition("Catalog async UI safety", DisableParallelization = true)]
public sealed class CatalogAsyncUiSafetyCollection { }

[Collection("Catalog async UI safety")]
public sealed class CatalogMissingDataAndCancellationTests
{
    [Fact]
    public Task Cancel_interrupts_cached_count_even_when_the_provider_ignores_cancellation() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: true, ignoreCountCancellation: true);
        var load = fixture.ViewModel.LoadCommand.ExecuteAsync(null);
        await fixture.Query.CountStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(fixture.ViewModel.IsBusy);
        Assert.True(fixture.ViewModel.HasLoadedItems);
        Assert.True(fixture.ViewModel.IsResultCountPending);
        var firstRow = Assert.Single(fixture.ViewModel.Items.Cast<CatalogItemViewModel>());
        await fixture.ViewModel.SelectRowAsync(firstRow, additive: true, range: false);
        Assert.True(firstRow.IsSelected);
        Assert.True(fixture.ViewModel.CopySelectionCommand.CanExecute(null));
        Assert.True(fixture.ViewModel.ExportSelectionCommand.CanExecute(null));
        Assert.True(fixture.ViewModel.CancelCommand.CanExecute(null));
        fixture.ViewModel.CancelCommand.Execute(null);
        await load.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(fixture.Query.CountToken.IsCancellationRequested);
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.False(fixture.ViewModel.CancelCommand.CanExecute(null));
        Assert.True(fixture.ViewModel.HasLoadedItems);
        Assert.Same(firstRow, Assert.Single(fixture.ViewModel.Items.Cast<CatalogItemViewModel>()));
        Assert.False(fixture.ViewModel.IsResultCountPending);
        Assert.False(fixture.ViewModel.SelectAllCommand.CanExecute(null));
        Assert.True(fixture.ViewModel.LoadCommand.CanExecute(null));
        Assert.DoesNotContain("전체 개수 확인 중", fixture.ViewModel.FilterSummary, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Service.ScanCalls);
        Assert.Equal(0, fixture.Service.LoadCalls);

        // An uncooperative background read may finish later, but it must not
        // replace already-visible rows or turn cancellation into successful completion.
        fixture.Query.ReleaseCount.TrySetResult(1);
        await Task.Delay(250);
        Assert.True(fixture.ViewModel.HasLoadedItems);
        Assert.Same(firstRow, Assert.Single(fixture.ViewModel.Items.Cast<CatalogItemViewModel>()));
        Assert.True(firstRow.IsSelected);
        Assert.False(fixture.Query.IsDisposed); // The visible page still owns its query.
    });

    [Fact]
    public Task Cancelled_count_can_be_retried_manually_without_rescanning_or_adopting_the_old_count() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: true, ignoreCountCancellation: true);
        var initialLoad = fixture.ViewModel.LoadCommand.ExecuteAsync(null);
        await fixture.Query.CountStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var initialRow = Assert.Single(fixture.ViewModel.Items.Cast<CatalogItemViewModel>());
        fixture.ViewModel.CancelCommand.Execute(null);
        await initialLoad.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(fixture.ViewModel.SelectAllCommand.CanExecute(null));

        var retryQuery = new Query(ignoreCountCancellation: false, cacheCount: 1);
        fixture.Store.ReplacementQuery = retryQuery;
        await fixture.ViewModel.LoadCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        var retriedRow = Assert.Single(fixture.ViewModel.Items.Cast<CatalogItemViewModel>());
        Assert.NotSame(initialRow, retriedRow);
        Assert.True(fixture.Query.IsDisposed);
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.False(fixture.ViewModel.IsResultCountPending);
        Assert.True(fixture.ViewModel.SelectAllCommand.CanExecute(null));
        Assert.Equal(2, fixture.Store.OpenQueryCalls);
        Assert.Equal(0, fixture.Service.ScanCalls);
        Assert.Equal(0, fixture.Service.LoadCalls);
        fixture.ViewModel.SelectAllCommand.Execute(null);
        Assert.Equal(1, fixture.ViewModel.SelectedCount);

        // Completion of the abandoned, uncooperative count cannot overwrite
        // this retry's rows, exact total, or selection.
        fixture.Query.ReleaseCount.TrySetResult(99);
        await Task.Delay(100);
        Assert.Same(retriedRow, Assert.Single(fixture.ViewModel.Items.Cast<CatalogItemViewModel>()));
        Assert.True(retriedRow.IsSelected);
        Assert.Equal(1, fixture.ViewModel.SelectedCount);
        Assert.False(fixture.ViewModel.IsResultCountPending);
    });

    [Fact]
    public Task File_count_failure_retains_usable_rows_and_can_retry_without_scanning() => OnStaAsync(async () =>
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "file-count-fixture.db");
        var service = new FileService();
        using var viewModel = new FileSearchViewModel(service, new IndexingSettings(), new Workspace(databasePath),
            new DatabaseResolver(databasePath), null!, null!,
            new ApplicationOperationCoordinator(NullLogger<ApplicationOperationCoordinator>.Instance),
            new Notifier(), new Snackbar(), new Formats(), null!, null!, new Profiles(), null!,
            new TestAppLocalizer(), NullLogger<FileSearchViewModel>.Instance);
        var loading = viewModel.ActivateAsync();
        await service.Query.CountStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var row = Assert.Single(viewModel.Items);
        await viewModel.SelectRowAsync(row, additive: true, range: false);
        Assert.True(row.IsSelected);
        Assert.True(viewModel.CopySelectionCommand.CanExecute(null));
        service.Query.ReleaseCount.TrySetException(new IOException("Simulated count failure."));
        await loading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(row, Assert.Single(viewModel.Items));
        Assert.True(row.IsSelected);
        Assert.False(service.Query.IsDisposed);
        Assert.False(viewModel.IsSearching);
        Assert.False(viewModel.IsResultCountPending);
        Assert.False(viewModel.SelectAllCommand.CanExecute(null));
        Assert.True(viewModel.CanRetryResultCount);
        Assert.True(viewModel.RetryResultCountCommand.CanExecute(null));
        Assert.True(viewModel.CopySelectionCommand.CanExecute(null));

        var oldQuery = service.Query;
        service.Query = new FileQuery();
        var retrying = viewModel.RetryResultCountCommand.ExecuteAsync(null);
        await service.Query.CountStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Query.ReleaseCount.TrySetResult(1);
        await retrying.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(oldQuery.IsDisposed);
        Assert.True(Assert.Single(viewModel.Items).IsSelected);
        Assert.Equal(1, viewModel.SelectedCount);
        Assert.True(viewModel.SelectAllCommand.CanExecute(null));
        Assert.False(viewModel.CanRetryResultCount);
        Assert.Equal(2, service.OpenCalls);
        Assert.Equal(0, service.ScanCalls);
    });

    [Fact]
    public Task Live_filter_eligibility_starts_without_a_second_debounce() => OnStaAsync(() =>
    {
        using var fixture = new Fixture(cacheExists: false);
        var owner = fixture.ViewModel;
        var fields = new SampleProfile().Descriptor.Fields;
        var scope = new CatalogProjectionScope("live", "sample", Path.GetTempPath(), "live.db", "fingerprint", "ko-KR", null);
        using var writer = new Writer(scope, fields);
        writer.Append(new(Path.Combine(Path.GetTempPath(), "sample"), "sample", "rule", new object(), new Dictionary<string, object?>(), false));
        owner.ApplyColumnFilter(new(CatalogColumnTarget.Field("client"), CatalogFilterOperator.Contains) { Text = "Example" });
        const System.Reflection.BindingFlags instancePrivate = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var version = (long)typeof(CatalogViewModel).GetField("_resultSessionVersion", instancePrivate)!.GetValue(owner)!;
        typeof(CatalogViewModel).GetMethod("CaptureLiveFilterSnapshot", instancePrivate)!.Invoke(owner, [writer, scope, fields, version]);
        typeof(CatalogViewModel).GetMethod("ScheduleLiveFilterRefresh", instancePrivate)!.Invoke(owner, [true]);
        // No clock threshold: the eligibility provider must have been invoked
        // synchronously before scheduling returns (input has its own debounce).
        Assert.Equal(1, fixture.Store.LiveFilterCalls);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Cancelled_scan_disposes_staging_rows_off_the_dispatcher_and_always_resets_busy_state(bool cleanupThrows) => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: false);
        var writer = new BlockingWriter(cleanupThrows);
        fixture.Store.WriterFactory = (_, _) => writer;
        var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.ScanOverride = async (_, token) =>
        {
            scanStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("A cancelled scan cannot publish.");
        };
        var dispatcherThread = Environment.CurrentManagedThreadId;
        var scanning = fixture.ViewModel.ScanCommand.ExecuteAsync(null);
        try
        {
            await scanStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.ViewModel.CancelCommand.Execute(null);
            await writer.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(dispatcherThread, writer.DisposalThread);
            var markerRan = false;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => markerRan = true);
            Assert.True(markerRan);
            Assert.True(fixture.ViewModel.IsBusy); // Cleanup is awaited, not abandoned.
        }
        finally { writer.ReleaseDisposal.TrySetResult(); }
        await scanning.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.False(fixture.ViewModel.IsScanning);
        Assert.False(fixture.ViewModel.IsLoadingExistingIndex);
        Assert.False(fixture.ViewModel.CancelCommand.CanExecute(null));
    });

    [Fact]
    public Task Cancelled_automatic_load_does_not_restart_when_the_same_page_is_reopened() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: true, ignoreCountCancellation: true);
        fixture.ViewModel.Activate();
        await fixture.Query.CountStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.ViewModel.CancelCommand.Execute(null);
        await WaitForAsync(() => !fixture.ViewModel.IsBusy);

        fixture.Query.ReleaseCount.TrySetResult(1);
        fixture.ViewModel.Deactivate();
        fixture.ViewModel.Activate();
        await Task.Delay(350);

        Assert.Equal(1, fixture.Store.FindCalls);
        Assert.Equal(1, fixture.Store.OpenQueryCalls);
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.True(fixture.ViewModel.HasLoadedItems);
        Assert.Equal(0, fixture.Service.ScanCalls);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Missing_or_empty_structured_data_asks_once_and_never_scans_without_consent(bool emptyCache) => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: emptyCache, cacheCount: 0,
            decision: CatalogMissingDataDecision.Later);
        fixture.ViewModel.Activate();
        await fixture.Prompt.Shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => !fixture.ViewModel.IsBusy);
        fixture.ViewModel.Deactivate();
        fixture.ViewModel.Activate();
        await Task.Delay(350);

        Assert.Equal(1, fixture.Prompt.Calls);
        Assert.True(fixture.Prompt.LastHasIndex);
        Assert.Equal("sample", fixture.Prompt.LastProfileName);
        Assert.Equal(0, fixture.Service.LoadCalls);
        Assert.Equal(0, fixture.Service.ScanCalls);
        Assert.Equal(0, fixture.Store.BeginCalls);
        Assert.False(fixture.ViewModel.HasLoadedItems);
    });

    [Theory]
    [InlineData(CatalogMissingDataDecision.Later, 0, 0)]
    [InlineData(CatalogMissingDataDecision.BuildFromIndex, 1, 0)]
    [InlineData(CatalogMissingDataDecision.Scan, 0, 1)]
    public Task Missing_data_dialog_choice_controls_the_expensive_operation(
        CatalogMissingDataDecision decision, int expectedLoads, int expectedScans) => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: false, decision: decision);
        await fixture.ViewModel.LoadCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, fixture.Prompt.Calls);
        Assert.Equal(expectedLoads, fixture.Service.LoadCalls);
        Assert.Equal(expectedScans, fixture.Service.ScanCalls);
        Assert.Equal(expectedLoads + expectedScans, fixture.Store.BeginCalls);
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.Equal(expectedLoads + expectedScans > 0, fixture.ViewModel.HasLoadedItems);
    });

    [Fact]
    public Task Cancellation_while_the_missing_data_dialog_is_open_exits_without_an_unhandled_error() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: false, waitForPromptCancellation: true);
        var load = fixture.ViewModel.LoadCommand.ExecuteAsync(null);
        await fixture.Prompt.Shown.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.ViewModel.CancelCommand.Execute(null);
        await load.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(fixture.Prompt.Token.IsCancellationRequested);
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.Equal(0, fixture.Service.LoadCalls);
        Assert.Equal(0, fixture.Service.ScanCalls);
    });

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, cancellation.Token);
    }

    private static Task OnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await action();
                    completion.TrySetResult();
                }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _databasePath = Path.GetTempFileName();

        public Fixture(bool cacheExists, bool ignoreCountCancellation = false, int cacheCount = 1,
            CatalogMissingDataDecision decision = CatalogMissingDataDecision.Later,
            bool waitForPromptCancellation = false)
        {
            Query = new Query(ignoreCountCancellation, cacheCount);
            Store = new Store(cacheExists, cacheCount, Query);
            Prompt = new Prompt(decision, waitForPromptCancellation);
            Service = new CatalogService();
            // The composition smoke test owns the process-wide Application.
            // Keep this fixture's callbacks on its own running STA dispatcher.
            ViewModel = new CatalogViewModel(Service, new Profiles(), new Workspace(_databasePath),
                new DatabaseResolver(_databasePath), null!, null!,
                new ApplicationOperationCoordinator(NullLogger<ApplicationOperationCoordinator>.Instance),
                new Notifier(), new Snackbar(), new Formats(), null!, null!, null!,
                new TestAppLocalizer(), NullLogger<CatalogViewModel>.Instance,
                projectionStore: Store, missingDataPrompt: Prompt, dispatcher: Dispatcher.CurrentDispatcher);
        }

        public CatalogViewModel ViewModel { get; }
        public Query Query { get; }
        public Store Store { get; }
        public Prompt Prompt { get; }
        public CatalogService Service { get; }

        public void Dispose()
        {
            Query.ReleaseCount.TrySetResult(1);
            ViewModel.Dispose();
            File.Delete(_databasePath);
        }
    }

    private sealed class SampleProfile : ILoadedProfile
    {
        public ProfileDescriptor Descriptor { get; } = new("sample", "1.0", "sample",
            ProfileCandidateKind.Directory,
            [new("client", "client", "Client", 0, false, ProfileFieldValueKind.String, true, null, null)], []);
        public ProfileMapResult Map(ProfilePathCandidate candidate) => ProfileMapResult.NoMatch();
    }

    private sealed class Profiles : IProfileCatalog
    {
        public ProfileCatalogSnapshot Current { get; } = new([new SampleProfile()], [], DateTimeOffset.UtcNow);
        public event EventHandler<ProfileCatalogChangedEventArgs>? Changed { add { } remove { } }
    }

    private sealed class Workspace(string databasePath) : IWorkspaceContext
    {
        public WorkspaceSnapshot Current { get; private set; } = new("sample", Path.GetTempPath(), databasePath);
        public event EventHandler<WorkspaceChangedEventArgs>? Changed { add { } remove { } }
        public Task SaveAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken = default)
        { Current = snapshot; return Task.CompletedTask; }
        public Task UpdateAsync(Func<WorkspaceSnapshot, WorkspaceSnapshot> update, CancellationToken cancellationToken = default)
        { Current = update(Current); return Task.CompletedTask; }
        public Task SetProfileDatabasePathAsync(string profileId, string? path, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class DatabaseResolver(string databasePath) : IIndexDatabasePathResolver
    {
        public string ResolveFileSearchDatabasePath() => databasePath;
        public string ResolveFileSearchDatabasePath(WorkspaceOptions options) => databasePath;
        public string ResolveFileSearchDatabasePath(WorkspaceSnapshot workspace) => databasePath;
        public string ResolveProfileDatabasePath(string profileId) => databasePath;
        public string ResolveProfileDatabasePath(string profileId, WorkspaceOptions options) => databasePath;
        public string ResolveProfileDatabasePath(string profileId, WorkspaceSnapshot workspace) => databasePath;
    }

    private sealed class Prompt(CatalogMissingDataDecision decision, bool waitForCancellation) : ICatalogMissingDataPrompt
    {
        public TaskCompletionSource Shown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public bool LastHasIndex { get; private set; }
        public string? LastProfileName { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<CatalogMissingDataDecision> ChooseAsync(string profileName, bool hasIndex, CancellationToken cancellationToken)
        {
            Calls++;
            Token = cancellationToken;
            LastProfileName = profileName;
            LastHasIndex = hasIndex;
            Shown.TrySetResult();
            return waitForCancellation ? WaitForCancellationAsync(cancellationToken) : Task.FromResult(decision);
        }

        private static async Task<CatalogMissingDataDecision> WaitForCancellationAsync(CancellationToken cancellationToken)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return CatalogMissingDataDecision.Later; }
    }

    private sealed class CatalogService : ICatalogService
    {
        public int LoadCalls { get; private set; }
        public int ScanCalls { get; private set; }
        public Func<CatalogRequest, CancellationToken, Task<CatalogResult>>? ScanOverride { get; set; }
        public Task<CatalogResult> LoadExistingAsync(CatalogRequest request,
            IProgress<CatalogOperationProgress>? progress = null, CancellationToken cancellationToken = default)
        { LoadCalls++; return Result(request); }
        public Task<CatalogResult> ScanAndLoadAsync(CatalogRequest request,
            IProgress<CatalogOperationProgress>? progress = null, CancellationToken cancellationToken = default)
        { ScanCalls++; return ScanOverride?.Invoke(request, cancellationToken) ?? Result(request); }
        public Task<CatalogResult> DiscoverAsync(CatalogRequest request,
            IProgress<CatalogOperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The missing-data dialog must never invoke legacy discovery.");
        private static Task<CatalogResult> Result(CatalogRequest request)
        {
            var row = new CatalogItem(Path.Combine(request.RootPath, "sample"), "sample", "rule",
                new object(), new Dictionary<string, object?> { ["client"] = "Example" }, false);
            request.ResultWriter!.Append(row);
            return Task.FromResult(new CatalogResult(new SampleProfile().Descriptor, [], [], 1, 0, false, null)
            { MatchedCount = 1 });
        }
    }

    private sealed class Store(bool cacheExists, int cacheCount, Query query) : ICatalogProjectionStore
    {
        public int FindCalls { get; private set; }
        public int OpenQueryCalls { get; private set; }
        public int BeginCalls { get; private set; }
        public int LiveFilterCalls { get; private set; }
        public Query? ReplacementQuery { get; set; }
        public Func<CatalogProjectionScope, IReadOnlyList<ProfileFieldDescriptor>, ICatalogProjectionWriter>? WriterFactory { get; set; }
        public Task<CatalogProjectionSnapshot?> FindAsync(CatalogProjectionScope scope, CancellationToken cancellationToken = default)
        {
            FindCalls++;
            return Task.FromResult<CatalogProjectionSnapshot?>(cacheExists
                ? new("cached", scope, new(cacheCount, cacheCount, 0, 0, 0, false, null, DateTimeOffset.UtcNow),
                    new SampleProfile().Descriptor.Fields) : null);
        }
        public ICatalogProjectionWriter Begin(CatalogProjectionScope scope, IReadOnlyList<ProfileFieldDescriptor> fields)
        { BeginCalls++; return WriterFactory?.Invoke(scope, fields) ?? new Writer(scope, fields); }
        public IResultQuerySession<CatalogProjectionRow> OpenQuery(CatalogProjectionSnapshot snapshot, CatalogProjectionQuery definition)
        {
            OpenQueryCalls++;
            var result = ReplacementQuery ?? query;
            result.Identity = snapshot.Generation;
            return result;
        }
        public IResultQuerySession<CatalogProjectionRow> OpenBuildingQuery(ICatalogProjectionWriter writer, CatalogProjectionScope scope,
            IReadOnlyList<ProfileFieldDescriptor> fields, CatalogProjectionQuery definition) => throw new NotSupportedException();
        public Task<IReadOnlySet<string>?> FindMatchingPathsAsync(CatalogProjectionSnapshot snapshot,
            CatalogProjectionQuery query, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        { LiveFilterCalls++; return Task.FromResult<IReadOnlySet<string>?>(new HashSet<string>(StringComparer.OrdinalIgnoreCase)); }
        public Task<CatalogProjectionSnapshot> RefreshColumnAsync(CatalogProjectionSnapshot snapshot, string fieldId,
            Func<string, CancellationToken, Task<TextColumnReadResult>> readValue,
            IProgress<TextColumnRefreshProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Writer(CatalogProjectionScope scope, IReadOnlyList<ProfileFieldDescriptor> fields) : ICatalogProjectionWriter
    {
        public string Generation { get; } = Guid.NewGuid().ToString("N");
        public int Count { get; private set; }
        public void Append(CatalogItem item) => Count++;
        public void Flush() { }
        public void Reset() => Count = 0;
        public CatalogProjectionSnapshot Publish(CatalogProjectionSummary summary, CatalogProjectionScope? publishedScope = null) =>
            new(Generation, publishedScope ?? scope, summary, fields);
        public void Dispose() { }
    }

    private sealed class BlockingWriter(bool cleanupThrows) : ICatalogProjectionWriter
    {
        public string Generation => "blocked-stage";
        public int Count => 0;
        public int DisposalThread { get; private set; }
        public TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Append(CatalogItem item) { }
        public void Flush() { }
        public void Reset() { }
        public CatalogProjectionSnapshot Publish(CatalogProjectionSummary summary, CatalogProjectionScope? publishedScope = null) => throw new NotSupportedException();
        public void Dispose()
        {
            DisposalThread = Environment.CurrentManagedThreadId;
            DisposalStarted.TrySetResult();
            ReleaseDisposal.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (cleanupThrows) throw new IOException("Simulated stage cleanup failure.");
        }
    }

    private sealed class IndexingSettings : IValidatedSettingsState<IndexingOptions>
    {
        public IndexingOptions Current { get; } = new();
    }

    private sealed class FileService : IFileSearchService
    {
        public FileQuery Query { get; set; } = new();
        public int OpenCalls { get; private set; }
        public int ScanCalls { get; private set; }
        public Task<IResultQuerySession<IndexedPathEntry>> OpenQueryAsync(FileSearchScope scope, EntrySearchRequest query, CancellationToken cancellationToken = default)
        { OpenCalls++; return Task.FromResult<IResultQuerySession<IndexedPathEntry>>(Query); }
        public Task<IndexRootStatus> GetStatusAsync(FileSearchScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(new IndexRootStatus(scope.RootPath, IndexRootAvailability.Available, null, null,
                IndexScanStatus.Completed, DateTimeOffset.UtcNow, 1, 0, false));
        public Task<IndexScanReport> ScanAsync(FileSearchScope scope, IProgress<IndexScanProgress>? progress = null, CancellationToken cancellationToken = default)
        { ScanCalls++; throw new NotSupportedException(); }
        public Task<IndexScanReport> RefreshScopeAsync(FileSearchScope scope, string refreshPath, IProgress<IndexScanProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EntrySearchResult> SearchAsync(FileSearchScope scope, EntrySearchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FileQuery : IResultQuerySession<IndexedPathEntry>
    {
        public string Identity => "file-count-fixture";
        public bool IsDisposed { get; private set; }
        public TaskCompletionSource CountStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<long> ReleaseCount { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<long> CountAsync(CancellationToken cancellationToken = default)
        { CountStarted.TrySetResult(); return ReleaseCount.Task; }
        public Task<ResultPage<IndexedPathEntry>> ReadRangeAsync(int offset, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResultPage<IndexedPathEntry>([new(Path.Combine(Path.GetTempPath(), "count-row.txt"), "count-row.txt", ".txt",
                IndexedPathKind.File, 12, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false)], offset, true));
        public async IAsyncEnumerable<IndexedPathEntry> EnumerateSelectionAsync(QuerySelection selection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
        public ValueTask DisposeAsync() { IsDisposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Query(bool ignoreCountCancellation, int cacheCount) : IResultQuerySession<CatalogProjectionRow>
    {
        public string Identity { get; set; } = "cached";
        public TaskCompletionSource CountStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<long> ReleaseCount { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken CountToken { get; private set; }
        public bool IsDisposed { get; private set; }
        public Task<long> CountAsync(CancellationToken cancellationToken = default)
        {
            CountToken = cancellationToken;
            CountStarted.TrySetResult();
            return ignoreCountCancellation ? ReleaseCount.Task : Task.FromResult((long)Math.Max(1, cacheCount));
        }
        public Task<ResultPage<CatalogProjectionRow>> ReadRangeAsync(int offset, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResultPage<CatalogProjectionRow>(cacheCount == 0 ? [] :
                [new(Path.Combine(Path.GetTempPath(), "sample"), "sample", "rule",
                    new Dictionary<string, object?> { ["client"] = "Example" }, false)], offset, ignoreCountCancellation));
        public async IAsyncEnumerable<CatalogProjectionRow> EnumerateSelectionAsync(QuerySelection selection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
        public ValueTask DisposeAsync() { IsDisposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Formats : ISelectionOutputFormatStore
    {
        public event EventHandler? Changed { add { } remove { } }
        public IReadOnlyList<SelectionOutputFormatDefinition> GetAll() => [];
        public IReadOnlyList<SelectionOutputFormatDefinition> GetApplicable(string? profileId) => [];
        public void Save(SelectionOutputFormatDefinition definition) => throw new NotSupportedException();
        public bool Delete(string id) => false;
    }

    private sealed class Notifier : IScanCompletionNotifier
    { public void Notify(ScanCompletionNotice notice) { } }

    private sealed class Snackbar : ISnackbarService
    {
        public TimeSpan DefaultTimeOut { get; set; }
        public void SetSnackbarPresenter(SnackbarPresenter contentPresenter) { }
        public SnackbarPresenter? GetSnackbarPresenter() => null;
        public void Show(string title, string message, ControlAppearance appearance, IconElement? icon, TimeSpan timeout) { }
    }
}
