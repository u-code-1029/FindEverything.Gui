using System.Globalization;
using System.Text;
using System.Xml;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FindEverything.Application.Catalog;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class SelectionImportReaderTests
{
    [Fact]
    public async Task CsvTsvHeadersAndQuotedMultilineCellsAreMappedWithoutLosingText()
    {
        var reader = new SelectionImportReader();
        var rule = TableRule();
        using var csv = await reader.ReadTextAsync("Name,Note\r\nAlice,\"hello, world\r\nnext \"\"line\"\"\"\r\nBob,plain", rule);
        var rows = await Collect(csv);
        Assert.Equal(2, rows.Count); Assert.Equal(new[] { "Alice", "hello, world\r\nnext \"line\"" }, rows[0].Values);
        using var tsv = await reader.ReadTextAsync("Name\tNote\nAlice\tone\nBob\ttwo", rule);
        Assert.Equal(new[] { "Bob", "two" }, (await Collect(tsv))[1].Values);
    }

    [Fact]
    public async Task NamedHeadersMustBePresentAndUnambiguousAndMalformedQuotesAreRejected()
    {
        var reader = new SelectionImportReader(); var rule = TableRule();
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadTextAsync("Wrong,Note\nAlice,one", rule));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadTextAsync("Name,Name,Note\nA,B,one", rule));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadTextAsync("Name,Note\nAlice,\"unterminated", rule));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadTextAsync("Name,Note\nAlice,\"quoted\"oops", rule));
    }

    [Fact]
    public async Task HtmlUsesChosenTableWithoutExecutingScriptsOrLoadingResources()
    {
        var html = "<script>throw new Error('do not execute')</script><img src='https://invalid.example/never-load'>"
            + "<table><tr><th>Name</th><th>Note</th></tr><tr><td>ignored</td><td>first</td></tr></table>"
            + "<table><tr><th>Name</th><th>Note</th></tr><tr><td>Alice<script>bad()</script></td><td>A &amp; B</td></tr></table>";
        using var document = await new SelectionImportReader().ReadTextAsync(html, TableRule(), true, 1);
        Assert.Equal(new[] { "Alice", "A & B" }, Assert.Single(await Collect(document)).Values);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SelectionImportReader().ReadTextAsync("<table><tr><td colspan='2'>merged</td></tr></table>", TableRule(), true));
    }

    [Fact]
    public async Task TextLinesAndMultilineRegexMapCapturesAndLeaveMissingGroupsNull()
    {
        var reader = new SelectionImportReader();
        var lineRule = new ProfileSelectionMatchRuleManifest { Id = "line", DisplayName = "Line", Columns = [new() { FieldId = "name" }] };
        using var lines = await reader.ReadTextAsync(" Alice \r\n\r\nBob\n", lineRule);
        Assert.Equal(new[] { " Alice " }, (await Collect(lines))[0].Values); Assert.Equal(2, lines.RowCount);
        var regexRule = new ProfileSelectionMatchRuleManifest
        {
            Id = "regex", DisplayName = "Regex", InputKind = ProfileSelectionInputKind.Regex,
            RegexPattern = @"(?<name>\w+):\s*(?<amount>\d+)(?:\r?\nNote=(?<note>[^\r\n]+))?",
            Columns = [new() { FieldId = "name", RegexGroup = "name" }, new() { FieldId = "amount", RegexGroup = "amount" }, new() { FieldId = "note", RegexGroup = "note" }],
        };
        using var regex = await reader.ReadTextAsync("Alice: 12\nNote=hello\nBob: 34", regexRule);
        var rows = await Collect(regex);
        Assert.Equal(new[] { "Alice", "12", "hello" }, rows[0].Values); Assert.Null(rows[1].Values[2]);
    }

    [Fact]
    public async Task ExcelStreamsSharedStringsSparseCellsBooleanDatesAndCachedFormulaValues()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "selection.xlsx");
        var date = new DateTime(2026, 5, 21);
        using (var package = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbook = package.AddWorkbookPart(); workbook.Workbook = new Workbook();
            var part = workbook.AddNewPart<WorksheetPart>();
            var data = new SheetData();
            part.Worksheet = new Worksheet(data);
            var shared = workbook.AddNewPart<SharedStringTablePart>();
            shared.SharedStringTable = new SharedStringTable(new SharedStringItem(new Text("Alice")));
            var styles = workbook.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = new Stylesheet(new CellFormats(new CellFormat(), new CellFormat { NumberFormatId = 14, ApplyNumberFormat = true }));
            var sheets = workbook.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet { Name = "Data", SheetId = 1, Id = workbook.GetIdOfPart(part) });
            data.Append(new Row(Inline("A1", "Name"), Inline("B1", "Date"), Inline("C1", "Flag"), Inline("E1", "Amount")) { RowIndex = 1 });
            data.Append(new Row(new Cell { CellReference = "A2", DataType = CellValues.SharedString, CellValue = new CellValue("0") },
                new Cell { CellReference = "B2", StyleIndex = 1, CellValue = new CellValue(date.ToOADate().ToString(CultureInfo.InvariantCulture)) },
                new Cell { CellReference = "C2", DataType = CellValues.Boolean, CellValue = new CellValue("1") },
                new Cell { CellReference = "E2", CellFormula = new CellFormula("2+3"), CellValue = new CellValue("5") }) { RowIndex = 2 });
            workbook.Workbook.Save(); part.Worksheet.Save(); shared.SharedStringTable.Save(); styles.Stylesheet.Save();
        }
        Assert.Equal(new[] { "Data" }, SelectionImportReader.ReadSheetNames(path));
        var rule = new ProfileSelectionMatchRuleManifest { Id = "excel", DisplayName = "Excel", InputKind = ProfileSelectionInputKind.Table,
            Columns = [new() { FieldId = "name", InputHeader = "Name" }, new() { FieldId = "date", InputHeader = "Date" }, new() { FieldId = "flag", InputHeader = "Flag" }, new() { FieldId = "amount", InputHeader = "Amount" }] };
        using var document = await new SelectionImportReader().ReadFileAsync(path, rule, "Data");
        var row = Assert.Single(await Collect(document));
        Assert.Equal(new[] { "Alice", date.ToString("O", CultureInfo.InvariantCulture), "true", "5" }, row.Values);
    }

    [Fact]
    public async Task PreviewIsBoundedWhileDiskBackedEnumerationKeepsFullInput()
    {
        var rule = new ProfileSelectionMatchRuleManifest { Id = "line", DisplayName = "Line", Columns = [new() { FieldId = "name" }] };
        var text = string.Join('\n', Enumerable.Repeat(new string('x', 400), 1001));
        using var document = await new SelectionImportReader().ReadTextAsync(text, rule);
        Assert.Equal(100, document.PreviewRows.Count); Assert.All(document.PreviewRows, row => Assert.Equal(257, row.Values[0]!.Length));
        var rows = await Collect(document); Assert.Equal(1001, rows.Count); Assert.All(rows, row => Assert.Equal(400, row.Values[0]!.Length));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SelectionImportReader().ReadTextAsync(text, rule, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task SourceRowBudgetIncludesUnmappedCsvColumnsBeforeMapping()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "wide.csv");
        using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            writer.WriteLine("Name,Unused1,Unused2,Unused3,Unused4,Unused5");
            writer.Write("Alice");
            var buffer = new string('x', 8192);
            for (var column = 0; column < 5; column++)
            { writer.Write(','); for (var chunk = 0; chunk < 128; chunk++) writer.Write(buffer); }
        }
        var rule = NameOnlyTableRule();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new SelectionImportReader().ReadFileAsync(path, rule));
        Assert.Contains("source row", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedExcelTextIsRejectedByChunkedPreflightBeforeDomAllocation(bool shared)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "oversized.xlsx");
        WriteLargeExcel(path, shared, textCharacters: 12 * 1024 * 1024, sourceColumns: 1);
        using (var package = SpreadsheetDocument.Open(path, false))
        {
            var workbook = package.WorkbookPart!;
            OpenXmlPart part = shared ? workbook.SharedStringTablePart! : workbook.WorksheetParts.Single();
            using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var error = Assert.Throws<InvalidDataException>(() => SelectionImportReader.ValidateExcelXmlBounds(stream, shared, CancellationToken.None));
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Contains("exceeds 1MiB", error.Message);
            Assert.True(allocated < 4L * 1024 * 1024, $"Preflight allocated {allocated:N0} bytes before rejecting a large XML text node.");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => new SelectionImportReader().ReadFileAsync(path, NameOnlyTableRule()));
    }

    [Fact]
    public async Task ExcelSourceRowBudgetIncludesUnmappedInlineAndResolvedSharedStrings()
    {
        using var directory = new TemporaryDirectory();
        foreach (var shared in new[] { false, true })
        {
            var path = Path.Combine(directory.Path, shared ? "wide-shared.xlsx" : "wide-inline.xlsx");
            WriteLargeExcel(path, shared, textCharacters: 1024 * 1024, sourceColumns: 5);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => new SelectionImportReader().ReadFileAsync(path, NameOnlyTableRule()));
            Assert.Contains("source row", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ExcelRichFormattingCannotMaterializeAnUnboundedNumberOfTinyDomNodes()
    {
        var xml = new StringBuilder("<sst><si>");
        for (var index = 0; index < 4200; index++) xml.Append("<r><t>x</t></r>");
        xml.Append("</si></sst>");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml.ToString()));
        var error = Assert.Throws<InvalidDataException>(() => SelectionImportReader.ValidateExcelXmlBounds(stream, true, CancellationToken.None));
        Assert.Contains("nested formatting", error.Message);
    }

    private static ProfileSelectionMatchRuleManifest NameOnlyTableRule() => new() { Id = "name", DisplayName = "Name", InputKind = ProfileSelectionInputKind.Table,
        Columns = [new() { FieldId = "name", InputHeader = "Name" }] };

    private static void WriteLargeExcel(string path, bool shared, int textCharacters, int sourceColumns)
    {
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using var package = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbook = package.AddWorkbookPart();
        var definition = new Workbook(); workbook.Workbook = definition;
        var part = workbook.AddNewPart<WorksheetPart>();
        definition.Append(new Sheets(new Sheet { Name = "Data", SheetId = 1, Id = workbook.GetIdOfPart(part) }));
        if (shared)
        {
            var strings = workbook.AddNewPart<SharedStringTablePart>();
            using var source = strings.GetStream(FileMode.Create, FileAccess.Write);
            using var writer = XmlWriter.Create(source, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false });
            writer.WriteStartElement("sst", ns); writer.WriteStartElement("si", ns); writer.WriteStartElement("t", ns);
            WriteCharacters(writer, textCharacters);
            writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
        }
        using (var source = part.GetStream(FileMode.Create, FileAccess.Write))
        using (var writer = XmlWriter.Create(source, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false }))
        {
            writer.WriteStartElement("worksheet", ns); writer.WriteStartElement("sheetData", ns);
            writer.WriteStartElement("row", ns); writer.WriteAttributeString("r", "1");
            InlineXml(writer, "A1", "Name"); writer.WriteEndElement();
            writer.WriteStartElement("row", ns); writer.WriteAttributeString("r", "2"); InlineXml(writer, "A2", "Alice");
            for (var column = 0; column < sourceColumns; column++)
            {
                writer.WriteStartElement("c", ns); writer.WriteAttributeString("r", ((char)('B' + column)).ToString() + "2");
                writer.WriteAttributeString("t", shared ? "s" : "inlineStr");
                if (shared) { writer.WriteElementString("v", ns, "0"); }
                else
                {
                    writer.WriteStartElement("is", ns); writer.WriteStartElement("t", ns); WriteCharacters(writer, textCharacters);
                    writer.WriteEndElement(); writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
        }
        definition.Save();

        static void WriteCharacters(XmlWriter writer, int count)
        {
            var characters = new string('x', 8192).ToCharArray();
            for (var written = 0; written < count; written += characters.Length)
                writer.WriteChars(characters, 0, Math.Min(characters.Length, count - written));
        }
        static void InlineXml(XmlWriter writer, string reference, string text)
        {
            writer.WriteStartElement("c", ns); writer.WriteAttributeString("r", reference); writer.WriteAttributeString("t", "inlineStr");
            writer.WriteStartElement("is", ns); writer.WriteElementString("t", ns, text); writer.WriteEndElement(); writer.WriteEndElement();
        }
    }

    private static Cell Inline(string reference, string value) => new() { CellReference = reference, DataType = CellValues.InlineString, InlineString = new InlineString(new Text(value)) };
    private static ProfileSelectionMatchRuleManifest TableRule() => new() { Id = "table", DisplayName = "Table", InputKind = ProfileSelectionInputKind.Table,
        Columns = [new() { FieldId = "name", InputHeader = "Name" }, new() { FieldId = "note", InputHeader = "Note" }] };
    private static async Task<List<CatalogSelectionInputRow>> Collect(SelectionImportDocument document)
    { var rows = new List<CatalogSelectionInputRow>(); await foreach (var row in document.ReadRowsAsync()) rows.Add(row); return rows; }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("fe-import-reader-tests-").FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }
}
