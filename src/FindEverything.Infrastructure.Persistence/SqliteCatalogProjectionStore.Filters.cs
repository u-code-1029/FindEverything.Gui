using System.Collections.Concurrent;
using System.Globalization;
using FindEverything.Application.Catalog;
using FindEverything.Profile.Runtime;
using Microsoft.Data.Sqlite;

namespace FindEverything.Infrastructure.Persistence;

public sealed partial class SqliteCatalogProjectionStore
{
    private sealed record RangeCacheValue(CatalogNumericRange? Value);
    private readonly ConcurrentDictionary<(string Generation, string Field), RangeCacheValue> _numericRanges = new();

    public async Task<IReadOnlySet<string>?> FindMatchingPathsAsync(CatalogProjectionSnapshot snapshot,
        CatalogProjectionQuery query, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count > 500) throw new ArgumentOutOfRangeException(nameof(paths), "A live preview contains at most 500 paths.");
        var capturedPaths = paths.ToArray();
        return await Task.Run(async () =>
        {
            await using var session = new ProjectionQuery(this, snapshot.Generation, snapshot.Scope, snapshot.Fields,
                query, snapshot.IsBuilding, snapshot.IsBuilding ? snapshot.BuildingRevision : null);
            return await session.FindMatchingPathsAsync(capturedPaths, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CatalogNumericRange?> ReadNumericRangeAsync(CatalogProjectionSnapshot snapshot,
        string fieldId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var field = snapshot.Fields.FirstOrDefault(candidate => string.Equals(candidate.FieldId, fieldId, StringComparison.OrdinalIgnoreCase));
        if (field?.Kind is not (ProfileFieldValueKind.Int32 or ProfileFieldValueKind.Decimal))
            throw new ArgumentException("The column is not numeric.", nameof(fieldId));
        var key = (snapshot.Generation, field.FieldId.ToLowerInvariant());
        if (_numericRanges.TryGetValue(key, out var cached)) return cached.Value;
        var result = await Task.Run(() => SqliteReadCancellation.RunAsync(async token =>
        {
            using var connection = OpenConnection(readOperation: true);
            using var interrupt = new SqliteReadCancellation(connection, token);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT sort_numeric FROM projection_cells WHERE generation=$generation AND field_id=$field
                        AND sort_numeric IS NOT NULL ORDER BY sort_numeric ASC LIMIT 1),
                    (SELECT sort_numeric FROM projection_cells WHERE generation=$generation AND field_id=$field
                        AND sort_numeric IS NOT NULL ORDER BY sort_numeric DESC LIMIT 1);
                """;
            command.Parameters.AddWithValue("$generation", snapshot.Generation);
            command.Parameters.AddWithValue("$field", field.FieldId.ToLowerInvariant());
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.IsDBNull(0)) return null;
            return new CatalogNumericRange(DecodeNumericSort(reader.GetString(0)), DecodeNumericSort(reader.GetString(1)));
        }, cancellationToken), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (_numericRanges.Count >= 128) _numericRanges.Clear();
        _numericRanges[key] = new(result);
        return result;
    }

    private static decimal DecodeNumericSort(string value)
    {
        var negative = value[0] == '0';
        var magnitude = value[1..];
        if (negative) magnitude = new string(magnitude.Select(character => character is >= '0' and <= '9'
            ? (char)('9' - (character - '0')) : character).ToArray());
        var parsed = decimal.Parse(magnitude, CultureInfo.InvariantCulture);
        return negative ? -parsed : parsed;
    }

    private sealed partial class ProjectionQuery
    {
        public Task<IReadOnlySet<string>> FindMatchingPathsAsync(IReadOnlyList<string> paths, CancellationToken token) =>
            ExecuteReadAsync<IReadOnlySet<string>>(async cancellation =>
            {
                var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (paths.Count == 0) return results;
                using var connection = _owner.OpenConnection(readOperation: true);
                using var interrupt = new SqliteReadCancellation(connection, cancellation);
                using var transaction = connection.BeginTransaction(deferred: true);
                EnsureGeneration(connection, transaction);
                using var command = BuildCommand(connection, transaction, "user_inputs", null, null, null);
                var names = new List<string>(paths.Count);
                for (var index = 0; index < paths.Count; index++)
                {
                    var name = "$livePath" + index;
                    names.Add(name); command.Parameters.AddWithValue(name, PathKey(paths[index]));
                }
                command.CommandText = "SELECT r.full_path " + command.CommandText + $" AND r.path_key IN ({string.Join(',', names)});";
                using var reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellation).ConfigureAwait(false)) results.Add(reader.GetString(0));
                return results;
            }, token);

        private void AddColumnFilters(SqliteCommand command, string notesTable, ICollection<string> predicates)
        {
            for (var index = 0; index < _filters.Count; index++)
            {
                var filter = _filters[index];
                var prefix = "$filter" + index;
                string expression;
                string? present = null;
                if (filter.Target.Kind == CatalogColumnTargetKind.FullPath)
                {
                    expression = "r.path_search";
                    present = "r.full_path";
                }
                else if (filter.Target.Kind == CatalogColumnTargetKind.Coverage)
                {
                    expression = "r.coverage";
                    present = "r.coverage";
                }
                else
                {
                    command.Parameters.AddWithValue(prefix + "Field", filter.Target.FieldId!.ToLowerInvariant());
                    var displayed = _query.ShowOriginalValues ? "fc.raw_search" : "fc.mapped_search";
                    expression = $"CASE WHEN fc.is_user_input=1 THEN COALESCE(fn.value_search,fc.raw_search) ELSE {displayed} END";
                    present = "CASE WHEN fc.is_user_input=1 THEN COALESCE(fn.value_search,fc.raw_sort) ELSE fc.raw_sort END";
                }
                var condition = filter.Operator switch
                {
                    CatalogFilterOperator.IsMissing => $"{present} IS NULL",
                    CatalogFilterOperator.HasValue => $"{present} IS NOT NULL",
                    CatalogFilterOperator.IsEmpty => $"{present} IS NOT NULL AND {expression}=''",
                    CatalogFilterOperator.IsNotEmpty => $"{present} IS NOT NULL AND {expression}<>''",
                    CatalogFilterOperator.Contains or CatalogFilterOperator.DoesNotContain => TextCondition(),
                    CatalogFilterOperator.BooleanEquals => BooleanCondition("fc.sort_numeric", NumericSort(filter.Boolean!.Value)!),
                    CatalogFilterOperator.CoverageEquals => BooleanCondition(expression, filter.Boolean!.Value ? 1 : 0),
                    CatalogFilterOperator.NumberRange => RangeCondition("fc.sort_numeric COLLATE BINARY", filter.Minimum is { } minimum ? NumericSort(minimum) : null,
                        filter.Maximum is { } maximum ? NumericSort(maximum) : null, false),
                    CatalogFilterOperator.DateRange => RangeCondition("fc.sort_ticks", filter.StartDate?.Date.Ticks,
                        filter.EndDate is { } end && end.Date < DateTime.MaxValue.Date ? end.Date.AddDays(1).Ticks : null, true),
                    _ => throw new ArgumentException("Unsupported filter.")
                };
                if (filter.Target.Kind == CatalogColumnTargetKind.ProfileField)
                    predicates.Add($"""
                        EXISTS(SELECT 1 FROM projection_cells fc LEFT JOIN {notesTable} fn
                        ON fc.is_user_input=1 AND fn.profile_key=$profile AND fn.path_key=fc.path_key AND fn.field_id=fc.field_id
                        WHERE fc.generation=r.generation AND fc.path_key=r.path_key AND fc.field_id={prefix}Field AND ({condition}))
                        """);
                else predicates.Add("(" + condition + ")");

                string TextCondition()
                {
                    command.Parameters.AddWithValue(prefix + "Text", (filter.Text ?? string.Empty).ToLowerInvariant());
                    return $"{present} IS NOT NULL AND instr({expression},{prefix}Text){(filter.Operator == CatalogFilterOperator.Contains ? ">0" : "=0")}";
                }
                string BooleanCondition(string column, object value)
                {
                    command.Parameters.AddWithValue(prefix + "Value", value);
                    return $"{column}={prefix}Value";
                }
                string RangeCondition(string column, object? minimum, object? maximum, bool endExclusive)
                {
                    var bounds = new List<string> { $"{column} IS NOT NULL" };
                    if (minimum is not null)
                    {
                        command.Parameters.AddWithValue(prefix + "Min", minimum);
                        bounds.Add($"{column}>={prefix}Min");
                    }
                    if (maximum is not null)
                    {
                        command.Parameters.AddWithValue(prefix + "Max", maximum);
                        bounds.Add($"{column}{(endExclusive ? "<" : "<=")}{prefix}Max");
                    }
                    return string.Join(" AND ", bounds);
                }
            }
        }
    }
}
