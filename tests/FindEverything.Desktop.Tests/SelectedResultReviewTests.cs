using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using FindEverything.Application.Catalog;
using FindEverything.Application.FileSearch;
using FindEverything.Application.Indexing;
using FindEverything.Application.Results;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using FindEverything.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed partial class CatalogMissingDataAndCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Catalog_selected_review_ignores_filters_then_restores_them_without_losing_checks(bool selectAll) => OnStaAsync(async () =>
    {
        var directory = Directory.CreateTempSubdirectory("findeverything-review-").FullName;
        var database = Path.Combine(directory, "catalog.db");
        try
        {
            using var store = new SqliteCatalogProjectionStore(database);
            using var owner = new CatalogViewModel(new CatalogService(), new Profiles(), new Workspace(database),
                new DatabaseResolver(database), null!, null!,
                new ApplicationOperationCoordinator(NullLogger<ApplicationOperationCoordinator>.Instance),
                new Notifier(), new Snackbar(), new Formats(), null!, null!, null!, new TestAppLocalizer(),
                NullLogger<CatalogViewModel>.Instance, projectionStore: store, dispatcher: Dispatcher.CurrentDispatcher);
            var scope = new CatalogProjectionScope("review", "sample", directory, database, "review", "ko-KR", null);
            using var writer = store.Begin(scope, new SampleProfile().Descriptor.Fields);
            foreach (var name in new[] { "alpha-1", "alpha-2", "beta" })
                writer.Append(new(Path.Combine(directory, name), name, "rule", new object(), new Dictionary<string, object?> { ["client"] = name }, false));
            var snapshot = writer.Publish(new(3, 3, 0, 0, 0, false, null, DateTimeOffset.UtcNow));
            typeof(CatalogViewModel).GetField("_projectionSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, snapshot);
            // This fixture installs a published query directly, without the page's automatic load.
            typeof(CatalogViewModel).GetField("_isActive", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, true);
            owner.FilterText = "alpha"; owner.ApplySearchImmediately();
            await WaitForAsync(() => !owner.IsSearchPending && !owner.IsResultCountPending && owner.Items.Count == 2);
            if (selectAll) owner.SelectAllCommand.Execute(null);
            else
            {
                await owner.SelectRowAsync(owner.Items.GetItemAt(0), additive: true, range: false);
                owner.FilterText = "beta"; owner.ApplySearchImmediately();
                await WaitForAsync(() => !owner.IsSearchPending && !owner.IsResultCountPending && owner.Items.Count == 1);
                await owner.SelectRowAsync(owner.Items.GetItemAt(0), additive: true, range: false);
            }
            Assert.Equal(2, owner.SelectedCount);
            var originalText = owner.FilterText;
            await owner.ToggleSelectedOnlyCommand.ExecuteAsync(null);
            Assert.True(owner.IsShowingSelectedOnly);
            Assert.False(owner.CanEditResultFilters);
            Assert.Equal(2, owner.Items.Count);
            Assert.Equal(originalText, owner.FilterText);
            Assert.Equal(string.Empty, owner.AppliedHighlightQuery);
            var first = (CatalogItemViewModel)owner.Items.GetItemAt(0);
            await owner.SelectRowAsync(first, additive: true, range: false);
            owner.Deactivate();
            await Task.Delay(300);
            Assert.Equal(2, owner.Items.Count); // No hidden-page refresh while navigation is away.
            await owner.ActivateAsync();
            await WaitForAsync(() => !owner.IsSearchPending && owner.Items.Count == 1);
            Assert.Equal(1, owner.SelectedCount);
            await owner.ToggleSelectedOnlyCommand.ExecuteAsync(null);
            Assert.False(owner.IsShowingSelectedOnly);
            Assert.Equal(originalText, owner.FilterText);
            Assert.Equal(selectAll ? 2 : 1, owner.Items.Count);
            Assert.Equal(1, owner.SelectedCount);

            await owner.ToggleSelectedOnlyCommand.ExecuteAsync(null);
            owner.ClearSelectionCommand.Execute(null);
            await WaitForAsync(() => !owner.IsSearchPending && owner.Items.Count == 0);
            Assert.True(owner.IsShowingSelectedOnly);
            Assert.True(owner.CanShowSelectedOnly);
            await owner.ToggleSelectedOnlyCommand.ExecuteAsync(null);
            Assert.False(owner.IsShowingSelectedOnly);
            Assert.Equal(selectAll ? 2 : 1, owner.Items.Count);
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    [Fact]
    public Task Files_selected_review_keeps_cross_filter_checks_and_empty_review_can_be_closed() => OnStaAsync(async () =>
    {
        var service = new ReviewFileService();
        using var owner = CreateReviewFilesOwner(service);
        await owner.ActivateAsync();
        owner.SearchText = "alpha"; owner.ApplySearchImmediately();
        await WaitForAsync(() => !owner.IsSearching && !owner.IsResultCountPending && owner.Items.Count == 2);
        owner.SelectAllCommand.Execute(null);
        owner.SearchText = "beta"; owner.ApplySearchImmediately();
        await WaitForAsync(() => !owner.IsSearching && !owner.IsResultCountPending && owner.Items.Count == 1);
        await owner.SelectRowAsync(owner.Items[0], additive: true, range: false);
        Assert.Equal(3, owner.SelectedCount);
        await owner.ToggleSelectedOnlyCommand.ExecuteAsync(null);
        Assert.True(owner.IsShowingSelectedOnly);
        Assert.Equal(3, owner.Items.Count);
        Assert.Equal("beta", owner.SearchText);
        Assert.Null(service.LastRequest!.Keyword);
        await owner.SelectRowAsync(owner.Items[0], additive: true, range: false);
        await WaitForAsync(() => !owner.IsSearching && owner.Items.Count == 2);
        Assert.Equal(2, owner.SelectedCount);
        await owner.ToggleSelectedOnlyCommand.ExecuteAsync(null);
        Assert.Equal("beta", service.LastRequest!.Keyword);
        Assert.Single(owner.Items);
        Assert.Equal(2, owner.SelectedCount);
        await owner.ToggleSelectedOnlyCommand.ExecuteAsync(null);
        owner.ClearSelectionCommand.Execute(null);
        await WaitForAsync(() => !owner.IsSearching && owner.Items.Count == 0);
        Assert.True(owner.CanShowSelectedOnly);
        await owner.ToggleSelectedOnlyCommand.ExecuteAsync(null);
        Assert.False(owner.IsShowingSelectedOnly);
        Assert.Single(owner.Items);
    });

    [Fact]
    public Task Files_selected_review_defers_pending_refresh_until_the_page_is_active() => OnStaAsync(async () =>
    {
        var service = new ReviewFileService();
        using var owner = CreateReviewFilesOwner(service);
        await owner.ActivateAsync();
        owner.SelectAllCommand.Execute(null);
        await owner.ToggleSelectedOnlyCommand.ExecuteAsync(null);
        var calls = service.EnumerationCalls;
        await owner.SelectRowAsync(owner.Items[0], additive: true, range: false);
        owner.Deactivate();
        await Task.Delay(300);
        Assert.Equal(calls, service.EnumerationCalls);
        Assert.Equal(3, owner.Items.Count);
        await owner.ActivateAsync();
        Assert.Equal(2, owner.Items.Count);
        Assert.Equal(2, owner.SelectedCount);
        Assert.True(owner.IsShowingSelectedOnly);
        Assert.True(service.EnumerationCalls > calls);
    });

    [Fact]
    public Task Files_new_search_supersedes_a_slow_review_exit_without_reentering_review() => OnStaAsync(async () =>
    {
        var service = new ReviewFileService();
        using var owner = CreateReviewFilesOwner(service);
        await owner.ActivateAsync();
        await owner.SelectRowAsync(owner.Items[0], additive: true, range: false);
        await owner.ToggleSelectedOnlyCommand.ExecuteAsync(null);
        service.BlockNextOpen = true;
        var exit = owner.ToggleSelectedOnlyCommand.ExecuteAsync(null);
        await service.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        owner.SearchText = "beta";
        owner.ApplySearchImmediately();
        await exit.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => !owner.IsSearching && !owner.IsResultCountPending && owner.Items.Count == 1);
        Assert.False(owner.IsShowingSelectedOnly);
        Assert.Equal("beta", owner.SearchText);
        Assert.Equal("beta", owner.Items[0].Name);
        Assert.Equal(1, owner.SelectedCount);
    });

    [Fact]
    public Task Cancelled_files_review_preparation_keeps_the_previous_page_and_conditions() => OnStaAsync(async () =>
    {
        var service = new ReviewFileService();
        using var owner = CreateReviewFilesOwner(service);
        await owner.ActivateAsync();
        var first = owner.Items[0];
        await owner.SelectRowAsync(first, additive: true, range: false);
        service.BlockEnumeration = true;
        var preparing = owner.ToggleSelectedOnlyCommand.ExecuteAsync(null);
        await service.EnumerationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(owner.IsShowingSelectedOnly);
        Assert.False(owner.IsResultInteractionEnabled);
        Assert.True(owner.CancelCommand.CanExecute(null));
        owner.CancelCommand.Execute(null);
        await preparing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(owner.IsShowingSelectedOnly);
        Assert.Same(first, owner.Items[0]);
        Assert.True(first.IsSelected);
        Assert.Equal(1, owner.SelectedCount);
        Assert.True(owner.IsResultInteractionEnabled);
    });

    private static FileSearchViewModel CreateReviewFilesOwner(IFileSearchService service)
    {
        var database = Path.Combine(Path.GetTempPath(), "file-review-fixture.db");
        return new(service, new IndexingSettings(), new Workspace(database), new DatabaseResolver(database), null!, null!,
            new ApplicationOperationCoordinator(NullLogger<ApplicationOperationCoordinator>.Instance), new Notifier(), new Snackbar(),
            new Formats(), null!, null!, new Profiles(), null!, new TestAppLocalizer(), NullLogger<FileSearchViewModel>.Instance);
    }

    private sealed class ReviewFileService : IFileSearchService
    {
        private readonly Guid _scan = Guid.NewGuid();
        public int EnumerationCalls;
        public bool BlockEnumeration { get; set; }
        public bool BlockNextOpen { get; set; }
        public TaskCompletionSource OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource EnumerationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public EntrySearchRequest? LastRequest { get; private set; }
        public async Task<IResultQuerySession<IndexedPathEntry>> OpenQueryAsync(FileSearchScope scope, EntrySearchRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (BlockNextOpen)
            {
                BlockNextOpen = false;
                OpenStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            var rows = new[] { "alpha-1", "alpha-2", "beta" }.Where(name => string.IsNullOrEmpty(request.Keyword) || name.Contains(request.Keyword, StringComparison.OrdinalIgnoreCase))
                .Select(name => new IndexedPathEntry(Path.Combine(scope.RootPath, name), name, ".txt", IndexedPathKind.File, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false)).ToArray();
            if (request.SortDirection == EntrySortDirection.Descending) Array.Reverse(rows);
            return new ReviewFileQuery(rows, this, BlockEnumeration);
        }
        public Task<IndexRootStatus> GetStatusAsync(FileSearchScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(new IndexRootStatus(scope.RootPath, IndexRootAvailability.Available, _scan, null, IndexScanStatus.Completed, DateTimeOffset.UtcNow, 3, 0, false));
        public Task<IndexScanReport> ScanAsync(FileSearchScope scope, IProgress<IndexScanProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IndexScanReport> RefreshScopeAsync(FileSearchScope scope, string path, IProgress<IndexScanProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EntrySearchResult> SearchAsync(FileSearchScope scope, EntrySearchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ReviewFileQuery(IndexedPathEntry[] rows, ReviewFileService owner, bool blocked) : IResultQuerySession<IndexedPathEntry>
    {
        public string Identity => "review-fixture";
        public Task<long> CountAsync(CancellationToken cancellationToken = default) => Task.FromResult((long)rows.Length);
        public Task<ResultPage<IndexedPathEntry>> ReadRangeAsync(int offset, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResultPage<IndexedPathEntry>(rows.Skip(offset).Take(limit).ToArray(), offset, offset + limit < rows.Length));
        public async IAsyncEnumerable<IndexedPathEntry> EnumerateSelectionAsync(QuerySelection selection, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref owner.EnumerationCalls);
            if (blocked) { owner.EnumerationStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            var flags = await selection.ReadMembershipAsync(rows.Select(row => row.FullPath).ToArray(), cancellationToken);
            for (var i = 0; i < rows.Length; i++) if (flags[i]) yield return rows[i];
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
