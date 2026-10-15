using FindEverything.Desktop.Filtering;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed partial class CatalogMissingDataAndCancellationTests
{
    [Fact]
    public void Search_input_uses_a_shared_700ms_delay() =>
        Assert.Equal(TimeSpan.FromMilliseconds(700), SearchInputTiming.DebounceDelay);

    [Fact]
    public Task Catalog_keyword_burst_opens_only_the_latest_query() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: true);
        await fixture.ViewModel.LoadCommand.ExecuteAsync(null);
        fixture.Store.ReplacementQuery = new Query(ignoreCountCancellation: false, cacheCount: 1);

        for (var index = 0; index < 40; index++) fixture.ViewModel.FilterText = $"keyword-{index}";

        Assert.Equal(1, fixture.Store.OpenQueryCalls);
        await WaitForAsync(() => fixture.Store.OpenQueryCalls > 1 && !fixture.ViewModel.IsResultCountPending);
        Assert.Equal(2, fixture.Store.OpenQueryCalls);
        Assert.Equal("keyword-39", fixture.Store.LastQuery!.Keyword);
        Assert.Equal("keyword-39", fixture.ViewModel.AppliedHighlightQuery);
    });

    [Fact]
    public Task Catalog_in_memory_keyword_burst_applies_only_after_debounce() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: false);
        for (var index = 0; index < 40; index++) fixture.ViewModel.FilterText = $"keyword-{index}";

        Assert.Equal(string.Empty, fixture.ViewModel.AppliedHighlightQuery);
        await WaitForAsync(() => fixture.ViewModel.AppliedHighlightQuery == "keyword-39");
        Assert.Equal(0, fixture.Store.OpenQueryCalls);
    });

    [Fact]
    public Task File_keyword_burst_opens_only_the_latest_query() => OnStaAsync(async () =>
    {
        var service = new FileService();
        service.Query.ReleaseCount.TrySetResult(1);
        using var owner = CreateFilesOwner(service);
        await owner.ActivateAsync();
        service.Query = new FileQuery();
        service.Query.ReleaseCount.TrySetResult(1);

        for (var index = 0; index < 40; index++) owner.SearchText = $"keyword-{index}";

        Assert.Equal(1, service.OpenCalls);
        await WaitForAsync(() => service.OpenCalls > 1 && !owner.IsSearching);
        Assert.Equal(2, service.OpenCalls);
        Assert.Equal("keyword-39", service.LastRequest!.Keyword);
    });
}
