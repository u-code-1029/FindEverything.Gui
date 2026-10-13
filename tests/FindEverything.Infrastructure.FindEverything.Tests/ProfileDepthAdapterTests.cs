using FindEverything.Application.Indexing;
using FindEverything.Infrastructure.FindEverything;
using Xunit;

namespace FindEverything.Infrastructure.FindEverything.Tests;

public sealed class ProfileDepthAdapterTests
{
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)]
    public async Task Profile_depth_callback_prunes_without_engine_off_by_one_or_error(int limit)
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.SourcePath, "one", "two", "three"));
        var seen = new List<int>();
        await using var session = new FindEverythingIndexSessionFactory().Create(workspace.DatabasePath);
        var scan = await session.ScanAsync(new(workspace.SourcePath)
        {
            Options = FastOptions(limit),
            InspectDirectory = directory =>
            { seen.Add(directory.Depth); return directory.Depth >= limit ? DirectoryTraversalDecision.SkipDescendants : DirectoryTraversalDecision.Continue; },
        });
        Assert.Equal(IndexScanStatus.Completed, scan.Status); Assert.Empty(scan.Errors);
        Assert.Equal(Enumerable.Range(0, limit + 1), seen);
        var result = await session.SearchDirectoriesAsync(new(workspace.SourcePath));
        Assert.Equal(limit, result.Directories.Count);
    }
    [Fact]
    public async Task Partial_scope_depth_remains_relative_to_the_index_root()
    {
        using var workspace = new TestWorkspace(); var scope = Path.Combine(workspace.SourcePath, "one", "two");
        Directory.CreateDirectory(Path.Combine(scope, "three"));
        await using var session = new FindEverythingIndexSessionFactory().Create(workspace.DatabasePath);
        await session.ScanAsync(new(workspace.SourcePath) { Options = FastOptions(10) });
        var seen = new List<int>();
        var report = await session.ScanAsync(new(workspace.SourcePath)
        {
            ScopePath = scope, Options = FastOptions(2), InspectDirectory = directory =>
            { seen.Add(directory.Depth); return directory.Depth >= 2 ? DirectoryTraversalDecision.SkipDescendants : DirectoryTraversalDecision.Continue; },
        });
        Assert.Equal([2], seen); Assert.Empty(report.Errors); Assert.Equal(IndexScanStatus.Completed, report.Status);
    }
    private static IndexScanOptions FastOptions(int depth) => new()
    { MaxDepth = depth, MaxEntriesPerSecond = int.MaxValue, DirectoryDelay = TimeSpan.Zero };
}
