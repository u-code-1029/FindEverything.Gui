using System.IO.Compression;
using System.IO;
using System.Text;
using System.Xml.Linq;
using FindEverything.Desktop.Services;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class CatalogSelectionExporterTests
{
    [Theory]
    [InlineData("FindEverything-catalog-20261009-120000.xlsx", "FindEverything-catalog-20261009-120000")]
    [InlineData("selected.CSV", "selected")]
    [InlineData("selected", "selected")]
    public void Catalog_export_suggestion_leaves_the_selected_filter_in_control_of_the_extension(
        string suggestion,
        string expected)
    {
        Assert.Equal(
            expected,
            DesktopPickerService.CreateCatalogExportSuggestion(
                suggestion,
                new DateTime(2026, 10, 9, 12, 0, 0)));
    }

    [Fact]
    public void Empty_catalog_export_suggestion_is_extensionless()
    {
        Assert.Equal(
            "FindEverything-catalog-20261009-120000",
            DesktopPickerService.CreateCatalogExportSuggestion(
                string.Empty,
                new DateTime(2026, 10, 9, 12, 0, 0)));
    }

    [Fact]
    public async Task Csv_export_is_excel_compatible_escaped_safe_and_atomic()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var destination = Path.Combine(temporaryDirectory.Path, "selected.csv");
        await File.WriteAllTextAsync(destination, "old");
        var exporter = new CatalogSelectionExporter(new TestAppLocalizer());
        var document = new CatalogSelectionExportDocument(
            ["Name", "Note"],
            new IReadOnlyList<string>[]
            {
                ["=2+2", "hello, \"world\"\r\nnext"],
                ["safe", "한글"],
            },
            RowCount: 2);

        await exporter.ExportAsync(
            destination,
            CatalogSelectionExportFormat.Csv,
            document);

        var bytes = await File.ReadAllBytesAsync(destination);
        Assert.True(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Equal(
            "Name,Note\r\n'=2+2,\"hello, \"\"world\"\"\r\nnext\"\r\nsafe,한글\r\n",
            await File.ReadAllTextAsync(destination, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, ".*.tmp"));
    }

    [Fact]
    public async Task Xlsx_export_writes_a_real_open_xml_workbook_with_inline_text_cells()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var destination = Path.Combine(temporaryDirectory.Path, "selected.xlsx");
        var exporter = new CatalogSelectionExporter(
            new TestAppLocalizer(
                "en-US",
                new Dictionary<string, string>
                {
                    ["Loc.Export.SheetName"] = "Structured results",
                }));
        var document = new CatalogSelectionExportDocument(
            ["상태", "고객", "폴더 경로"],
            new IReadOnlyList<string>[]
            {
                ["완료", "=2+2", @"C:\자료\Apollo"],
                ["대기 범위", "홍길동", @"\\server\share\folder"],
            },
            RowCount: 2);

        await exporter.ExportAsync(
            destination,
            CatalogSelectionExportFormat.Xlsx,
            document);

        await using var stream = File.OpenRead(destination);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry("[Content_Types].xml"));
        Assert.NotNull(archive.GetEntry("_rels/.rels"));
        var workbookEntry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("xl/workbook.xml"));
        Assert.NotNull(archive.GetEntry("xl/_rels/workbook.xml.rels"));
        await using (var workbookStream = workbookEntry.Open())
        {
            var workbook = await XDocument.LoadAsync(
                workbookStream,
                LoadOptions.None,
                CancellationToken.None);
            XNamespace workbookSpreadsheet =
                "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            Assert.Equal(
                "Structured results",
                (string?)Assert.Single(workbook.Descendants(workbookSpreadsheet + "sheet"))
                    .Attribute("name"));
        }
        var worksheetEntry = Assert.IsType<ZipArchiveEntry>(
            archive.GetEntry("xl/worksheets/sheet1.xml"));
        await using var worksheetStream = worksheetEntry.Open();
        var worksheet = await XDocument.LoadAsync(
            worksheetStream,
            LoadOptions.None,
            CancellationToken.None);
        XNamespace spreadsheet =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var cells = worksheet.Descendants(spreadsheet + "c").ToArray();
        Assert.All(cells, cell => Assert.Equal("inlineStr", (string?)cell.Attribute("t")));
        Assert.Equal(
            new[]
            {
                "상태", "고객", "폴더 경로",
                "완료", "=2+2", @"C:\자료\Apollo",
                "대기 범위", "홍길동", @"\\server\share\folder",
            },
            worksheet.Descendants(spreadsheet + "t").Select(static value => value.Value));
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, ".*.tmp"));
    }

    [Fact]
    public async Task Xlsx_export_replaces_invalid_xml_text_and_preserves_following_emoji()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var destination = Path.Combine(temporaryDirectory.Path, "selected.xlsx");
        var exporter = new CatalogSelectionExporter(new TestAppLocalizer());
        var document = new CatalogSelectionExportDocument(
            ["Value"],
            new IReadOnlyList<string>[] { ["before\u0001🙂after"] },
            RowCount: 1);

        await exporter.ExportAsync(
            destination,
            CatalogSelectionExportFormat.Xlsx,
            document);

        await using var stream = File.OpenRead(destination);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var worksheetEntry = Assert.IsType<ZipArchiveEntry>(
            archive.GetEntry("xl/worksheets/sheet1.xml"));
        await using var worksheetStream = worksheetEntry.Open();
        var worksheet = await XDocument.LoadAsync(
            worksheetStream,
            LoadOptions.None,
            CancellationToken.None);
        XNamespace spreadsheet =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        Assert.Equal(
            "before\uFFFD🙂after",
            worksheet.Descendants(spreadsheet + "t").Last().Value);
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, ".*.tmp"));
    }

    [Fact]
    public async Task Cancellation_does_not_replace_an_existing_destination()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var destination = Path.Combine(temporaryDirectory.Path, "selected.csv");
        await File.WriteAllTextAsync(destination, "keep");
        var exporter = new CatalogSelectionExporter(new TestAppLocalizer());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.ExportAsync(
            destination,
            CatalogSelectionExportFormat.Csv,
            new CatalogSelectionExportDocument(
                ["Value"],
                new IReadOnlyList<string>[] { ["replacement"] },
                RowCount: 1),
            cancellation.Token));

        Assert.Equal("keep", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, ".*.tmp"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"FindEverything-export-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
