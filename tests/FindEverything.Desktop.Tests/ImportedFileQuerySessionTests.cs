using System.Runtime.CompilerServices;
using System.IO;
using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Application.Results;
using FindEverything.Application.Sharing;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ImportedFileQuerySessionTests
{
    [Fact]
    public async Task Loaded_and_selected_paths_keep_original_identity_without_loading_the_whole_dataset()
    {
        var originalRoot = Path.Combine(Path.GetTempPath(), "original");
        var linkedRoot = Path.Combine(Path.GetTempPath(), "linked");
        var original = Path.Combine(originalRoot, "item.txt");
        var effective = Path.Combine(linkedRoot, "item.txt");
        var source = new ImportedWorkspaceSource("source", "profile", "Shared", originalRoot, linkedRoot, "local", "metadata.db",
            null, null, 1, false, false, DateTimeOffset.UtcNow, 2, new ProfileManifest());
        var inner = new RecordingQuery(new(effective, "item.txt", linkedRoot, IndexedPathKind.File, 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false));
        await using var query = new ImportedFileQuerySession(inner, source);
        var page = await query.ReadRangeAsync(0, 100);
        Assert.Equal(original, page.Items[0].OriginalFullPath);
        Assert.Equal(original.ToUpperInvariant(), new FileSearchItemViewModel(page.Items[0]).SelectionKey);
        Assert.StartsWith("source:2:", query.Identity);
        var selection = new QuerySelection(true, [original.ToUpperInvariant()]);
        await foreach (var _ in query.EnumerateSelectionAsync(selection)) { }
        Assert.True(inner.Selection!.AllMatching);
        Assert.Single(inner.Selection.Keys);
        Assert.Equal(effective.ToUpperInvariant(), inner.Selection.Keys.Single().ToUpperInvariant());
        Assert.Equal(1, inner.PageCalls);
        Assert.Equal(0, inner.CountCalls);
    }

    private sealed class RecordingQuery(IndexedPathEntry item) : IResultQuerySession<IndexedPathEntry>
    {
        public string Identity => "generation";
        public int PageCalls { get; private set; }
        public int CountCalls { get; private set; }
        public QuerySelection? Selection { get; private set; }
        public Task<long> CountAsync(CancellationToken cancellationToken = default) { CountCalls++; return Task.FromResult(1L); }
        public Task<ResultPage<IndexedPathEntry>> ReadRangeAsync(int offset, int limit, CancellationToken cancellationToken = default)
        { PageCalls++; return Task.FromResult(new ResultPage<IndexedPathEntry>([item], offset, false)); }
        public async IAsyncEnumerable<IndexedPathEntry> EnumerateSelectionAsync(QuerySelection selection, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { Selection = selection.Capture(); await Task.CompletedTask; yield return item; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
