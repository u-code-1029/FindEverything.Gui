using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows.Data;
using System.Windows.Threading;
using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed partial class CatalogMissingDataAndCancellationTests
{
    [Fact]
    public Task Date_default_uses_column_order_and_typed_values_with_missing_dates_last() => OnStaAsync(() =>
    {
        using var fixture = new Fixture(cacheExists: false);
        var owner = fixture.ViewModel;
        owner.SelectedProfile = new ProfileChoiceViewModel(new DatePresentationProfile());
        Assert.True(owner.IsColumnSorted(CatalogColumnTarget.Field("first-date")));
        Assert.True(owner.IsColumnSortDescending);
        Assert.NotNull(owner.Items.CustomSort);

        var rows = (IList)owner.Items.SourceCollection;
        rows.Add(PresentationRow(owner, "older", new DateTime(2026, 1, 31)));
        rows.Add(PresentationRow(owner, "missing", null));
        rows.Add(PresentationRow(owner, "newer", new DateTime(2026, 2, 1)));
        owner.Items.Refresh();
        Assert.Equal(["newer", "older", "missing"], owner.Items.Cast<CatalogItemViewModel>().Select(row => row.RelativePath));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Explicit_sort_and_explicit_clear_are_not_replaced_when_a_live_view_is_recreated() => OnStaAsync(() =>
    {
        using var fixture = new Fixture(cacheExists: false);
        var owner = fixture.ViewModel;
        Assert.Null(owner.Items.CustomSort); // A profile with no date keeps ordinary ordering.
        owner.SelectedProfile = new ProfileChoiceViewModel(new DatePresentationProfile());
        owner.ApplySort("FullPath", EntrySortDirection.Ascending);
        var rows = new List<CatalogItemViewModel>
        {
            PresentationRow(owner, "a-older", new DateTime(2026, 1, 31)),
            PresentationRow(owner, "b-newer", new DateTime(2026, 2, 1)),
        };
        owner.Items = RecreatePresentationView(owner, rows);
        Assert.True(owner.IsColumnSorted(CatalogColumnTarget.FullPath));
        Assert.False(owner.IsColumnSortDescending);
        Assert.Equal("a-older", owner.Items.Cast<CatalogItemViewModel>().First().RelativePath);

        owner.ApplyColumnSort(CatalogColumnTarget.Field("first-date"), null);
        owner.Items = RecreatePresentationView(owner, rows);
        Assert.Null(owner.Items.CustomSort);
        Assert.False(owner.IsColumnSorted(CatalogColumnTarget.Field("first-date")));
        owner.SelectedProfile = new ProfileChoiceViewModel(new DatePresentationProfile());
        Assert.True(owner.IsColumnSorted(CatalogColumnTarget.Field("first-date")));
        Assert.True(owner.IsColumnSortDescending);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Cached_manual_and_automatic_loads_use_frozen_dates_and_keep_stored_scan_elapsed(bool automatic) => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: true);
        var owner = fixture.ViewModel;
        owner.SelectedProfile = new ProfileChoiceViewModel(new DatePresentationProfile());
        // Shared sources also query frozen fields, which can differ from the
        // profile that is currently installed on this machine.
        fixture.Store.CachedFields = [DatePresentationField("frozen-date", -1), .. owner.Fields];
        fixture.Store.CachedScanElapsed = TimeSpan.FromSeconds(12.34);
        var changes = new HashSet<string?>();
        owner.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        if (automatic)
        {
            owner.Activate();
            await WaitForAsync(() => fixture.Store.OpenQueryCalls > 0 && !owner.IsBusy);
            owner.Deactivate();
        }
        else await owner.LoadCommand.ExecuteAsync(null);

        Assert.Equal(CatalogColumnTarget.Field("frozen-date"), fixture.Store.LastQuery!.SortTarget);
        Assert.True(fixture.Store.LastQuery.Descending);
        Assert.Equal(TimeSpan.FromSeconds(12.34), owner.LastCompletedScanElapsed);
        Assert.True(owner.HasCompletedScanElapsed);
        Assert.Contains("12.34", owner.LastCompletedScanElapsedText, StringComparison.Ordinal);
        Assert.Contains(owner.LastCompletedScanElapsedText, owner.ResultSourceDescription, StringComparison.Ordinal);
        Assert.Contains(nameof(CatalogViewModel.LastCompletedScanElapsedText), changes);

        fixture.Store.ReplacementQuery = new Query(false, 1);
        owner.ApplySort("DisplayValues[frozen-date]", EntrySortDirection.Ascending);
        await WaitForAsync(() => fixture.Store.LastQuery is { Descending: false } && !owner.IsSearchPending && !owner.IsResultCountPending);
        fixture.Store.ReplacementQuery = new Query(false, 1);
        await owner.LoadCommand.ExecuteAsync(null);
        Assert.Equal(CatalogColumnTarget.Field("frozen-date"), fixture.Store.LastQuery!.SortTarget);
        Assert.False(fixture.Store.LastQuery.Descending); // Refresh never overwrites a user's sort.
        Assert.Equal(TimeSpan.FromSeconds(12.34), owner.LastCompletedScanElapsed);

        owner.StatusMessage = string.Empty; // A dismissed/transient notice is not the duration's owner.
        Assert.Contains("12.34", owner.LastCompletedScanElapsedText, StringComparison.Ordinal);
        owner.SelectedProfile = new ProfileChoiceViewModel(new SampleProfile());
        Assert.False(owner.HasCompletedScanElapsed);
        Assert.Empty(owner.LastCompletedScanElapsedText);
        Assert.False(owner.IsColumnSorted(CatalogColumnTarget.Field("frozen-date")));
    });

    [Fact]
    public Task Scan_publication_preserves_explicit_sort_and_saved_elapsed_through_a_later_failure() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: false);
        var owner = fixture.ViewModel;
        owner.SelectedProfile = new ProfileChoiceViewModel(new DatePresentationProfile());
        owner.ApplyColumnSort(CatalogColumnTarget.FullPath, false);
        await owner.ScanCommand.ExecuteAsync(null);
        Assert.Equal(CatalogColumnTarget.FullPath, fixture.Store.LastQuery!.SortTarget);
        Assert.False(fixture.Store.LastQuery.Descending);
        Assert.True(owner.HasCompletedScanElapsed); // Includes hosts with no engine elapsed report.
        var elapsed = owner.LastCompletedScanElapsed;
        Assert.True(elapsed >= TimeSpan.Zero);
        Assert.Equal(elapsed, fixture.Store.LastSnapshot!.Summary.ScanElapsed);
        var text = owner.LastCompletedScanElapsedText;

        fixture.Service.ScanOverride = (_, _) => throw new IOException("A later scan failed.");
        await owner.ScanCommand.ExecuteAsync(null);
        Assert.Equal(elapsed, owner.LastCompletedScanElapsed);
        Assert.Equal(text, owner.LastCompletedScanElapsedText);
        Assert.Equal(CatalogColumnTarget.FullPath, fixture.Store.LastQuery!.SortTarget);
    });

    [Fact]
    public Task Legacy_scan_sorts_dates_and_retains_elapsed_independently_of_notifications() => OnStaAsync(async () =>
    {
        var profile = new DatePresentationProfile();
        var service = new CatalogService
        {
            ScanOverride = (request, _) => Task.FromResult(new CatalogResult(profile.Descriptor,
                [PresentationItem("older", new DateTime(2026, 1, 31)), PresentationItem("newer", new DateTime(2026, 2, 1))],
                [], 2, 0, false, null) { MatchedCount = 2 }),
        };
        var database = Path.Combine(Path.GetTempPath(), "legacy-date-presentation-fixture.db");
        using var owner = new CatalogViewModel(service, new Profiles(), new Workspace(database), new DatabaseResolver(database),
            null!, null!, new ApplicationOperationCoordinator(NullLogger<ApplicationOperationCoordinator>.Instance),
            new Notifier(), new Snackbar(), new Formats(), null!, null!, null!, new TestAppLocalizer(),
            NullLogger<CatalogViewModel>.Instance, dispatcher: Dispatcher.CurrentDispatcher);
        owner.SelectedProfile = new ProfileChoiceViewModel(profile);
        await owner.ScanCommand.ExecuteAsync(null);
        Assert.Equal(["newer", "older"], owner.Items.Cast<CatalogItemViewModel>().Select(row => row.RelativePath));
        Assert.True(owner.HasCompletedScanElapsed);
        var text = owner.LastCompletedScanElapsedText;
        owner.StatusMessage = "different action";
        Assert.Equal(text, owner.LastCompletedScanElapsedText);
        Assert.Equal(text, owner.ResultSourceDescription);
        owner.SelectedProfile = new ProfileChoiceViewModel(new SampleProfile());
        Assert.Null(owner.LastCompletedScanElapsed);
    });

    private static ListCollectionView RecreatePresentationView(CatalogViewModel owner, IList rows) =>
        (ListCollectionView)typeof(CatalogViewModel).GetMethod("CreateItemsView", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(owner, [rows])!;

    private static CatalogItemViewModel PresentationRow(CatalogViewModel owner, string name, DateTime? date) =>
        new(PresentationItem(name, date), owner.Fields, new TestAppLocalizer());

    private static CatalogItem PresentationItem(string name, DateTime? date) => new(Path.Combine(Path.GetTempPath(), name), name,
        "rule", new object(), new Dictionary<string, object?> { ["first-date"] = date, ["later-date"] = new DateTime(2000, 1, 1) }, false);

    private static ProfileFieldDescriptor DatePresentationField(string id, int order) =>
        new(id, id, id, order, false, ProfileFieldValueKind.DateTime, true, "yyyyMMdd", "dd/MM/yyyy");

    private sealed class DatePresentationProfile : ILoadedProfile
    {
        public ProfileDescriptor Descriptor { get; } = new("sample", "1.0", "Date presentation", ProfileCandidateKind.Directory,
            [DatePresentationField("later-date", 20),
             new("client", "client", "Client", 0, false, ProfileFieldValueKind.String, true, null, null),
             DatePresentationField("first-date", 10)], []);
        public ProfileMapResult Map(ProfilePathCandidate candidate) => ProfileMapResult.NoMatch();
    }
}
