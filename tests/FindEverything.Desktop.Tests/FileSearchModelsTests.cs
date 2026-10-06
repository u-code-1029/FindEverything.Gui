using FindEverything.Application.Indexing;
using FindEverything.Desktop.ViewModels;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class FileSearchModelsTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(1073741824, "1.0 GB")]
    public void File_size_formatter_uses_readable_binary_units(long bytes, string expected) =>
        Assert.Equal(expected, FileSizeFormatter.Format(bytes));

    [Fact]
    public void File_search_item_derives_kind_size_and_local_dates()
    {
        var created = new DateTimeOffset(2026, 10, 6, 1, 2, 3, TimeSpan.Zero);
        var modified = created.AddHours(1);
        var item = new FileSearchItemViewModel(new IndexedPathEntry(
            @"C:\Archive\report.pdf",
            "report.pdf",
            @"C:\Archive",
            IndexedPathKind.File,
            1024,
            created,
            modified,
            CoveragePending: true));

        Assert.Equal("PDF 파일", item.KindText);
        Assert.Equal("1.0 KB", item.SizeText);
        Assert.Equal(created.LocalDateTime, item.CreatedLocal);
        Assert.Equal(modified.LocalDateTime, item.ModifiedLocal);
        Assert.Equal("확인 대기", item.StatusText);
    }

    [Fact]
    public void Directory_size_is_not_claimed()
    {
        var now = DateTimeOffset.UtcNow;
        var item = new FileSearchItemViewModel(new IndexedPathEntry(
            @"C:\Archive",
            "Archive",
            @"C:\",
            IndexedPathKind.Directory,
            null,
            now,
            now,
            CoveragePending: false));

        Assert.Equal("폴더", item.KindText);
        Assert.Equal("—", item.SizeText);
        Assert.Equal(string.Empty, item.StatusText);
    }
}
