using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using FindEverything.Application.Catalog;
using FindEverything.Application.Options;
using FindEverything.Application.Results;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Results;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class CatalogRowSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_row_and_checkbox_gestures_toggle_without_clearing_other_checks(bool persisted)
    {
        using var fixture = new SelectionFixture(persisted);
        var vm = fixture.ViewModel;

        await vm.SelectRowAsync(fixture.Rows[0], additive: true, range: false);
        await vm.SelectRowAsync(fixture.Rows[1], additive: true, range: false);
        Assert.Equal(2, vm.SelectedCount);
        Assert.True(fixture.Rows[0].IsSelected);
        Assert.True(fixture.Rows[1].IsSelected);

        await vm.SelectRowAsync(fixture.Rows[0], additive: true, range: false);
        Assert.Equal(1, vm.SelectedCount);
        Assert.False(fixture.Rows[0].IsSelected);
        Assert.True(fixture.Rows[1].IsSelected);
        Assert.Same(fixture.Rows[0], vm.SelectedItem);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shift_adds_the_anchor_range_and_keeps_previously_checked_rows(bool persisted)
    {
        using var fixture = new SelectionFixture(persisted);
        var vm = fixture.ViewModel;
        await vm.SelectRowAsync(fixture.Rows[0], additive: true, range: false);
        await vm.SelectRowAsync(fixture.Rows[2], additive: true, range: false);
        await vm.SelectRowAsync(fixture.Rows[4], additive: true, range: true);

        Assert.Equal(4, vm.SelectedCount);
        Assert.Equal(new[] { true, false, true, true, true }, fixture.Rows.Select(row => row.IsSelected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Header_inversion_and_toolbar_commands_update_every_loaded_checkbox(bool persisted)
    {
        using var fixture = new SelectionFixture(persisted);
        var vm = fixture.ViewModel;
        await vm.SelectRowAsync(fixture.Rows[0], additive: true, range: false);

        vm.InvertSelectionCommand.Execute(null);
        Assert.Equal(4, vm.SelectedCount);
        Assert.False(fixture.Rows[0].IsSelected);
        Assert.All(fixture.Rows.Skip(1), row => Assert.True(row.IsSelected));

        vm.SelectAllCommand.Execute(null);
        Assert.Equal(5, vm.SelectedCount);
        Assert.All(fixture.Rows, row => Assert.True(row.IsSelected));

        vm.ClearSelectionCommand.Execute(null);
        Assert.Equal(0, vm.SelectedCount);
        Assert.All(fixture.Rows, row => Assert.False(row.IsSelected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Header_selects_all_from_partial_state_and_clears_all_from_checked_state(bool persisted)
    {
        using var fixture = new SelectionFixture(persisted);
        var vm = fixture.ViewModel;
        await vm.SelectRowAsync(fixture.Rows[0], additive: true, range: false);
        Assert.Null(vm.CurrentResultsSelectionState);

        vm.ToggleCurrentResultsSelectionCommand.Execute(null);
        Assert.Equal(5, vm.SelectedCount);
        Assert.True(vm.CurrentResultsSelectionState);
        Assert.All(fixture.Rows, row => Assert.True(row.IsSelected));

        vm.ToggleCurrentResultsSelectionCommand.Execute(null);
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.CurrentResultsSelectionState);
        Assert.All(fixture.Rows, row => Assert.False(row.IsSelected));
    }

    [Fact]
    public void Live_preview_header_clears_checked_rows_even_if_a_previous_database_count_was_incomplete()
    {
        using var fixture = new SelectionFixture(persisted: false);
        typeof(CatalogViewModel).GetField("_hasExactResultCount", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fixture.ViewModel, false);
        var vm = fixture.ViewModel;
        vm.ToggleCurrentResultsSelectionCommand.Execute(null);
        Assert.Equal(5, vm.SelectedCount);
        Assert.True(vm.CurrentResultsSelectionState);

        vm.ToggleCurrentResultsSelectionCommand.Execute(null);
        Assert.Equal(0, vm.SelectedCount);
        Assert.All(fixture.Rows, row => Assert.False(row.IsSelected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Placeholder_rows_are_never_selected(bool persisted)
    {
        using var fixture = new SelectionFixture(persisted);
        var placeholder = CreateRow(99, placeholder: true);
        await fixture.ViewModel.SelectRowAsync(placeholder, additive: true, range: false);
        Assert.Equal(0, fixture.ViewModel.SelectedCount);
        Assert.False(placeholder.IsSelected);
    }

    [Fact]
    public async Task Single_persisted_toggle_does_not_resynchronize_unrelated_cached_rows()
    {
        using var fixture = new SelectionFixture(persisted: true);
        // Page adoption and bulk commands own cache reconciliation. A single
        // gesture must not read/rewrite every other row in the bounded cache.
        fixture.Rows[4].IsSelected = true;
        await fixture.ViewModel.SelectRowAsync(fixture.Rows[0], additive: true, range: false);
        Assert.True(fixture.Rows[0].IsSelected);
        Assert.True(fixture.Rows[4].IsSelected);
        Assert.Equal(1, fixture.ViewModel.SelectedCount);
        fixture.ViewModel.ClearSelectionCommand.Execute(null);
        Assert.False(fixture.Rows[4].IsSelected);
    }

    [Fact]
    public void Copy_selection_model_does_not_depend_on_native_grid_focus_selection()
    {
        using var fixture = new SelectionFixture(persisted: false);
        var vm = fixture.ViewModel;
        vm.SetSelection([fixture.Rows[0], fixture.Rows[2]]);
        vm.SelectedItem = fixture.Rows[4];

        Assert.Equal(2, vm.SelectedCount);
        Assert.True(fixture.Rows[0].IsSelected);
        Assert.True(fixture.Rows[2].IsSelected);
        Assert.False(fixture.Rows[4].IsSelected);

        vm.SetSelection([fixture.Rows[1], fixture.Rows[3]]);
        Assert.Equal(new[] { false, true, false, true, false }, fixture.Rows.Select(row => row.IsSelected));
    }

    [Fact]
    public void Dragging_and_second_double_click_are_not_checkbox_toggle_gestures()
    {
        var origin = new Point(50, 50);
        Assert.True(ResultGridInput.IsClickGesture(origin, origin, clickCount: 1));
        Assert.False(ResultGridInput.IsClickGesture(origin, origin, clickCount: 2));
        Assert.False(ResultGridInput.IsClickGesture(origin,
            new Point(origin.X + SystemParameters.MinimumHorizontalDragDistance, origin.Y), clickCount: 1));
        Assert.False(ResultGridInput.IsClickGesture(origin,
            new Point(origin.X, origin.Y + SystemParameters.MinimumVerticalDragDistance), clickCount: 1));
    }

    private static CatalogItemViewModel CreateRow(int index, bool placeholder = false) => new(
        new CatalogItem($@"C:\catalog\Item-{index:D4}", $"Item-{index:D4}", "rule",
            new object(), new Dictionary<string, object?>(), false), [], new TestAppLocalizer())
        { IsPlaceholder = placeholder };

    private sealed class SelectionFixture : IDisposable
    {
        private readonly VirtualResultCollection<CatalogItemViewModel>? _virtualRows;
        public CatalogItemViewModel[] Rows { get; } = Enumerable.Range(0, 5).Select(index => CreateRow(index)).ToArray();
        public CatalogViewModel ViewModel { get; }

        public SelectionFixture(bool persisted)
        {
            ViewModel = new CatalogViewModel(null!, new Catalog(), new Workspace(), new DatabaseResolver(),
                null!, null!, new ApplicationOperationCoordinator(NullLogger<ApplicationOperationCoordinator>.Instance), null!, null!, new Formats(),
                null!, null!, null!, new TestAppLocalizer(), NullLogger<CatalogViewModel>.Instance);
            if (persisted)
            {
                var query = new Query(Rows);
                _virtualRows = new VirtualResultCollection<CatalogItemViewModel>(
                    (offset, limit, token) => Task.FromResult(new ResultPage<CatalogItemViewModel>(
                        Rows.Skip(offset).Take(limit).ToArray(), offset, false)),
                    index => CreateRow(index, placeholder: true), action => action(), _ => { });
                _virtualRows.Seed(new ResultPage<CatalogItemViewModel>(Rows, 0, false), Rows.Length);
                SetField("_projectionQuery", query);
                SetField("_virtualItems", _virtualRows);
                SetField("_matchingCount", (long)Rows.Length);
                ViewModel.Items = new ListCollectionView(_virtualRows);
            }
            else ViewModel.Items = new ListCollectionView(Rows.ToList());
        }

        private void SetField(string name, object value) => typeof(CatalogViewModel)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ViewModel, value);
        public void Dispose() { ViewModel.Dispose(); _virtualRows?.Dispose(); }
    }

    private sealed class Query(IReadOnlyList<CatalogItemViewModel> rows) : IResultQuerySession<CatalogProjectionRow>
    {
        public string Identity => "selection-test";
        public Task<long> CountAsync(CancellationToken cancellationToken = default) => Task.FromResult((long)rows.Count);
        public Task<ResultPage<CatalogProjectionRow>> ReadRangeAsync(int offset, int limit,
            CancellationToken cancellationToken = default) => Task.FromResult(new ResultPage<CatalogProjectionRow>(
                rows.Skip(offset).Take(limit).Select(row => new CatalogProjectionRow(row.FullPath, row.RelativePath,
                    row.MatchedRuleId, row.Values, row.CoveragePending)).ToArray(), offset, offset + limit < rows.Count));
        public IAsyncEnumerable<CatalogProjectionRow> EnumerateSelectionAsync(QuerySelection selection,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Profile : ILoadedProfile
    {
        public ProfileDescriptor Descriptor { get; } = new("selection-test", "1.0", "Selection", ProfileCandidateKind.Directory, [], []);
        public ProfileMapResult Map(ProfilePathCandidate candidate) => ProfileMapResult.NoMatch();
    }
    private sealed class Catalog : IProfileCatalog
    {
        public ProfileCatalogSnapshot Current { get; } = new([new Profile()], [], DateTimeOffset.UtcNow);
        public event EventHandler<ProfileCatalogChangedEventArgs>? Changed { add { } remove { } }
    }
    private sealed class Workspace : IWorkspaceContext
    {
        public WorkspaceSnapshot Current { get; } = new("selection-test", Path.GetTempPath(), null);
        public event EventHandler<WorkspaceChangedEventArgs>? Changed { add { } remove { } }
        public Task SaveAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Func<WorkspaceSnapshot, WorkspaceSnapshot> update, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetProfileDatabasePathAsync(string profileId, string? databasePath,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class DatabaseResolver : IIndexDatabasePathResolver
    {
        private static string Database => Path.Combine(Path.GetTempPath(), "catalog-row-selection-test.db");
        public string ResolveFileSearchDatabasePath() => Database;
        public string ResolveFileSearchDatabasePath(WorkspaceOptions options) => Database;
        public string ResolveFileSearchDatabasePath(WorkspaceSnapshot workspace) => Database;
        public string ResolveProfileDatabasePath(string profileId) => Database;
        public string ResolveProfileDatabasePath(string profileId, WorkspaceOptions options) => Database;
        public string ResolveProfileDatabasePath(string profileId, WorkspaceSnapshot workspace) => Database;
    }
    private sealed class Formats : ISelectionOutputFormatStore
    {
        public event EventHandler? Changed { add { } remove { } }
        public IReadOnlyList<SelectionOutputFormatDefinition> GetAll() => [];
        public IReadOnlyList<SelectionOutputFormatDefinition> GetApplicable(string? profileId) => [];
        public void Save(SelectionOutputFormatDefinition definition) => throw new NotSupportedException();
        public bool Delete(string id) => false;
    }
}
