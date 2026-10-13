using System.Globalization;
using FindEverything.Application.Catalog;
using FindEverything.Profile.Runtime;
using Microsoft.Data.Sqlite;

namespace FindEverything.Infrastructure.Persistence;

public sealed partial class SqliteCatalogProjectionStore
{
    private sealed partial class ProjectionQuery : ICatalogSelectionMatchQuery
    {
        public Task<CatalogSelectionMatchResult> CreateSelectionMatchAsync(CatalogSelectionMatchInput input,
            Func<string, string>? selectionKeyMapper = null, CancellationToken cancellationToken = default) =>
            ExecuteReadAsync(token => MatchSelectionCoreAsync(input, selectionKeyMapper, token), cancellationToken);

        private async Task<CatalogSelectionMatchResult> MatchSelectionCoreAsync(CatalogSelectionMatchInput input,
            Func<string, string>? keyMapper, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(input);
            if (_building) throw new InvalidOperationException("Data-based selection requires published scan results.");
            if (input.Columns.Count is < 1 or > ProfileSelectionMatchRules.MaximumColumns)
                throw new ArgumentException("A selection rule must map 1–32 columns.", nameof(input));
            var fields = input.Columns.Select(column => _fields.FirstOrDefault(field =>
                field.FieldId.Equals(column.FieldId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown selection field '{column.FieldId}'.", nameof(input))).ToArray();
            if (fields.Select(field => field.FieldId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Length)
                throw new ArgumentException("A selection rule cannot map a field more than once.", nameof(input));
            var culture = CultureInfo.GetCultureInfo(input.CultureName);
            if (input.Columns.Any(column => !Enum.IsDefined(column.ValueSource) || !Enum.IsDefined(column.DateComparison)
                || column.DateFormat is { Length: > 128 }))
                throw new ArgumentException("A selection column has invalid comparison settings.", nameof(input));
            var directory = Directory.CreateTempSubdirectory("FindEverything-selection-").FullName;
            var destination = Path.Combine(directory, "keys.db");
            var completed = false;
            try
            {
                using var connection = _owner.OpenConnection(readOperation: true);
                using var interrupt = new SqliteReadCancellation(connection, token);
                connection.CreateFunction<string?, string?>("fe_selection_text", value => value?.Trim().ToLowerInvariant(), isDeterministic: true);
                using var transaction = connection.BeginTransaction(deferred: true);
                EnsureGeneration(connection, transaction);
                var columnNames = Enumerable.Range(0, fields.Length).Select(index => "v" + index).ToArray();
                using var inputCommand = connection.CreateCommand();
                inputCommand.Transaction = transaction;
                inputCommand.CommandText = $"CREATE TEMP TABLE selection_input(input_id INTEGER PRIMARY KEY,{string.Join(',', columnNames.Select(name => name + " TEXT NOT NULL"))},UNIQUE({string.Join(',', columnNames)}));";
                await inputCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                inputCommand.CommandText = $"INSERT OR IGNORE INTO selection_input(input_id,{string.Join(',', columnNames)}) VALUES($id,{string.Join(',', columnNames.Select(name => "$" + name))});";
                var idParameter = inputCommand.Parameters.Add("$id", SqliteType.Integer);
                var valueParameters = columnNames.Select(name => inputCommand.Parameters.Add("$" + name, SqliteType.Text)).ToArray();
                inputCommand.Prepare();
                long inputRows = 0, valid = 0, invalid = 0, duplicates = 0, inputBytes = 0;
                var issues = new List<CatalogSelectionInputIssue>();
                await foreach (var row in input.Rows.WithCancellation(token).ConfigureAwait(false))
                {
                    token.ThrowIfCancellationRequested();
                    if (++inputRows > 200_000) throw new InvalidDataException("Selection input exceeds 200,000 rows.");
                    if (row.Values.Count != fields.Length)
                    { Reject(row.RowNumber, "The input does not contain every mapped column."); continue; }
                    var normalized = new string[fields.Length];
                    var validRow = true;
                    for (var index = 0; index < fields.Length; index++)
                    {
                        if (!TryNormalize(row.Values[index], fields[index], input.Columns[index], culture,
                                out var value, out var error))
                        { Reject(row.RowNumber, fields[index].Header + ": " + error); validRow = false; break; }
                        normalized[index] = value!;
                    }
                    if (!validRow) continue;
                    inputBytes += normalized.Sum(value => 2L * value.Length);
                    if (inputBytes > 256L * 1024 * 1024) throw new InvalidDataException("Selection input exceeds 256MiB of normalized data.");
                    idParameter.Value = inputRows;
                    for (var index = 0; index < fields.Length; index++) valueParameters[index].Value = normalized[index];
                    var inserted = await inputCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    valid++;
                    if (inserted == 0) duplicates++;
                }

                using var output = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = destination, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
                await output.OpenAsync(token).ConfigureAwait(false);
                using var outputInterrupt = new SqliteReadCancellation(output, token);
                using var outputTransaction = output.BeginTransaction();
                using var append = output.CreateCommand();
                append.Transaction = outputTransaction;
                append.CommandText = """
                    CREATE TABLE selection_baseline(stable_key TEXT PRIMARY KEY COLLATE BINARY,query_key TEXT NOT NULL UNIQUE COLLATE BINARY) WITHOUT ROWID;
                    CREATE TABLE matched_inputs(input_id INTEGER PRIMARY KEY,matches INTEGER NOT NULL);
                    """;
                await append.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                append.CommandText = "INSERT OR IGNORE INTO selection_baseline(stable_key,query_key) VALUES($stable,$query);";
                var stable = append.Parameters.Add("$stable", SqliteType.Text);
                var key = append.Parameters.Add("$query", SqliteType.Text);
                append.Prepare();
                using var hits = output.CreateCommand();
                hits.Transaction = outputTransaction;
                hits.CommandText = "INSERT INTO matched_inputs(input_id,matches) VALUES($id,1) ON CONFLICT(input_id) DO UPDATE SET matches=matches+1;";
                var hitId = hits.Parameters.Add("$id", SqliteType.Integer);
                hits.Prepare();
                using var matches = BuildCommand(connection, transaction, "user_inputs", null, null, null);
                var expressions = new string[fields.Length];
                for (var index = 0; index < fields.Length; index++)
                {
                    var field = fields[index];
                    var mapping = input.Columns[index];
                    var fieldParameter = "$selectionField" + index;
                    matches.Parameters.AddWithValue(fieldParameter, field.FieldId.ToLowerInvariant());
                    var text = mapping.ValueSource == ProfileSelectionValueSource.Original ? "mc.raw_search" : "mc.mapped_search";
                    var value = field.Kind switch
                    {
                        ProfileFieldValueKind.String => $"fe_selection_text(CASE WHEN mc.is_user_input=1 THEN COALESCE(mn.value_search,mc.raw_sort) WHEN mc.raw_sort IS NULL THEN NULL ELSE {text} END)",
                        ProfileFieldValueKind.DateTime => mapping.DateComparison == ProfileSelectionDateComparison.DateOnly
                            ? "CAST(mc.sort_ticks/864000000000 AS TEXT)" : "CAST(mc.sort_ticks AS TEXT)",
                        _ => "mc.sort_numeric"
                    };
                    expressions[index] = $"i.v{index}=(SELECT {value} FROM projection_cells mc LEFT JOIN user_inputs mn ON mc.is_user_input=1 AND mn.profile_key=$profile AND mn.path_key=mc.path_key AND mn.field_id=mc.field_id WHERE mc.generation=r.generation AND mc.path_key=r.path_key AND mc.field_id={fieldParameter})";
                }
                var mainWhere = matches.CommandText.IndexOf(" WHERE ", StringComparison.Ordinal);
                matches.CommandText = "SELECT r.full_path,r.path_key,i.input_id " + matches.CommandText.Insert(mainWhere,
                    " JOIN selection_input i ON " + string.Join(" AND ", expressions));
                using (var reader = await matches.ExecuteReaderAsync(token).ConfigureAwait(false))
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        token.ThrowIfCancellationRequested();
                        var path = reader.GetString(0);
                        stable.Value = PathKey(keyMapper?.Invoke(path) ?? path);
                        key.Value = reader.GetString(1);
                        await append.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        hitId.Value = reader.GetInt64(2);
                        await hits.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                EnsureGeneration(connection, transaction);
                using var summary = output.CreateCommand();
                summary.Transaction = outputTransaction;
                summary.CommandText = "SELECT (SELECT COUNT(*) FROM selection_baseline),(SELECT COUNT(*) FROM matched_inputs),(SELECT COUNT(*) FROM matched_inputs WHERE matches>1);";
                long matching, matchedInputs, ambiguous;
                using (var reader = await summary.ExecuteReaderAsync(token).ConfigureAwait(false))
                { await reader.ReadAsync(token).ConfigureAwait(false); matching = reader.GetInt64(0); matchedInputs = reader.GetInt64(1); ambiguous = reader.GetInt64(2); }
                summary.CommandText = "DROP TABLE matched_inputs;";
                await summary.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                outputTransaction.Commit();
                transaction.Commit();
                output.Close();
                var result = new CatalogSelectionMatchResult(new SqliteSelectionKeySet(destination, matching),
                    new(inputRows, valid, invalid, duplicates, matchedInputs, Math.Max(0, valid - duplicates - matchedInputs),
                        ambiguous, matching, issues));
                completed = true;
                return result;

                void Reject(long number, string message)
                { invalid++; if (issues.Count < 100) issues.Add(new(number, message)); }
            }
            finally
            {
                if (!completed)
                {
                    try { File.Delete(destination); } catch (IOException) { }
                    try { Directory.Delete(directory); } catch (IOException) { }
                }
            }
        }

        private static bool TryNormalize(string? text, ProfileFieldDescriptor field, CatalogSelectionMatchColumn column,
            CultureInfo culture, out string? value, out string? error)
        {
            value = null; error = null;
            text = text?.Trim();
            if (string.IsNullOrEmpty(text)) { error = "An empty value cannot be used as a wildcard."; return false; }
            if (text.Length > 1024 * 1024) { error = "A cell exceeds 1MiB of text."; return false; }
            switch (field.Kind)
            {
                case ProfileFieldValueKind.String: value = text.ToLowerInvariant(); return true;
                case ProfileFieldValueKind.Int32:
                    if (int.TryParse(text, NumberStyles.Number, culture, out var integer)) { value = NumericSort(integer); return true; }
                    break;
                case ProfileFieldValueKind.Decimal:
                    if (decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowExponent | NumberStyles.AllowCurrencySymbol, culture, out var number))
                    { value = NumericSort(number); return true; }
                    break;
                case ProfileFieldValueKind.DateTime:
                    DateTime date;
                    var parsed = !string.IsNullOrWhiteSpace(column.DateFormat)
                        ? DateTime.TryParseExact(text, column.DateFormat, culture, DateTimeStyles.AllowWhiteSpaces, out date)
                        : DateTime.TryParse(text, culture, DateTimeStyles.AllowWhiteSpaces, out date);
                    if (!parsed) parsed = DateTime.TryParseExact(text, "O", CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out date);
                    if (parsed) { value = (column.DateComparison == ProfileSelectionDateComparison.DateOnly
                        ? date.Date.Ticks / TimeSpan.TicksPerDay : date.Ticks).ToString(CultureInfo.InvariantCulture); return true; }
                    break;
                case ProfileFieldValueKind.Boolean:
                    if (bool.TryParse(text, out var boolean)) { value = NumericSort(boolean); return true; }
                    if (text is "1" or "yes" or "Yes" or "예") { value = NumericSort(true); return true; }
                    if (text is "0" or "no" or "No" or "아니오") { value = NumericSort(false); return true; }
                    break;
            }
            error = $"'{text[..Math.Min(text.Length, 80)]}' is not a valid {field.Kind} value.";
            return false;
        }
    }
}
