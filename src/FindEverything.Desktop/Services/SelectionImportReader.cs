using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using AngleSharp.Html.Parser;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FindEverything.Application.Catalog;
using FindEverything.Profile.Runtime;
using Microsoft.Data.Sqlite;

namespace FindEverything.Desktop.Services;

/// <summary>Private disk-backed parsed input. Only the first 100 rows are kept for preview.</summary>
public sealed class SelectionImportDocument : IDisposable
{
    private readonly string _directory;
    private readonly string _path;
    private bool _disposed;
    internal SelectionImportDocument(string directory, string path, IReadOnlyList<CatalogSelectionInputRow> preview,
        long count, string description)
    { _directory = directory; _path = path; PreviewRows = preview; RowCount = count; Description = description; }
    public IReadOnlyList<CatalogSelectionInputRow> PreviewRows { get; }
    public long RowCount { get; }
    public string Description { get; }
    public async IAsyncEnumerable<CatalogSelectionInputRow> ReadRowsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = _path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT row_number,values_json FROM input_rows ORDER BY row_number;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(reader.GetInt64(0), JsonSerializer.Deserialize<string?[]>(reader.GetString(1))!);
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { File.Delete(_path); } catch (IOException) { }
        try { Directory.Delete(_directory); } catch (IOException) { }
    }
}

public sealed class SelectionImportReader
{
    public const long MaximumFileBytes = 256L * 1024 * 1024;
    public const int MaximumTextCharacters = 16 * 1024 * 1024;
    private const int MaximumCellCharacters = 1024 * 1024;
    internal const int MaximumSourceRowCharacters = 4 * 1024 * 1024;

    public Task<SelectionImportDocument> ReadFileAsync(string path, ProfileSelectionMatchRuleManifest rule,
        string? sheetName = null, int tableIndex = 0, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaximumFileBytes) throw new InvalidDataException("The input file is missing or exceeds 256MiB.");
            var extension = file.Extension.ToLowerInvariant();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumFileBytes) throw new InvalidDataException("The input file exceeds 256MiB.");
            if (extension == ".xlsx") return ReadExcel(stream, rule, sheetName, file.Name, cancellationToken);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            if (extension is ".html" or ".htm")
                return ReadTextCore(ReadBounded(reader), rule, true, tableIndex, file.Name, cancellationToken);
            if (extension is not (".txt" or ".csv" or ".tsv")) throw new InvalidDataException("Only XLSX, HTML, TXT, CSV and TSV input is supported.");
            if (rule.InputKind == ProfileSelectionInputKind.Regex)
                return ReadTextCore(ReadBounded(reader), rule, false, tableIndex, file.Name, cancellationToken);
            if (rule.InputKind == ProfileSelectionInputKind.TextLines)
                return Buffer(ReadLines(reader, cancellationToken), rule, file.Name, cancellationToken);
            var delimiter = extension == ".tsv" ? '\t' : rule.Delimiter is { Length: 1 } ? rule.Delimiter[0] : ',';
            if (extension == ".txt" && string.IsNullOrEmpty(rule.Delimiter))
            {
                var buffer = new char[8192];
                var count = reader.ReadBlock(buffer, 0, buffer.Length);
                var prefix = new string(buffer, 0, count);
                delimiter = prefix.Contains('\t') ? '\t' : ',';
                using var combined = new PrefixTextReader(prefix, reader);
                return Buffer(ReadDelimited(combined, delimiter, cancellationToken), rule, file.Name, cancellationToken);
            }
            return Buffer(ReadDelimited(reader, delimiter, cancellationToken), rule, file.Name, cancellationToken);
        }, cancellationToken);

    public Task<SelectionImportDocument> ReadTextAsync(string text, ProfileSelectionMatchRuleManifest rule,
        bool isHtml = false, int tableIndex = 0, CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadTextCore(text, rule, isHtml, tableIndex, "Clipboard / text", cancellationToken), cancellationToken);

    public static IReadOnlyList<string> ReadSheetNames(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ValidateExcelArchive(stream);
        using var document = SpreadsheetDocument.Open(stream, false, new OpenSettings { AutoSave = false, MaxCharactersInPart = MaximumFileBytes });
        return document.WorkbookPart?.Workbook?.Sheets?.Elements<Sheet>().Select(sheet => sheet.Name?.Value ?? "").ToArray() ?? [];
    }

    private static SelectionImportDocument ReadTextCore(string text, ProfileSelectionMatchRuleManifest rule,
        bool html, int tableIndex, string description, CancellationToken token)
    {
        if (text.Length > MaximumTextCharacters) throw new InvalidDataException("Text/HTML input exceeds 16MiB of characters.");
        token.ThrowIfCancellationRequested();
        if (rule.InputKind == ProfileSelectionInputKind.Regex)
        {
            var options = RegexOptions.CultureInvariant | (rule.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None)
                | (rule.Multiline ? RegexOptions.Multiline : RegexOptions.None);
            var regex = new Regex(rule.RegexPattern ?? "", options, TimeSpan.FromMilliseconds(rule.TimeoutMilliseconds));
            IEnumerable<string?[]> Rows()
            {
                foreach (Match match in regex.Matches(text))
                {
                    token.ThrowIfCancellationRequested();
                    yield return rule.Columns.Select(column => match.Groups[column.RegexGroup ?? ""].Success
                        ? match.Groups[column.RegexGroup ?? ""].Value : null).ToArray();
                }
            }
            return BufferMapped(Rows(), description, token);
        }
        if (html)
        {
            // No browsing context, resource loader, or JS engine is created.
            var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false });
            using var document = parser.ParseDocument(text);
            var tables = document.QuerySelectorAll("table");
            if (tableIndex < 0 || tableIndex >= tables.Length) throw new InvalidDataException("The selected HTML table does not exist.");
            var table = tables[tableIndex];
            IEnumerable<string?[]> Rows()
            {
                foreach (var row in table.QuerySelectorAll("tr").Where(row => ReferenceEquals(row.Closest("table"), table)))
                {
                    token.ThrowIfCancellationRequested();
                    var cells = row.Children.Where(child => child.LocalName is "td" or "th").ToArray();
                    if (cells.Any(cell => cell.HasAttribute("rowspan") && cell.GetAttribute("rowspan") != "1"
                        || cell.HasAttribute("colspan") && cell.GetAttribute("colspan") != "1"))
                        throw new InvalidDataException("Merged HTML cells are ambiguous. Paste an unmerged table instead.");
                    foreach (var ignored in cells.SelectMany(cell => cell.QuerySelectorAll("script,style"))) ignored.Remove();
                    yield return cells.Select(cell => (string?)cell.TextContent).ToArray();
                }
            }
            return Buffer(Rows(), rule, description, token);
        }
        using var reader = new StringReader(text);
        if (rule.InputKind == ProfileSelectionInputKind.TextLines) return Buffer(ReadLines(reader, token), rule, description, token);
        var separator = rule.Delimiter is { Length: 1 } ? rule.Delimiter[0] : text.Contains('\t') ? '\t' : ',';
        return Buffer(ReadDelimited(reader, separator, token), rule, description, token);
    }

    private static string ReadBounded(TextReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (builder.Length + count > MaximumTextCharacters) throw new InvalidDataException("Text/HTML input exceeds 16MiB of characters.");
            builder.Append(buffer, 0, count);
        }
        return builder.ToString();
    }

    private static IEnumerable<string?[]> ReadLines(TextReader reader, CancellationToken token)
    {
        var line = new StringBuilder();
        int character;
        while ((character = reader.Read()) != -1)
        {
            token.ThrowIfCancellationRequested();
            if (character is '\r' or '\n')
            {
                if (character == '\r' && reader.Peek() == '\n') reader.Read();
                var text = line.ToString(); line.Clear();
                if (!string.IsNullOrWhiteSpace(text)) yield return [text];
            }
            else line.Append((char)character);
            if (line.Length > MaximumCellCharacters) throw new InvalidDataException("An input line exceeds 1MiB.");
        }
        if (line.Length > 0)
        { var text = line.ToString(); if (!string.IsNullOrWhiteSpace(text)) yield return [text]; }
    }

    internal static IEnumerable<string?[]> ReadDelimited(TextReader reader, char delimiter, CancellationToken token)
    {
        var values = new List<string?>();
        var current = new StringBuilder();
        var quoted = false;
        var afterQuote = false;
        var started = false;
        long rowCharacters = 0;
        int character;
        while ((character = reader.Read()) != -1)
        {
            token.ThrowIfCancellationRequested();
            started = true;
            var value = (char)character;
            if (quoted)
            {
                if (value == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); current.Append('"'); }
                    else { quoted = false; afterQuote = true; }
                }
                else current.Append(value);
            }
            else if (value == '"' && current.Length == 0 && !afterQuote) quoted = true;
            else if (value == delimiter)
            { AddValue(); current.Clear(); afterQuote = false; }
            else if (value is '\r' or '\n')
            {
                if (value == '\r' && reader.Peek() == '\n') reader.Read();
                AddValue();
                if (values.Any(cell => !string.IsNullOrWhiteSpace(cell))) yield return values.ToArray();
                values.Clear(); current.Clear(); afterQuote = false; started = false; rowCharacters = 0;
            }
            else if (afterQuote && !char.IsWhiteSpace(value)) throw new InvalidDataException("Unexpected text follows a closing CSV quote.");
            else if (!afterQuote) current.Append(value);
            if (current.Length > MaximumCellCharacters || values.Count > 16384) throw new InvalidDataException("An input cell or row is too large.");
            if (rowCharacters + current.Length > MaximumSourceRowCharacters)
                throw new InvalidDataException("An input source row exceeds 8MiB of UTF-16 text.");
        }
        if (quoted) throw new InvalidDataException("An input CSV field has no closing quote.");
        if (started)
        { AddValue(); if (values.Any(cell => !string.IsNullOrWhiteSpace(cell))) yield return values.ToArray(); }
        void AddValue()
        {
            rowCharacters += current.Length;
            if (rowCharacters > MaximumSourceRowCharacters) throw new InvalidDataException("An input source row exceeds 8MiB of UTF-16 text.");
            values.Add(current.ToString());
        }
    }

    private static SelectionImportDocument Buffer(IEnumerable<string?[]> rows, ProfileSelectionMatchRuleManifest rule,
        string description, CancellationToken token)
    {
        using var enumerator = rows.GetEnumerator();
        if (!enumerator.MoveNext()) throw new InvalidDataException("There are no input rows.");
        var first = enumerator.Current;
        var headers = rule.InputKind == ProfileSelectionInputKind.Table && rule.HasHeaderRow ? first : null;
        var indices = rule.Columns.Select(column => rule.InputKind == ProfileSelectionInputKind.TextLines ? 0
            : !string.IsNullOrWhiteSpace(column.InputHeader) ? FindHeader(headers, column.InputHeader)
            : column.InputColumnIndex ?? throw new InvalidDataException("The input column is not configured.")).ToArray();
        IEnumerable<string?[]> Mapped()
        {
            if (headers is null) yield return Map(first);
            while (enumerator.MoveNext()) { token.ThrowIfCancellationRequested(); yield return Map(enumerator.Current); }
        }
        string?[] Map(string?[] row) => indices.Select(index => index < row.Length ? row[index] : null).ToArray();
        return BufferMapped(Mapped(), description, token);

        static int FindHeader(string?[]? headers, string name)
        {
            if (headers is null) throw new InvalidDataException("The rule requires a header row.");
            var found = Enumerable.Range(0, headers.Length).Where(index => string.Equals(headers[index]?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (found.Length != 1) throw new InvalidDataException($"Input header '{name}' is missing or ambiguous.");
            return found[0];
        }
    }

    private static SelectionImportDocument BufferMapped(IEnumerable<string?[]> rows, string description, CancellationToken token)
    {
        var directory = Directory.CreateTempSubdirectory("FindEverything-input-").FullName;
        var path = Path.Combine(directory, "input.db");
        var success = false;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false }.ToString());
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var append = connection.CreateCommand();
            append.Transaction = transaction;
            append.CommandText = "CREATE TABLE input_rows(row_number INTEGER PRIMARY KEY,values_json TEXT NOT NULL);";
            append.ExecuteNonQuery();
            append.CommandText = "INSERT INTO input_rows(row_number,values_json) VALUES($row,$values);";
            var number = append.Parameters.Add("$row", SqliteType.Integer);
            var json = append.Parameters.Add("$values", SqliteType.Text);
            append.Prepare();
            var preview = new List<CatalogSelectionInputRow>(100);
            long count = 0, bytes = 0;
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                if (++count > 200_000 || row.Any(value => value?.Length > MaximumCellCharacters))
                    throw new InvalidDataException("Input exceeds 200,000 rows or 1MiB per cell.");
                var serialized = JsonSerializer.Serialize(row);
                bytes += 2L * serialized.Length;
                if (bytes > MaximumFileBytes) throw new InvalidDataException("Parsed input exceeds 256MiB.");
                number.Value = count; json.Value = serialized; append.ExecuteNonQuery();
                if (preview.Count < 100) preview.Add(new(count, row.Select(value => value is { Length: > 256 } ? value[..256] + "…" : value).ToArray()));
            }
            if (count == 0) throw new InvalidDataException("There are no data rows after the header.");
            transaction.Commit();
            success = true;
            return new(directory, path, preview, count, description);
        }
        finally
        {
            if (!success)
            { try { File.Delete(path); } catch (IOException) { } try { Directory.Delete(directory); } catch (IOException) { } }
        }
    }

    private static SelectionImportDocument ReadExcel(Stream stream, ProfileSelectionMatchRuleManifest rule,
        string? sheetName, string description, CancellationToken token)
    {
        if (rule.InputKind == ProfileSelectionInputKind.Regex) throw new InvalidDataException("A regex input rule expects text, not an Excel workbook.");
        ValidateExcelArchive(stream);
        using var document = SpreadsheetDocument.Open(stream, false, new OpenSettings { AutoSave = false, MaxCharactersInPart = MaximumFileBytes });
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("The workbook is missing.");
        var definition = workbook.Workbook ?? throw new InvalidDataException("The workbook definition is missing.");
        var sheet = definition.Sheets?.Elements<Sheet>().FirstOrDefault(sheet => sheetName is null
            ? sheet.State?.Value != SheetStateValues.Hidden && sheet.State?.Value != SheetStateValues.VeryHidden
            : sheet.Name?.Value == sheetName) ?? throw new InvalidDataException("The selected worksheet is missing.");
        var part = workbook.GetPartById(sheet.Id?.Value ?? "") as WorksheetPart ?? throw new InvalidDataException("The worksheet is missing.");
        var sharedDirectory = Directory.CreateTempSubdirectory("FindEverything-xlsx-").FullName;
        var sharedPath = Path.Combine(sharedDirectory, "strings.db");
        try
        {
            using var strings = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sharedPath, Pooling = false }.ToString());
            strings.Open();
            using (var transaction = strings.BeginTransaction())
            using (var append = strings.CreateCommand())
            {
                append.Transaction = transaction;
                append.CommandText = "CREATE TABLE shared_strings(id INTEGER PRIMARY KEY,value TEXT NOT NULL);";
                append.ExecuteNonQuery();
                append.CommandText = "INSERT INTO shared_strings(id,value) VALUES($id,$value);";
                var id = append.Parameters.Add("$id", SqliteType.Integer);
                var value = append.Parameters.Add("$value", SqliteType.Text);
                if (workbook.SharedStringTablePart is { } sharedPart)
                {
                    using (var source = sharedPart.GetStream(FileMode.Open, FileAccess.Read))
                        ValidateExcelXmlBounds(source, sharedStrings: true, token);
                    using var reader = OpenXmlReader.Create(sharedPart);
                    long index = 0, bytes = 0;
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        if (reader.IsStartElement && reader.ElementType == typeof(SharedStringItem))
                        {
                            var item = reader.LoadCurrentElement() as SharedStringItem
                                ?? throw new InvalidDataException("The Excel shared string entry is invalid.");
                            var text = string.Concat(item.Descendants<DocumentFormat.OpenXml.Spreadsheet.Text>().Select(text => text.Text));
                            bytes += 2L * text.Length;
                            if (text.Length > MaximumCellCharacters || bytes > MaximumFileBytes) throw new InvalidDataException("Excel shared text is too large.");
                            id.Value = index++; value.Value = text; append.ExecuteNonQuery();
                        }
                    }
                }
                transaction.Commit();
            }
            using var lookup = strings.CreateCommand();
            lookup.CommandText = "SELECT value FROM shared_strings WHERE id=$id;";
            var sharedId = lookup.Parameters.Add("$id", SqliteType.Integer);
            var cache = new Dictionary<long, string>();
            long cacheCharacters = 0;
            var styles = workbook.WorkbookStylesPart?.Stylesheet;
            var formats = styles?.NumberingFormats?.Elements<NumberingFormat>().ToDictionary(format => format.NumberFormatId?.Value ?? 0,
                format => format.FormatCode?.Value ?? "") ?? [];
            var dateStyles = styles?.CellFormats?.Elements<CellFormat>().Select((format, index) => (format, index))
                .Where(pair => IsDateFormat(pair.format.NumberFormatId?.Value ?? 0, formats)).Select(pair => (uint)pair.index).ToHashSet() ?? [];
            var date1904 = definition.WorkbookProperties?.Date1904?.Value == true;
            IEnumerable<string?[]> Rows()
            {
                using (var source = part.GetStream(FileMode.Open, FileAccess.Read))
                    ValidateExcelXmlBounds(source, sharedStrings: false, token);
                using var reader = OpenXmlReader.Create(part);
                var values = new Dictionary<int, string?>();
                var rowOpen = false;
                var sequentialColumn = 0;
                long rowCharacters = 0;
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.ElementType == typeof(Row))
                    {
                        if (reader.IsStartElement) { values.Clear(); rowOpen = true; sequentialColumn = 0; rowCharacters = 0; }
                        else if (rowOpen)
                        {
                            rowOpen = false;
                            if (values.Count != 0)
                            {
                                var row = new string?[values.Keys.Max() + 1];
                                foreach (var cell in values) row[cell.Key] = cell.Value;
                                yield return row;
                            }
                        }
                    }
                    else if (rowOpen && reader.IsStartElement && reader.ElementType == typeof(Cell))
                    {
                        var cell = reader.LoadCurrentElement() as Cell
                            ?? throw new InvalidDataException("The Excel cell is invalid.");
                        var column = cell.CellReference?.Value is { Length: > 0 } reference ? ColumnIndex(reference) : sequentialColumn;
                        sequentialColumn = column + 1;
                        if (column > 16383) throw new InvalidDataException("Excel column reference is out of bounds.");
                        var text = cell.CellValue?.Text ?? cell.InlineString?.InnerText;
                        if (cell.DataType?.Value == CellValues.SharedString)
                        {
                            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) throw new InvalidDataException("Invalid Excel shared string index.");
                            if (!cache.TryGetValue(id, out text))
                            {
                                sharedId.Value = id;
                                text = lookup.ExecuteScalar() as string ?? throw new InvalidDataException("Missing Excel shared string value.");
                                if (cache.Count >= 2048 || cacheCharacters + text.Length > MaximumSourceRowCharacters)
                                { cache.Clear(); cacheCharacters = 0; }
                                cache[id] = text; cacheCharacters += text.Length;
                            }
                        }
                        else if (cell.DataType?.Value == CellValues.Boolean) text = text == "1" ? "true" : text == "0" ? "false" : text;
                        else if (cell.DataType?.Value == CellValues.Error) text = null;
                        else if (text is not null && dateStyles.Contains(cell.StyleIndex?.Value ?? 0)
                            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
                        {
                            try { text = DateTime.FromOADate(serial + (date1904 ? 1462 : 0)).ToString("O", CultureInfo.InvariantCulture); }
                            catch (ArgumentException) { text = null; }
                        }
                        if (text?.Length > MaximumCellCharacters) throw new InvalidDataException("An Excel cell exceeds 1MiB.");
                        rowCharacters += text?.Length ?? 0;
                        if (rowCharacters > MaximumSourceRowCharacters)
                            throw new InvalidDataException("An Excel source row exceeds 8MiB of UTF-16 text.");
                        values[column] = text;
                    }
                }
            }
            return Buffer(Rows(), rule, description + " · " + sheet.Name?.Value, token);
        }
        finally
        { try { File.Delete(sharedPath); } catch (IOException) { } try { Directory.Delete(sharedDirectory); } catch (IOException) { } }
    }

    private static int ColumnIndex(string reference)
    {
        var result = 0;
        foreach (var character in reference)
        {
            if (character is >= 'A' and <= 'Z') result = checked(result * 26 + character - 'A' + 1);
            else if (character is >= 'a' and <= 'z') result = checked(result * 26 + character - 'a' + 1);
            else break;
        }
        if (result == 0) throw new InvalidDataException("Invalid Excel cell reference.");
        return result - 1;
    }

    /// <summary>Bound XML text before OpenXML materializes an si/c element as a DOM subtree.</summary>
    internal static void ValidateExcelXmlBounds(Stream stream, bool sharedStrings, CancellationToken token)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            IgnoreComments = true, IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = MaximumFileBytes, CloseInput = false,
        });
        var buffer = new char[8192];
        var elementName = sharedStrings ? "si" : "c";
        var elementDepth = -1;
        var rowDepth = -1;
        long elementCharacters = 0, rowCharacters = 0;
        var elementNodes = 0;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (!sharedStrings && reader.LocalName == "row")
                { rowDepth = reader.Depth; rowCharacters = 0; }
                if (reader.LocalName == elementName && elementDepth < 0)
                { elementDepth = reader.IsEmptyElement ? -1 : reader.Depth; elementCharacters = 0; elementNodes = 0; }
                if (elementDepth >= 0 && (++elementNodes > 8192 || reader.Depth - elementDepth > 64))
                    throw new InvalidDataException("An Excel cell/shared string has too much nested formatting.");
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.Depth == elementDepth) elementDepth = -1;
                if (reader.Depth == rowDepth) rowDepth = -1;
            }
            else if (elementDepth >= 0 && reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA
                         or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                int count;
                while ((count = reader.ReadValueChunk(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    elementCharacters += count;
                    if (elementCharacters > MaximumCellCharacters)
                        throw new InvalidDataException("An Excel source cell/shared string exceeds 1MiB of text.");
                    if (rowDepth >= 0)
                    {
                        rowCharacters += count;
                        if (rowCharacters > MaximumSourceRowCharacters)
                            throw new InvalidDataException("An Excel source row exceeds 8MiB of UTF-16 text.");
                    }
                }
            }
        }
    }
    private static bool IsDateFormat(uint id, IReadOnlyDictionary<uint, string> formats)
    {
        if (id is >= 14 and <= 22 or >= 27 and <= 36 or >= 45 and <= 47 or >= 50 and <= 58) return true;
        if (!formats.TryGetValue(id, out var format)) return false;
        var unquoted = Regex.Replace(format, "\"[^\"]*\"|\\\\.", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return unquoted.IndexOfAny(['y', 'Y', 'd', 'D', 'h', 'H', 's', 'S']) >= 0;
    }

    private static void ValidateExcelArchive(Stream stream)
    {
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("The Excel input exceeds 256MiB.");
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (zip.Entries.Count > 10_000) throw new InvalidDataException("The Excel input has too many package parts.");
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                total = checked(total + entry.Length);
                if (total > 1024L * 1024 * 1024) throw new InvalidDataException("The expanded Excel package exceeds 1GiB.");
                if ((entry.FullName is "xl/workbook.xml" or "xl/styles.xml") && entry.Length > 16L * 1024 * 1024)
                    throw new InvalidDataException("Excel workbook/style metadata exceeds 16MiB.");
            }
        }
        stream.Position = 0;
    }

    private sealed class PrefixTextReader(string prefix, TextReader remaining) : TextReader
    {
        private int _offset;
        public override int Read() => _offset < prefix.Length ? prefix[_offset++] : remaining.Read();
        public override int Peek() => _offset < prefix.Length ? prefix[_offset] : remaining.Peek();
    }
}
