using System.Text;
using System.IO;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class LiveFileOperationsTests
{
    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : System.IO.Path.GetTempPath(), "fe-live-test-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }
    private static ProfileTextFileFieldDescriptor Field(string? creation = null, long maximum = 4096) =>
        new(0, "note", "Note", @".*\.txt", ProfileRegexMatchMode.Full, true, 100, false, maximum)
        { EditOptions = new() { Enabled = true, CreationFileNameTemplate = creation } };

    [Fact]
    public async Task EmptyExistingFileIsEditableRatherThanMissing()
    {
        using var fixture = new Fixture(); var path = System.IO.Path.Combine(fixture.Path, "note.txt");
        await File.WriteAllTextAsync(path, string.Empty);
        var service = new TextFileEditingService();
        var document = await service.OpenAsync(fixture.Path, Field(), new(ProfileFieldResourceState.Present, "note.txt"), new Dictionary<string, object?>());
        Assert.False(document.IsCreation); Assert.Empty(document.Content);
        var result = await service.SaveAsync(document, "updated");
        Assert.Equal("updated", await File.ReadAllTextAsync(path)); Assert.Equal(ProfileFieldResourceState.Present, result.Resource.State);
        Assert.Empty(Directory.EnumerateFiles(fixture.Path, ".fe-edit-*.tmp"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EditPreservesUtf16BomAndCrLf(bool bigEndian)
    {
        using var fixture = new Fixture(); var path = System.IO.Path.Combine(fixture.Path, "note.txt");
        var encoding = new UnicodeEncoding(bigEndian, true, true);
        await File.WriteAllBytesAsync(path, encoding.GetPreamble().Concat(encoding.GetBytes("first\r\nsecond")).ToArray());
        var service = new TextFileEditingService();
        var document = await service.OpenAsync(fixture.Path, Field(), new(ProfileFieldResourceState.Present, "note.txt"), new Dictionary<string, object?>());
        Assert.True(document.HasBom); Assert.Equal("\r\n", document.NewLine);
        await service.SaveAsync(document, "changed\nnext");
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.AsSpan().StartsWith(encoding.GetPreamble()));
        Assert.Equal("changed\r\nnext", encoding.GetString(bytes.AsSpan(encoding.GetPreamble().Length)));
    }

    [Fact]
    public async Task ExternalChangesAreNotOverwritten()
    {
        using var fixture = new Fixture(); var path = System.IO.Path.Combine(fixture.Path, "note.txt");
        await File.WriteAllTextAsync(path, "before"); var service = new TextFileEditingService();
        var document = await service.OpenAsync(fixture.Path, Field(), new(ProfileFieldResourceState.Present, "note.txt"), new Dictionary<string, object?>());
        await File.WriteAllTextAsync(path, "external");
        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(document, "mine"));
        Assert.Equal("external", await File.ReadAllTextAsync(path)); Assert.Empty(Directory.EnumerateFiles(fixture.Path, ".fe-edit-*.tmp"));
    }

    [Fact]
    public async Task CreateUsesRawTypedFieldsAndDoesNotOverwriteCollision()
    {
        using var fixture = new Fixture(); var service = new TextFileEditingService();
        var values = new Dictionary<string, object?> { ["alias"] = "A", ["date"] = new DateTime(2026, 5, 21) };
        var document = await service.OpenAsync(fixture.Path, Field("{Field:alias}_{Field:date:yyyy-MM-dd}.txt"),
            new(ProfileFieldResourceState.Missing), values);
        Assert.Equal("A_2026-05-21.txt", System.IO.Path.GetFileName(document.FilePath));
        await File.WriteAllTextAsync(document.FilePath, "other");
        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(document, "mine"));
        Assert.Equal("other", await File.ReadAllTextAsync(document.FilePath));
    }

    [Fact]
    public async Task CreateWritesUtf8WithoutBomAndHonorsByteLimit()
    {
        using var fixture = new Fixture(); var service = new TextFileEditingService();
        var document = await service.OpenAsync(fixture.Path, Field("note.txt", maximum: 4), new(ProfileFieldResourceState.Missing), new Dictionary<string, object?>());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SaveAsync(document, "한국어"));
        Assert.False(File.Exists(document.FilePath));
        await service.SaveAsync(document, "ok"); Assert.Equal(new byte[] { (byte)'o', (byte)'k' }, await File.ReadAllBytesAsync(document.FilePath));
    }

    [Theory]
    [InlineData("../note.txt")]
    [InlineData("{Field:unknown}.txt")]
    [InlineData("{RandomGuid}/note.txt")]
    [InlineData("CON.txt")]
    [InlineData("trailing.txt.")]
    public void CreationRejectsUnsafeOrUnknownTokens(string template) =>
        Assert.Throws<InvalidDataException>(() => TextFileEditingService.FormatCreationName(template, new Dictionary<string, object?>()));

    [Fact]
    public void RandomNamesAreGeneratedOncePerDocument()
    {
        var first = TextFileEditingService.FormatCreationName("{RandomGuid}.txt", new Dictionary<string, object?>());
        var second = TextFileEditingService.FormatCreationName("{RandomGuid}.txt", new Dictionary<string, object?>());
        Assert.NotEqual(first, second); Assert.Equal(36, first.Length);
    }

    [Fact]
    public async Task CreationNameMustMatchReadPattern()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidDataException>(() => new TextFileEditingService().OpenAsync(fixture.Path,
            Field("note.csv"), new(ProfileFieldResourceState.Missing), new Dictionary<string, object?>()));
    }

    [Fact]
    public async Task UnknownResourceDoesNotGuessWhichFileToEdit()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TextFileEditingService().OpenAsync(fixture.Path,
            Field("note.txt"), new(ProfileFieldResourceState.Unknown), new Dictionary<string, object?>()));
    }

    [Fact]
    public async Task CachedMissingDoesNotCreateWhenAnotherMatchingFileAlreadyExists()
    {
        using var fixture = new Fixture(); var existing = System.IO.Path.Combine(fixture.Path, "other.txt");
        await File.WriteAllTextAsync(existing, "existing source");
        await Assert.ThrowsAsync<IOException>(() => new TextFileEditingService().OpenAsync(fixture.Path,
            Field("note.txt"), new(ProfileFieldResourceState.Missing), new Dictionary<string, object?>()));
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "note.txt")));
        Assert.Equal("existing source", await File.ReadAllTextAsync(existing));
    }

    [Fact]
    public async Task CachedPresentMustStillBeTheFirstRegexSelectedFileAtOpen()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(System.IO.Path.Combine(fixture.Path, "z.txt"), "cached source");
        await File.WriteAllTextAsync(System.IO.Path.Combine(fixture.Path, "a.txt"), "new source");
        await Assert.ThrowsAsync<IOException>(() => new TextFileEditingService().OpenAsync(fixture.Path,
            Field(), new(ProfileFieldResourceState.Present, "z.txt"), new Dictionary<string, object?>()));
        Assert.Equal("cached source", await File.ReadAllTextAsync(System.IO.Path.Combine(fixture.Path, "z.txt")));
    }

    [Fact]
    public async Task FileSelectedByProfileChangingDuringEditPreventsSave()
    {
        using var fixture = new Fixture(); var original = System.IO.Path.Combine(fixture.Path, "z.txt");
        await File.WriteAllTextAsync(original, "original source"); var service = new TextFileEditingService();
        var document = await service.OpenAsync(fixture.Path, Field(), new(ProfileFieldResourceState.Present, "z.txt"), new Dictionary<string, object?>());
        await File.WriteAllTextAsync(System.IO.Path.Combine(fixture.Path, "a.txt"), "new source");
        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(document, "unsaved draft"));
        Assert.Equal("original source", document.Content);
        Assert.Equal("original source", await File.ReadAllTextAsync(original));
        Assert.Empty(Directory.EnumerateFiles(fixture.Path, ".fe-edit-*.tmp"));
    }

    [Fact]
    public async Task MatchingFileAddedDuringCreationPreventsCreatingDifferentFile()
    {
        using var fixture = new Fixture(); var service = new TextFileEditingService();
        var document = await service.OpenAsync(fixture.Path, Field("note.txt"), new(ProfileFieldResourceState.Missing), new Dictionary<string, object?>());
        await File.WriteAllTextAsync(System.IO.Path.Combine(fixture.Path, "other.txt"), "new source");
        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(document, "unsaved draft"));
        Assert.False(File.Exists(document.FilePath));
        Assert.Equal("new source", await File.ReadAllTextAsync(System.IO.Path.Combine(fixture.Path, "other.txt")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BroadPatternExcludesOnlyItsOwnStageDuringFinalSelectionProbe(bool creation)
    {
        using var fixture = new Fixture(); var service = new TextFileEditingService();
        var field = Field("note.txt") with { FileNamePattern = ".*" };
        if (!creation) await File.WriteAllTextAsync(System.IO.Path.Combine(fixture.Path, "note.txt"), "before");
        var document = await service.OpenAsync(fixture.Path, field,
            creation ? new(ProfileFieldResourceState.Missing) : new(ProfileFieldResourceState.Present, "note.txt"), new Dictionary<string, object?>());
        await service.SaveAsync(document, "committed");
        Assert.Equal("committed", await File.ReadAllTextAsync(document.FilePath));
        Assert.Empty(Directory.EnumerateFiles(fixture.Path, ".fe-edit-*.tmp"));
    }

    [Fact]
    public async Task BroadPatternDoesNotIgnoreOtherWritersStageNamedFiles()
    {
        using var fixture = new Fixture(); var service = new TextFileEditingService();
        await File.WriteAllTextAsync(System.IO.Path.Combine(fixture.Path, "note.txt"), "before");
        var field = Field() with { FileNamePattern = ".*" };
        var document = await service.OpenAsync(fixture.Path, field, new(ProfileFieldResourceState.Present, "note.txt"), new Dictionary<string, object?>());
        await File.WriteAllTextAsync(System.IO.Path.Combine(fixture.Path, ".fe-edit-other.tmp"), "another source");
        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(document, "draft"));
        Assert.Equal("before", await File.ReadAllTextAsync(document.FilePath));
    }

    [Fact]
    public async Task LiveBrowsingIsTopLevelAndBounded()
    {
        using var fixture = new Fixture(); var nested = Directory.CreateDirectory(System.IO.Path.Combine(fixture.Path, "child"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(nested.FullName, "nested.txt"), "not top-level");
        for (var index = 0; index < 300; index++) await File.WriteAllTextAsync(System.IO.Path.Combine(fixture.Path, $"{index:000}.txt"), string.Empty);
        var service = new LiveDirectoryService();
        var first = await service.ReadPageAsync(fixture.Path);
        Assert.Equal(LiveDirectoryService.PageSize, first.Entries.Count); Assert.True(first.HasMore);
        var second = await service.ReadPageAsync(fixture.Path, first.NextOffset);
        Assert.False(second.HasMore); Assert.Equal(301, first.Entries.Count + second.Entries.Count);
        Assert.DoesNotContain(first.Entries.Concat(second.Entries), entry => entry.Name == "nested.txt");
        Assert.Equal(301, first.Entries.Concat(second.Entries).Select(entry => entry.FullPath).Distinct().Count());
    }

    [Fact]
    public async Task CancelledLiveReadDoesNotReturnLateEntries()
    {
        using var fixture = new Fixture(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LiveDirectoryService().ReadPageAsync(fixture.Path, cancellationToken: cancellation.Token));
    }
}
