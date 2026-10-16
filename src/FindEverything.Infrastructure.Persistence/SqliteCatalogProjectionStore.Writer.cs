using System.Globalization;
using System.Text.Json;
using FindEverything.Application.Catalog;
using FindEverything.Application.Results;
using FindEverything.Profile.Runtime;
using Microsoft.Data.Sqlite;

namespace FindEverything.Infrastructure.Persistence;

public sealed partial class SqliteCatalogProjectionStore
{
    private sealed class ProjectionWriter(SqliteCatalogProjectionStore owner, string generation,
        CatalogProjectionScope scope, IReadOnlyList<ProfileFieldDescriptor> fields, string? previous)
        : ICatalogProjectionWriter
    {
        private readonly List<CatalogProjectionRow> _pending = new(BatchSize);
        private const long MaximumPendingBytes = 8L * 1024 * 1024;
        private long _pendingBytes;
        private bool _published;
        private bool _disposed;
        private int _count;
        private long _revision;
        public SqliteCatalogProjectionStore Owner { get; } = owner;
        public string Generation { get; } = generation;
        public int Count => Volatile.Read(ref _count);
        public long Revision => Volatile.Read(ref _revision);

        public void Append(CatalogItem item) => AppendRow(new CatalogProjectionRow(item.FullPath,
            item.RelativePath, item.MatchedRuleId, item.Values, item.CoveragePending, item.FieldErrors) { Resources = item.Resources });

        public void AppendRow(CatalogProjectionRow row)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_published) throw new InvalidOperationException("A published generation cannot be edited.");
            var estimatedBytes = 512L + 2L * (row.FullPath.Length + row.RelativePath.Length + row.MatchedRuleId.Length)
                + row.Values.Sum(pair => 2L * pair.Key.Length + (pair.Value is string text ? 4L * text.Length : 64L));
            if (_pending.Count > 0 && _pendingBytes + estimatedBytes > MaximumPendingBytes) Flush();
            _pending.Add(row);
            _pendingBytes += estimatedBytes;
            Interlocked.Increment(ref _count);
            if (_pending.Count >= BatchSize || _pendingBytes >= MaximumPendingBytes) Flush();
        }

        public void Flush()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending.Count == 0) return;
            Owner._writeGate.Wait();
            try
            {
                using var connection = Owner.OpenConnection();
                using var transaction = connection.BeginTransaction();
                using var rowCommand = connection.CreateCommand();
                rowCommand.Transaction = transaction;
                rowCommand.CommandText = """
                    INSERT INTO projection_rows(generation, path_key, full_path, path_search,
                        relative_path, rule_id, values_json, errors_json, coverage, resources_json)
                    VALUES($generation, $path, $full, $search, $relative, $rule, $values, $errors, $coverage, $resources)
                    ON CONFLICT(generation, path_key) DO UPDATE SET full_path=excluded.full_path,
                        path_search=excluded.path_search, relative_path=excluded.relative_path,
                        rule_id=excluded.rule_id, values_json=excluded.values_json,
                        errors_json=excluded.errors_json, coverage=excluded.coverage, resources_json=excluded.resources_json;
                    """;
                rowCommand.Parameters.AddWithValue("$generation", Generation);
                foreach (var name in new[] { "$path", "$full", "$search", "$relative", "$rule", "$values", "$errors", "$coverage", "$resources" })
                    rowCommand.Parameters.Add(name, name == "$coverage" ? SqliteType.Integer : SqliteType.Text);
                rowCommand.Prepare();
                using var cellCommand = connection.CreateCommand();
                cellCommand.Transaction = transaction;
                cellCommand.CommandText = """
                    INSERT INTO projection_cells(generation, path_key, field_id, raw_search,
                        mapped_search, sort_numeric, sort_ticks, raw_sort, mapped_sort, is_user_input)
                    VALUES($generation, $path, $field, $raw, $mapped, $number, $ticks, $rawSort, $mappedSort, $input)
                    ON CONFLICT(generation, path_key, field_id) DO UPDATE SET raw_search=excluded.raw_search,
                        mapped_search=excluded.mapped_search, sort_numeric=excluded.sort_numeric,
                        sort_ticks=excluded.sort_ticks, raw_sort=excluded.raw_sort,
                        mapped_sort=excluded.mapped_sort, is_user_input=excluded.is_user_input;
                    """;
                cellCommand.Parameters.AddWithValue("$generation", Generation);
                foreach (var name in new[] { "$path", "$field", "$raw", "$mapped", "$number", "$rawSort", "$mappedSort" })
                    cellCommand.Parameters.Add(name, SqliteType.Text);
                cellCommand.Parameters.Add("$ticks", SqliteType.Integer);
                cellCommand.Parameters.Add("$input", SqliteType.Integer);
                cellCommand.Prepare();
                var culture = CultureInfo.GetCultureInfo(scope.CultureName);
                foreach (var row in _pending)
                {
                    var key = PathKey(row.FullPath);
                    rowCommand.Parameters["$path"].Value = key;
                    rowCommand.Parameters["$full"].Value = row.FullPath;
                    rowCommand.Parameters["$search"].Value = row.FullPath.ToLowerInvariant();
                    rowCommand.Parameters["$relative"].Value = row.RelativePath;
                    rowCommand.Parameters["$rule"].Value = row.MatchedRuleId;
                    rowCommand.Parameters["$values"].Value = SerializeValues(row.Values);
                    rowCommand.Parameters["$errors"].Value = JsonSerializer.Serialize(row.FieldErrors ?? new Dictionary<string, string>());
                    rowCommand.Parameters["$resources"].Value = JsonSerializer.Serialize(row.Resources);
                    rowCommand.Parameters["$coverage"].Value = row.CoveragePending ? 1 : 0;
                    rowCommand.ExecuteNonQuery();
                    foreach (var field in fields)
                    {
                        row.Values.TryGetValue(field.FieldId, out var value);
                        if (field.SourceKind == ProfileFieldSourceKind.UserInput) value = field.DefaultValue ?? string.Empty;
                        var raw = FormatValue(value, field.DisplayFormat, culture);
                        var mapped = value is string text && field.ValueMappings.TryGetValue(text, out var alias)
                            ? alias : raw;
                        cellCommand.Parameters["$path"].Value = key;
                        cellCommand.Parameters["$field"].Value = field.FieldId.ToLowerInvariant();
                        cellCommand.Parameters["$raw"].Value = raw.ToLowerInvariant();
                        cellCommand.Parameters["$mapped"].Value = mapped.ToLowerInvariant();
                        cellCommand.Parameters["$rawSort"].Value = value is null ? DBNull.Value : raw.ToLowerInvariant();
                        cellCommand.Parameters["$mappedSort"].Value = value is null ? DBNull.Value : mapped.ToLowerInvariant();
                        cellCommand.Parameters["$number"].Value = (object?)NumericSort(value) ?? DBNull.Value;
                        cellCommand.Parameters["$ticks"].Value = value switch
                        {
                            DateTime date => date.Ticks,
                            DateTimeOffset date => date.UtcTicks,
                            _ => DBNull.Value
                        };
                        cellCommand.Parameters["$input"].Value = field.SourceKind == ProfileFieldSourceKind.UserInput ? 1 : 0;
                        cellCommand.ExecuteNonQuery();
                    }
                }
                using var count = connection.CreateCommand();
                count.Transaction = transaction;
                count.CommandText = "UPDATE projection_generations SET row_count=(SELECT COUNT(*) FROM projection_rows WHERE generation=$generation) WHERE generation=$generation RETURNING row_count;";
                count.Parameters.AddWithValue("$generation", Generation);
                var storedCount = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
                transaction.Commit();
                Volatile.Write(ref _count, storedCount);
                _pending.Clear();
                _pendingBytes = 0;
            }
            finally { Owner._writeGate.Release(); }
        }

        public void Reset()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_published) throw new InvalidOperationException("A published generation cannot be reset.");
            Owner._writeGate.Wait();
            try
            {
                using var connection = Owner.OpenConnection();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM projection_rows WHERE generation=$generation; UPDATE projection_generations SET row_count=0, revision=revision+1 WHERE generation=$generation;";
                command.Parameters.AddWithValue("$generation", Generation);
                command.ExecuteNonQuery();
                transaction.Commit();
                _pending.Clear();
                _pendingBytes = 0;
                Volatile.Write(ref _count, 0);
                Interlocked.Increment(ref _revision);
            }
            finally { Owner._writeGate.Release(); }
        }

        public CatalogProjectionSnapshot Publish(CatalogProjectionSummary summary,
            CatalogProjectionScope? publishedScope = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_published) throw new InvalidOperationException("The generation is already published.");
            var finalScope = publishedScope ?? scope;
            finalScope.ValidateRootPaths();
            if (finalScope with { IndexScanId = scope.IndexScanId } != scope)
                throw new ArgumentException("Only the final index generation can change when publishing.", nameof(publishedScope));
            Flush();
            Owner._writeGate.Wait();
            try
            {
                using var connection = Owner.OpenConnection();
                using var transaction = connection.BeginTransaction();
                var actual = CurrentGeneration(connection, transaction, scope.Key);
                if (actual != previous) throw new ResultQueryChangedException(previous ?? Generation, actual);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE projection_generations SET summary_json=$summary, scope_json=$scopeJson, state=1 WHERE generation=$generation;
                    DELETE FROM projection_publications WHERE scope_key<>$scope AND generation IN
                        (SELECT generation FROM projection_generations WHERE logical_key=$logical);
                    INSERT INTO projection_publications(scope_key, generation) VALUES($scope, $generation)
                    ON CONFLICT(scope_key) DO UPDATE SET generation=excluded.generation;
                    """;
                command.Parameters.AddWithValue("$summary", JsonSerializer.Serialize(summary));
                command.Parameters.AddWithValue("$generation", Generation);
                command.Parameters.AddWithValue("$scope", scope.Key);
                command.Parameters.AddWithValue("$logical", LogicalScopeKey(scope));
                command.Parameters.AddWithValue("$scopeJson", JsonSerializer.Serialize(finalScope));
                command.ExecuteNonQuery();
                transaction.Commit();
                _published = true;
                Owner.Prune(connection);
                return new CatalogProjectionSnapshot(Generation, finalScope, summary, fields) { Revision = Revision };
            }
            finally { Owner._writeGate.Release(); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pending.Clear();
            _pendingBytes = 0;
            if (_published || Owner._disposed) return;
            Owner._writeGate.Wait();
            try
            {
                using var connection = Owner.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM projection_generations WHERE generation=$generation AND state=0;";
                command.Parameters.AddWithValue("$generation", Generation);
                command.ExecuteNonQuery();
            }
            finally { Owner._writeGate.Release(); }
        }
    }

    private sealed record StoredValue(string Type, string? Text);

    private static string SerializeValues(IReadOnlyDictionary<string, object?> values) => JsonSerializer.Serialize(
        values.ToDictionary(pair => pair.Key, pair => EncodeValue(pair.Value), StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<string, object?> DeserializeValues(string json) => Deserialize<Dictionary<string, StoredValue>>(json)
        .ToDictionary(pair => pair.Key, pair => DecodeValue(pair.Value), StringComparer.OrdinalIgnoreCase);

    private static StoredValue EncodeValue(object? value) => value switch
    {
        null => new("null", null),
        string text => new("string", text),
        int number => new("int", number.ToString(CultureInfo.InvariantCulture)),
        long number => new("long", number.ToString(CultureInfo.InvariantCulture)),
        decimal number => new("decimal", number.ToString(CultureInfo.InvariantCulture)),
        bool boolean => new("bool", boolean ? "true" : "false"),
        DateTime date => new("date", date.ToString("O", CultureInfo.InvariantCulture)),
        DateTimeOffset date => new("offset", date.ToString("O", CultureInfo.InvariantCulture)),
        _ => throw new InvalidDataException($"Unsupported profile value type '{value.GetType().Name}'.")
    };

    private static object? DecodeValue(StoredValue value) => value.Type switch
    {
        "null" => null,
        "string" => value.Text,
        "int" => int.Parse(value.Text!, CultureInfo.InvariantCulture),
        "long" => long.Parse(value.Text!, CultureInfo.InvariantCulture),
        "decimal" => decimal.Parse(value.Text!, CultureInfo.InvariantCulture),
        "bool" => bool.Parse(value.Text!),
        "date" => DateTime.Parse(value.Text!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        "offset" => DateTimeOffset.Parse(value.Text!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        _ => throw new InvalidDataException("The catalog contains an unsupported value type.")
    };

    private static string FormatValue(object? value, string? format, CultureInfo culture)
    {
        if (value is null) return "—";
        try { return value is IFormattable formattable ? formattable.ToString(format, culture) ?? string.Empty : Convert.ToString(value, culture) ?? string.Empty; }
        catch (FormatException) { return Convert.ToString(value, culture) ?? string.Empty; }
    }

    // Fixed-width decimal keys preserve all 28 fractional digits without double rounding.
    private static string? NumericSort(object? value)
    {
        decimal number;
        if (value is int integer) number = integer;
        else if (value is long longInteger) number = longInteger;
        else if (value is decimal precise) number = precise;
        else if (value is bool boolean) number = boolean ? 1 : 0;
        else return null;
        var magnitude = Math.Abs(number).ToString("0." + new string('0', 28), CultureInfo.InvariantCulture).PadLeft(58, '0');
        return number >= 0 ? "1" + magnitude : "0" + new string(magnitude.Select(character =>
            character is >= '0' and <= '9' ? (char)('9' - (character - '0')) : character).ToArray());
    }
}
