using System.IO.Compression;
using System.Text;
using System.Xml;
using FindEverything.Desktop.Localization;

namespace FindEverything.Desktop.Services;

public enum CatalogSelectionExportFormat
{
    Csv,
    Xlsx,
}

public sealed record CatalogSelectionExportDestination(
    string Path,
    CatalogSelectionExportFormat Format);

public sealed record CatalogSelectionExportDocument(
    IReadOnlyList<string> Headers,
    IEnumerable<IReadOnlyList<string>> Rows,
    int RowCount);

public interface ICatalogSelectionExporter
{
    Task ExportAsync(
        string path,
        CatalogSelectionExportFormat format,
        CatalogSelectionExportDocument document,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Writes selected structured rows without requiring Microsoft Office or an
/// external spreadsheet package. XLSX output uses the minimum Open XML package
/// parts and inline string cells, which also prevents values beginning with '='
/// from being interpreted as formulas.
/// </summary>
public sealed class CatalogSelectionExporter : ICatalogSelectionExporter
{
    private const int ExcelMaximumRows = 1_048_576;
    private const int ExcelMaximumColumns = 16_384;
    private const int ExcelMaximumCellCharacters = 32_767;
    private const string SpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string OfficeRelationshipsNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipsNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypesNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: true,
        throwOnInvalidBytes: true);
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly System.Globalization.CultureInfo _culture;
    private readonly ExportText _text;

    public CatalogSelectionExporter(IAppLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        _culture = localizer.Culture;
        // Resolve WPF resources while the service is created on the UI thread.
        // XLSX generation later runs on a worker thread and consumes only this
        // immutable snapshot, never the application ResourceDictionary.
        _text = new ExportText(
            localizer.Get("Loc.Export.Error.NoColumns", "내보낼 컬럼이 없습니다."),
            localizer.Get("Loc.Export.Error.NegativeRowCount", "내보낼 행 수는 음수일 수 없습니다."),
            localizer.Get(
                "Loc.Export.Error.DirectoryNotFound",
                "내보낼 폴더를 찾을 수 없습니다: {0}"),
            localizer.Get("Loc.Export.Error.UnsupportedFormat", "지원하지 않는 내보내기 형식입니다."),
            localizer.Get(
                "Loc.Export.Error.ExcelColumnLimit",
                "Excel은 최대 {0:N0}개 컬럼까지 지원합니다."),
            localizer.Get(
                "Loc.Export.Error.ExcelRowLimit",
                "Excel은 머리글을 포함해 최대 {0:N0}개 행까지 지원합니다."),
            localizer.Get("Loc.Export.SheetName", "구조화 결과"),
            localizer.Get(
                "Loc.Export.Error.ExcelCellLimit",
                "Excel 셀은 최대 {0:N0}자까지 지원합니다. {1:N0}행 {2:N0}열의 값을 줄여 주세요."),
            localizer.Get(
                "Loc.Export.Error.ColumnCountMismatch",
                "내보내기 행의 컬럼 수가 올바르지 않습니다. 예상 {0:N0}개, 실제 {1:N0}개입니다."),
            localizer.Get(
                "Loc.Export.Error.RowCountMismatch",
                "내보내기 행 수가 올바르지 않습니다. 예상 {0:N0}개, 실제 {1:N0}개입니다."));
    }

    public async Task ExportAsync(
        string path,
        CatalogSelectionExportFormat format,
        CatalogSelectionExportDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(document.Headers);
        ArgumentNullException.ThrowIfNull(document.Rows);
        if (document.Headers.Count == 0)
        {
            throw new ArgumentException(_text.NoColumns, nameof(document));
        }

        if (document.RowCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(document),
                _text.NegativeRowCount);
        }

        var destinationPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                Format(_text.DirectoryNotFound, directory));
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            switch (format)
            {
                case CatalogSelectionExportFormat.Csv:
                    await WriteCsvAsync(temporaryPath, document, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case CatalogSelectionExportFormat.Xlsx:
                    await Task.Run(
                            () => WriteXlsx(temporaryPath, document, cancellationToken),
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(format),
                        format,
                        _text.UnsupportedFormat);
            }

            cancellationToken.ThrowIfCancellationRequested();
            PublishAtomically(temporaryPath, destinationPath);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private async Task WriteCsvAsync(
        string temporaryPath,
        CatalogSelectionExportDocument document,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using var writer = new StreamWriter(
            stream,
            Utf8WithBom,
            64 * 1024,
            leaveOpen: true)
        {
            NewLine = "\r\n",
        };

        await WriteCsvRowAsync(writer, document.Headers, cancellationToken)
            .ConfigureAwait(false);
        var writtenRows = 0;
        foreach (var row in document.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateColumnCount(row, document.Headers.Count);
            await WriteCsvRowAsync(writer, row, cancellationToken).ConfigureAwait(false);
            writtenRows++;
        }

        ValidateWrittenRowCount(writtenRows, document.RowCount);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task WriteCsvRowAsync(
        TextWriter writer,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                await writer.WriteAsync(",".AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            var escaped = EscapeCsvValue(values[index] ?? string.Empty);
            await writer.WriteAsync(escaped.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string EscapeCsvValue(string value)
    {
        // Spreadsheet programs can execute formula-like CSV cells. Prefix the
        // original text with an apostrophe so opening an exported CSV is safe.
        var firstNonWhitespace = value.AsSpan().TrimStart();
        if (!firstNonWhitespace.IsEmpty && firstNonWhitespace[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private void WriteXlsx(
        string temporaryPath,
        CatalogSelectionExportDocument document,
        CancellationToken cancellationToken)
    {
        if (document.Headers.Count > ExcelMaximumColumns)
        {
            throw new InvalidOperationException(
                Format(_text.ExcelColumnLimit, ExcelMaximumColumns));
        }

        if (document.RowCount > ExcelMaximumRows - 1)
        {
            throw new InvalidOperationException(
                Format(_text.ExcelRowLimit, ExcelMaximumRows));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            64 * 1024,
            FileOptions.WriteThrough);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteContentTypes(archive);
            WritePackageRelationships(archive);
            WriteWorkbook(archive);
            WriteWorkbookRelationships(archive);
            WriteWorksheet(archive, document, cancellationToken);
        }

        stream.Flush(flushToDisk: true);
    }

    private static void WriteContentTypes(ZipArchive archive)
    {
        using var writer = CreateXmlWriter(archive, "[Content_Types].xml");
        writer.WriteStartDocument();
        writer.WriteStartElement("Types", ContentTypesNamespace);
        WriteContentTypeDefault(writer, "rels", "application/vnd.openxmlformats-package.relationships+xml");
        WriteContentTypeDefault(writer, "xml", "application/xml");
        WriteContentTypeOverride(
            writer,
            "/xl/workbook.xml",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        WriteContentTypeOverride(
            writer,
            "/xl/worksheets/sheet1.xml",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteContentTypeDefault(
        XmlWriter writer,
        string extension,
        string contentType)
    {
        writer.WriteStartElement("Default", ContentTypesNamespace);
        writer.WriteAttributeString("Extension", extension);
        writer.WriteAttributeString("ContentType", contentType);
        writer.WriteEndElement();
    }

    private static void WriteContentTypeOverride(
        XmlWriter writer,
        string partName,
        string contentType)
    {
        writer.WriteStartElement("Override", ContentTypesNamespace);
        writer.WriteAttributeString("PartName", partName);
        writer.WriteAttributeString("ContentType", contentType);
        writer.WriteEndElement();
    }

    private static void WritePackageRelationships(ZipArchive archive)
    {
        using var writer = CreateXmlWriter(archive, "_rels/.rels");
        writer.WriteStartDocument();
        writer.WriteStartElement("Relationships", PackageRelationshipsNamespace);
        WriteRelationship(
            writer,
            "rId1",
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument",
            "xl/workbook.xml");
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private void WriteWorkbook(ZipArchive archive)
    {
        using var writer = CreateXmlWriter(archive, "xl/workbook.xml");
        writer.WriteStartDocument();
        writer.WriteStartElement("workbook", SpreadsheetNamespace);
        writer.WriteAttributeString("xmlns", "r", null, OfficeRelationshipsNamespace);
        writer.WriteStartElement("sheets", SpreadsheetNamespace);
        writer.WriteStartElement("sheet", SpreadsheetNamespace);
        writer.WriteAttributeString("name", _text.SheetName);
        writer.WriteAttributeString("sheetId", "1");
        writer.WriteAttributeString("r", "id", OfficeRelationshipsNamespace, "rId1");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteWorkbookRelationships(ZipArchive archive)
    {
        using var writer = CreateXmlWriter(archive, "xl/_rels/workbook.xml.rels");
        writer.WriteStartDocument();
        writer.WriteStartElement("Relationships", PackageRelationshipsNamespace);
        WriteRelationship(
            writer,
            "rId1",
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet",
            "worksheets/sheet1.xml");
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteRelationship(
        XmlWriter writer,
        string id,
        string type,
        string target)
    {
        writer.WriteStartElement("Relationship", PackageRelationshipsNamespace);
        writer.WriteAttributeString("Id", id);
        writer.WriteAttributeString("Type", type);
        writer.WriteAttributeString("Target", target);
        writer.WriteEndElement();
    }

    private void WriteWorksheet(
        ZipArchive archive,
        CatalogSelectionExportDocument document,
        CancellationToken cancellationToken)
    {
        using var writer = CreateXmlWriter(archive, "xl/worksheets/sheet1.xml");
        writer.WriteStartDocument();
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);
        writer.WriteStartElement("sheetData", SpreadsheetNamespace);
        WriteSpreadsheetRow(writer, document.Headers, rowNumber: 1);

        var writtenRows = 0;
        foreach (var row in document.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateColumnCount(row, document.Headers.Count);
            writtenRows++;
            if (writtenRows >= ExcelMaximumRows)
            {
                throw new InvalidOperationException(
                    Format(_text.ExcelRowLimit, ExcelMaximumRows));
            }

            WriteSpreadsheetRow(writer, row, writtenRows + 1);
        }

        ValidateWrittenRowCount(writtenRows, document.RowCount);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private void WriteSpreadsheetRow(
        XmlWriter writer,
        IReadOnlyList<string> values,
        int rowNumber)
    {
        writer.WriteStartElement("row", SpreadsheetNamespace);
        writer.WriteAttributeString("r", rowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (var columnIndex = 0; columnIndex < values.Count; columnIndex++)
        {
            var value = values[columnIndex] ?? string.Empty;
            if (value.Length > ExcelMaximumCellCharacters)
            {
                throw new InvalidOperationException(
                    Format(
                        _text.ExcelCellLimit,
                        ExcelMaximumCellCharacters,
                        rowNumber,
                        columnIndex + 1));
            }

            writer.WriteStartElement("c", SpreadsheetNamespace);
            writer.WriteAttributeString("r", $"{GetExcelColumnName(columnIndex)}{rowNumber}");
            writer.WriteAttributeString("t", "inlineStr");
            writer.WriteStartElement("is", SpreadsheetNamespace);
            writer.WriteStartElement("t", SpreadsheetNamespace);
            writer.WriteAttributeString(
                "xml",
                "space",
                "http://www.w3.org/XML/1998/namespace",
                "preserve");
            writer.WriteString(SanitizeXmlText(value));
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static string GetExcelColumnName(int zeroBasedIndex)
    {
        var value = zeroBasedIndex + 1;
        Span<char> buffer = stackalloc char[3];
        var offset = buffer.Length;
        while (value > 0)
        {
            value--;
            buffer[--offset] = (char)('A' + value % 26);
            value /= 26;
        }

        return new string(buffer[offset..]);
    }

    private static string SanitizeXmlText(string value)
    {
        StringBuilder? builder = null;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (XmlConvert.IsXmlChar(character))
            {
                builder?.Append(character);
                continue;
            }

            if (char.IsHighSurrogate(character)
                && index + 1 < value.Length
                && char.IsLowSurrogate(value[index + 1]))
            {
                if (builder is not null)
                {
                    builder.Append(character);
                    builder.Append(value[++index]);
                }
                else
                {
                    index++;
                }

                continue;
            }

            builder ??= new StringBuilder(value.Length).Append(value, 0, index);
            builder.Append('\uFFFD');
        }

        return builder?.ToString() ?? value;
    }

    private static XmlWriter CreateXmlWriter(ZipArchive archive, string entryName)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        return XmlWriter.Create(
            entry.Open(),
            new XmlWriterSettings
            {
                Encoding = Utf8WithoutBom,
                CloseOutput = true,
                CheckCharacters = true,
                Indent = false,
            });
    }

    private void ValidateColumnCount(
        IReadOnlyCollection<string> row,
        int expectedColumnCount)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Count != expectedColumnCount)
        {
            throw new InvalidOperationException(
                Format(
                    _text.ColumnCountMismatch,
                    expectedColumnCount,
                    row.Count));
        }
    }

    private void ValidateWrittenRowCount(int writtenRows, int expectedRows)
    {
        if (writtenRows != expectedRows)
        {
            throw new InvalidOperationException(
                Format(
                    _text.RowCountMismatch,
                    expectedRows,
                    writtenRows));
        }
    }

    private static void PublishAtomically(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            try
            {
                File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
                return;
            }
            catch (PlatformNotSupportedException)
            {
                // File.Move(overwrite: true) is the portable fallback used by
                // tests and still keeps the completed temporary file isolated
                // until publication.
            }
        }

        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        if (!File.Exists(temporaryPath))
        {
            return;
        }

        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Preserve the actionable export failure. Temporary cleanup is
            // best-effort and must not replace the original exception.
        }
    }

    private string Format(string template, params object?[] arguments) =>
        string.Format(_culture, template, arguments);

    private sealed record ExportText(
        string NoColumns,
        string NegativeRowCount,
        string DirectoryNotFound,
        string UnsupportedFormat,
        string ExcelColumnLimit,
        string ExcelRowLimit,
        string SheetName,
        string ExcelCellLimit,
        string ColumnCountMismatch,
        string RowCountMismatch);
}
