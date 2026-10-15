using System.Globalization;
using System.Runtime.CompilerServices;
using FindEverything.Application.Catalog;
using FindEverything.Application.Results;
using FindEverything.Profile.Runtime;
using Microsoft.Data.Sqlite;

namespace FindEverything.Infrastructure.Persistence;

public sealed partial class SqliteCatalogProjectionStore
{
    private sealed partial class ProjectionQuery : IResultQuerySession<CatalogProjectionRow>, IResultSelectionKeyReader
    {
        private readonly SqliteCatalogProjectionStore _owner;
        private readonly string _generation;
        private readonly CatalogProjectionScope _scope;
        private readonly IReadOnlyList<ProfileFieldDescriptor> _inputFields;
        private readonly IReadOnlyList<ProfileFieldDescriptor> _fields;
        private readonly IReadOnlyList<string> _textFieldIds;
        private readonly CatalogProjectionQuery _query;
        private readonly ProfileFieldDescriptor? _sortField;
        private readonly CatalogColumnTarget _sortTarget;
        private readonly IReadOnlyList<CatalogColumnFilter> _filters;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly bool _building;
        private readonly string[] _terms;
        private readonly long _buildingRevision;
        private readonly long _notesRevision;
        private int _disposed;

        public ProjectionQuery(SqliteCatalogProjectionStore owner, string generation,
            CatalogProjectionScope scope, IReadOnlyList<ProfileFieldDescriptor> fields,
            CatalogProjectionQuery query, bool building, long? expectedRevision = null)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (query.TextPreviewCharacters < 0)
                throw new ArgumentOutOfRangeException(nameof(query), "A text preview limit cannot be negative.");
            _owner = owner;
            _generation = generation;
            _scope = scope;
            _fields = fields.ToArray();
            _inputFields = fields.Where(field => field.SourceKind == ProfileFieldSourceKind.UserInput).ToArray();
            _textFieldIds = fields.Where(field => field.SourceKind == ProfileFieldSourceKind.TextFileContent)
                .Select(field => field.FieldId.ToLowerInvariant()).ToArray();
            _query = query;
            _building = building;
            _sortTarget = query.SortTarget ?? (query.SortFieldId is null or "FullPath" or "fullPath"
                ? CatalogColumnTarget.FullPath : query.SortFieldId == "CoveragePending"
                    ? CatalogColumnTarget.Coverage : CatalogColumnTarget.Field(query.SortFieldId));
            _sortField = _sortTarget.Kind == CatalogColumnTargetKind.ProfileField
                ? fields.FirstOrDefault(field => string.Equals(field.FieldId, _sortTarget.FieldId, StringComparison.OrdinalIgnoreCase)) : null;
            if (!Enum.IsDefined(_sortTarget.Kind) || (_sortTarget.Kind == CatalogColumnTargetKind.ProfileField && _sortField is null))
                throw new ArgumentException("The sort field is not defined by the profile.", nameof(query));
            _filters = query.Filters.ToArray();
            foreach (var filter in _filters) CatalogColumnFilters.Validate(filter, fields);
            if (_filters.Select(filter => filter.Target).Distinct().Count() != _filters.Count)
                throw new ArgumentException("A column may have only one active condition.", nameof(query));
            _terms = (query.Keyword ?? string.Empty).Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(term => term.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
            _owner._leases.AddOrUpdate(generation, 1, static (_, value) => value + 1);
            try
            {
                using var connection = _owner.OpenConnection(readOperation: true);
                using var transaction = connection.BeginTransaction(deferred: true);
                _notesRevision = NotesRevision(connection, transaction, _scope.ProfileId);
                {
                    using var revision = connection.CreateCommand();
                    revision.Transaction = transaction;
                    revision.CommandText = "SELECT revision FROM projection_generations WHERE generation=$generation;";
                    revision.Parameters.AddWithValue("$generation", _generation);
                    _buildingRevision = Convert.ToInt64(revision.ExecuteScalar(), CultureInfo.InvariantCulture);
                    if (expectedRevision.HasValue && _buildingRevision != expectedRevision.Value)
                        throw new ResultQueryChangedException(generation + ":" + expectedRevision, generation + ":" + _buildingRevision);
                }
                EnsureGeneration(connection, transaction);
            }
            catch
            {
                ReleaseLease();
                throw;
            }
        }

        public string Identity => _building || _buildingRevision != 0 ? _generation + ":" + _buildingRevision : _generation;

        public Task<long> CountAsync(CancellationToken cancellationToken = default) => ExecuteReadAsync(CountCoreAsync, cancellationToken);

        public async IAsyncEnumerable<ResultSelectionKey> EnumerateSelectionKeysAsync(QuerySelection selection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var captured = selection.Capture();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var token = linked.Token;
            using var connection = _owner.OpenConnection(readOperation: true);
            using var interrupt = new SqliteReadCancellation(connection, token);
            if (captured.Baseline is { } baseline)
            {
                using var attach = connection.CreateCommand();
                attach.CommandText = "ATTACH DATABASE $path AS selection_source;";
                attach.Parameters.AddWithValue("$path", baseline.DatabasePath); attach.ExecuteNonQuery();
            }
            using var transaction = connection.BeginTransaction(deferred: true);
            EnsureGeneration(connection, transaction);
            using var prepare = connection.CreateCommand(); prepare.Transaction = transaction;
            prepare.CommandText = "CREATE TEMP TABLE selection_keys(path_key TEXT PRIMARY KEY COLLATE BINARY); CREATE TEMP TABLE selection_unavailable(path_key TEXT PRIMARY KEY COLLATE BINARY);";
            prepare.ExecuteNonQuery();
            foreach (var (table, keys) in new[] { ("selection_keys", captured.Keys), ("selection_unavailable", captured.UnavailableKeys) })
            {
                prepare.CommandText = $"INSERT OR IGNORE INTO {table}(path_key) VALUES($key);";
                prepare.Parameters.Clear(); var key = prepare.Parameters.Add("$key", SqliteType.Text);
                foreach (var value in keys)
                {
                    token.ThrowIfCancellationRequested(); key.Value = PathKey(value); prepare.ExecuteNonQuery();
                }
            }
            using var command = BuildCommand(connection, transaction, "user_inputs", captured, null, null);
            command.CommandText = "SELECT r.full_path " + command.CommandText;
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                var path = reader.GetString(0);
                yield return new(path, path);
            }
        }

        public async Task<long> CountSelectionAsync(QuerySelection selection, CancellationToken cancellationToken = default)
        {
            using var captured = selection.Capture();
            return await ExecuteReadAsync(async token =>
            {
                using var connection = _owner.OpenConnection(readOperation: true);
                using var interrupt = new SqliteReadCancellation(connection, token);
                if (captured.Baseline is { } baseline)
                {
                    using var attach = connection.CreateCommand();
                    attach.CommandText = "ATTACH DATABASE $path AS selection_source;";
                    attach.Parameters.AddWithValue("$path", baseline.DatabasePath);
                    attach.ExecuteNonQuery();
                }
                using var transaction = connection.BeginTransaction(deferred: true);
                EnsureGeneration(connection, transaction);
                using var prepare = connection.CreateCommand();
                prepare.Transaction = transaction;
                prepare.CommandText = "CREATE TEMP TABLE selection_keys(path_key TEXT PRIMARY KEY COLLATE BINARY); CREATE TEMP TABLE selection_unavailable(path_key TEXT PRIMARY KEY COLLATE BINARY);";
                prepare.ExecuteNonQuery();
                foreach (var (table, keys) in new[] { ("selection_keys", captured.Keys), ("selection_unavailable", captured.UnavailableKeys) })
                {
                    prepare.CommandText = $"INSERT OR IGNORE INTO {table}(path_key) VALUES($key);";
                    prepare.Parameters.Clear();
                    var key = prepare.Parameters.Add("$key", SqliteType.Text);
                    foreach (var value in keys)
                    {
                        token.ThrowIfCancellationRequested();
                        key.Value = PathKey(value);
                        prepare.ExecuteNonQuery();
                    }
                }
                using var count = BuildCommand(connection, transaction, "user_inputs", captured, null, null);
                count.CommandText = "SELECT COUNT(*) " + count.CommandText;
                return Convert.ToInt64(await count.ExecuteScalarAsync(token).ConfigureAwait(false));
            }, cancellationToken).ConfigureAwait(false);
        }

        private async Task<long> CountCoreAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = _owner.OpenConnection(readOperation: true);
            using var interrupt = new SqliteReadCancellation(connection, cancellationToken);
            using var transaction = connection.BeginTransaction(deferred: true);
            EnsureGeneration(connection, transaction);
            if (_terms.Length == 0 && _filters.Count == 0)
            {
                using var count = connection.CreateCommand();
                count.Transaction = transaction;
                count.CommandText = "SELECT row_count FROM projection_generations WHERE generation=$generation;";
                count.Parameters.AddWithValue("$generation", _generation);
                return Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }
            using var command = BuildCommand(connection, transaction, "user_inputs", null, null, null);
            command.CommandText = "SELECT COUNT(*) " + command.CommandText;
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        public Task<ResultPage<CatalogProjectionRow>> ReadRangeAsync(int offset, int limit,
            CancellationToken cancellationToken = default) => ExecuteReadAsync(async token =>
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ValidateRange(offset, limit);
            using var connection = _owner.OpenConnection(readOperation: true);
            using var interrupt = new SqliteReadCancellation(connection, token);
            return await ReadPageAsync(connection, "user_inputs", null, offset, limit,
                checkPublication: true, previewCharacters: _query.TextPreviewCharacters, token).ConfigureAwait(false);
        }, cancellationToken);

        public async Task<ResultPage<CatalogProjectionRow>> ReadSelectionRangeAsync(QuerySelection selection,
            int offset, int limit, CancellationToken cancellationToken = default)
        {
            using var captured = selection.Capture();
            return await ExecuteReadAsync(async token =>
            {
                ValidateRange(offset, limit);
                using var connection = _owner.OpenConnection(readOperation: true);
                using var interrupt = new SqliteReadCancellation(connection, token);
                if (captured.Baseline is { } baseline)
                {
                    using var attach = connection.CreateCommand();
                    attach.CommandText = "ATTACH DATABASE $path AS selection_source;";
                    attach.Parameters.AddWithValue("$path", baseline.DatabasePath); attach.ExecuteNonQuery();
                }
                using var prepare = connection.CreateCommand();
                prepare.CommandText = "CREATE TEMP TABLE selection_keys(path_key TEXT PRIMARY KEY COLLATE BINARY) WITHOUT ROWID; CREATE TEMP TABLE selection_unavailable(path_key TEXT PRIMARY KEY COLLATE BINARY) WITHOUT ROWID;";
                prepare.ExecuteNonQuery();
                foreach (var (table, keys) in new[] { ("selection_keys", captured.Keys), ("selection_unavailable", captured.UnavailableKeys) })
                {
                    prepare.CommandText = $"INSERT OR IGNORE INTO {table}(path_key) VALUES($key);";
                    prepare.Parameters.Clear(); var parameter = prepare.Parameters.Add("$key", SqliteType.Text);
                    foreach (var key in keys) { token.ThrowIfCancellationRequested(); parameter.Value = PathKey(key); prepare.ExecuteNonQuery(); }
                }
                return await ReadPageAsync(connection, "user_inputs", captured, offset, limit,
                    checkPublication: true, previewCharacters: _query.TextPreviewCharacters, token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }

        private async Task<T> ExecuteReadAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            return await Task.Run(() => SqliteReadCancellation.RunAsync(operation, linked.Token), linked.Token).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<CatalogProjectionRow> EnumerateSelectionAsync(QuerySelection selection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ArgumentNullException.ThrowIfNull(selection);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            cancellationToken = linked.Token;
            using var captured = selection.Capture();
            // Retain the immutable projection independently of the UI query lifetime.
            _owner._leases.AddOrUpdate(_generation, 1, static (_, value) => value + 1);
            try
            {
                using var connection = _owner.OpenConnection(readOperation: true);
                using var interrupt = new SqliteReadCancellation(connection, cancellationToken);
                if (captured.Baseline is { } baseline)
                {
                    using var attach = connection.CreateCommand();
                    attach.CommandText = "ATTACH DATABASE $path AS selection_source;";
                    attach.Parameters.AddWithValue("$path", baseline.DatabasePath);
                    await attach.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                using (var transaction = connection.BeginTransaction(deferred: true))
                {
                    EnsureGeneration(connection, transaction);
                    using var capture = connection.CreateCommand();
                    capture.Transaction = transaction;
                    capture.CommandText = """
                        CREATE TEMP TABLE captured_notes AS SELECT * FROM user_inputs WHERE profile_key=$profile;
                        CREATE INDEX captured_notes_key ON captured_notes(profile_key, path_key, field_id);
                        CREATE TEMP TABLE selection_keys(path_key TEXT PRIMARY KEY COLLATE BINARY) WITHOUT ROWID;
                        CREATE TEMP TABLE selection_unavailable(path_key TEXT PRIMARY KEY COLLATE BINARY) WITHOUT ROWID;
                        """;
                    capture.Parameters.AddWithValue("$profile", ProfileKey(_scope.ProfileId));
                    await SqliteReadCancellation.RunAsync(token => capture.ExecuteNonQueryAsync(token), cancellationToken).ConfigureAwait(false);
                    capture.CommandText = "INSERT OR IGNORE INTO selection_keys(path_key) VALUES($path);";
                    capture.Parameters.Clear();
                    var key = capture.Parameters.Add("$path", SqliteType.Text);
                    capture.Prepare();
                    foreach (var selectedKey in captured.Keys)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        key.Value = PathKey(selectedKey);
                        await capture.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                    capture.CommandText = "INSERT OR IGNORE INTO selection_unavailable(path_key) VALUES($path);";
                    capture.Prepare();
                    foreach (var unavailable in captured.UnavailableKeys)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        key.Value = PathKey(unavailable);
                        await capture.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                    transaction.Commit();
                }
                var offset = 0;
                while (true)
                {
                    var page = await SqliteReadCancellation.RunAsync(token => ReadPageAsync(connection, "captured_notes", captured,
                        offset, BatchSize, checkPublication: false, previewCharacters: 0, token), cancellationToken).ConfigureAwait(false);
                    foreach (var row in page.Items)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return row;
                    }
                    if (!page.HasMore) break;
                    offset = checked(offset + page.Items.Count);
                }
            }
            finally { ReleaseLease(); }
        }

        private async Task<ResultPage<CatalogProjectionRow>> ReadPageAsync(SqliteConnection connection,
            string notesTable, QuerySelection? selection, int offset, int limit,
            bool checkPublication, int previewCharacters, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = new List<CatalogProjectionRow>(limit + 1);
            var hasMore = false;
            var bufferedBytes = 0L;
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                EnsureGeneration(connection, transaction, checkPublication);
                using var command = BuildCommand(connection, transaction, notesTable, selection, offset, limit);
                var (valuesJson, previewFields) = PreviewSql(command, previewCharacters);
                var rowBytes = previewCharacters == 0 ? FullRowBytesSql(valuesJson, previewFields, notesTable) : "0";
                var resources = _owner.HasResourceColumn ? "r.resources_json" : "'{}'";
                command.CommandText = $"SELECT r.full_path, r.relative_path, r.rule_id, {valuesJson}, r.coverage, r.errors_json, r.path_key, {previewFields}, {rowBytes}, {resources} " + command.CommandText;
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (rows.Count == limit) { hasMore = true; break; }
                    // Include saved/default notes before materializing any strings.
                    // A rejected row is retried at the next offset, not skipped.
                    var estimatedBytes = previewCharacters == 0 ? reader.GetInt64(8) : 0;
                    if (previewCharacters == 0 && rows.Count > 0
                        && estimatedBytes > 8L * 1024 * 1024 - bufferedBytes)
                    {
                        hasMore = true;
                        break;
                    }
                    var serializedValues = reader.GetString(3);
                    rows.Add(new CatalogProjectionRow(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        DeserializeValues(serializedValues), reader.GetBoolean(4),
                        Deserialize<Dictionary<string, string>>(reader.GetString(5)))
                    { PreviewFields = Deserialize<string[]>(reader.GetString(7)),
                      Resources = Deserialize<Dictionary<string, ProfileFieldResourceInfo>>(reader.GetString(9)) });
                    bufferedBytes += estimatedBytes;
                    if (previewCharacters == 0 && bufferedBytes >= 8L * 1024 * 1024)
                    {
                        hasMore = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    }
                }
                reader.Close();
                // Bounded per-page note lookup uses the same read transaction as rows.
                var notesByPath = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                if (_inputFields.Count > 0 && rows.Count > 0)
                {
                    using var notes = connection.CreateCommand();
                    notes.Transaction = transaction;
                    var parameters = new List<string>(rows.Count);
                    notes.Parameters.AddWithValue("$profile", ProfileKey(_scope.ProfileId));
                    for (var index = 0; index < rows.Count; index++)
                    {
                        var parameter = "$path" + index;
                        parameters.Add(parameter);
                        notes.Parameters.AddWithValue(parameter, PathKey(rows[index].FullPath));
                    }
                    var inputParameters = new List<string>(_inputFields.Count);
                    for (var index = 0; index < _inputFields.Count; index++)
                    {
                        var parameter = "$inputField" + index;
                        inputParameters.Add(parameter); notes.Parameters.AddWithValue(parameter, _inputFields[index].FieldId.ToLowerInvariant());
                    }
                    notes.CommandText = $"SELECT path_key, field_id, value FROM {notesTable} WHERE profile_key=$profile AND path_key IN ({string.Join(',', parameters)}) AND field_id IN ({string.Join(',', inputParameters)});";
                    using var noteReader = await notes.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await noteReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var path = noteReader.GetString(0);
                        if (!notesByPath.TryGetValue(path, out var rowNotes))
                            notesByPath[path] = rowNotes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        rowNotes[noteReader.GetString(1)] = noteReader.GetString(2);
                    }
                }
                if (_inputFields.Count > 0)
                    for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                    {
                        var row = rows[rowIndex];
                        var values = row.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                        notesByPath.TryGetValue(PathKey(row.FullPath), out var rowNotes);
                        foreach (var field in _inputFields)
                            values[field.FieldId] = rowNotes is not null && rowNotes.TryGetValue(field.FieldId, out var note)
                                ? note : field.DefaultValue ?? string.Empty;
                        rows[rowIndex] = row with { Values = values };
                    }
            }
            return new ResultPage<CatalogProjectionRow>(rows, offset, hasMore);
        }

        private string FullRowBytesSql(string valuesJson, string previewFields, string notesTable)
        {
            // Twice UTF-8 length conservatively bounds UTF-16 string payloads,
            // including supplementary Unicode and JSON-escaped values. SQLite
            // computes this scalar without allocating rejected row text in .NET.
            var notes = _inputFields.Count == 0 ? "0" : $"""
                COALESCE((SELECT SUM(length(CAST(COALESCE(bn.value,bc.raw_search) AS BLOB)))
                    FROM projection_cells bc LEFT JOIN {notesTable} bn
                    ON bn.profile_key=$profile AND bn.path_key=bc.path_key AND bn.field_id=bc.field_id
                    WHERE bc.generation=r.generation AND bc.path_key=r.path_key AND bc.is_user_input=1),0)
                """;
            var resources = _owner.HasResourceColumn ? "length(CAST(r.resources_json AS BLOB))" : "0";
            return $"""
                512 + 2 * (length(CAST({valuesJson} AS BLOB)) + length(CAST(r.full_path AS BLOB))
                    + length(CAST(r.relative_path AS BLOB)) + length(CAST(r.rule_id AS BLOB))
                    + length(CAST(r.errors_json AS BLOB)) + length(CAST({previewFields} AS BLOB)) + {notes} + {resources})
                """;
        }

        private (string Values, string Fields) PreviewSql(SqliteCommand command, int characters)
        {
            if (characters == 0 || _textFieldIds.Count == 0) return ("r.values_json", "'[]'");
            var parameters = new List<string>(_textFieldIds.Count);
            for (var index = 0; index < _textFieldIds.Count; index++)
            {
                var name = "$previewField" + index;
                parameters.Add(name);
                command.Parameters.AddWithValue(name, _textFieldIds[index]);
            }
            command.Parameters.AddWithValue("$previewCharacters", characters);
            var truncated = $"lower(j.key) IN ({string.Join(',', parameters)}) AND json_extract(j.value,'$.Type')='string' AND length(json_extract(j.value,'$.Text'))>$previewCharacters";
            // SQLite truncates the encoded string before allocating managed JSON.
            // Numeric/date/null tags and all non-text-file values remain unchanged.
            return ($"""
                (SELECT json_group_object(j.key, json(CASE WHEN {truncated}
                    THEN json_object('Type','string','Text',substr(json_extract(j.value,'$.Text'),1,$previewCharacters))
                    ELSE j.value END)) FROM json_each(r.values_json) j)
                """, $"(SELECT json_group_array(j.key) FROM json_each(r.values_json) j WHERE {truncated})");
        }

        private SqliteCommand BuildCommand(SqliteConnection connection, SqliteTransaction transaction,
            string notesTable, QuerySelection? selection, int? offset, int? limit)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$generation", _generation);
            command.Parameters.AddWithValue("$profile", ProfileKey(_scope.ProfileId));
            var joins = string.Empty;
            var order = _sortTarget.Kind == CatalogColumnTargetKind.Coverage ? "r.coverage" : "r.path_key COLLATE BINARY";
            if (_sortField is not null)
            {
                joins = $"""
                    LEFT JOIN projection_cells s ON s.generation=r.generation AND s.path_key=r.path_key AND s.field_id=$sortField
                    LEFT JOIN {notesTable} sn ON s.is_user_input=1 AND sn.profile_key=$profile AND sn.path_key=r.path_key AND sn.field_id=s.field_id
                    """;
                command.Parameters.AddWithValue("$sortField", _sortField.FieldId.ToLowerInvariant());
                order = _sortField.SourceKind == ProfileFieldSourceKind.UserInput
                    ? "COALESCE(sn.value_search, s.raw_sort) COLLATE BINARY"
                    : _sortField.Kind switch
                    {
                        ProfileFieldValueKind.Int32 or ProfileFieldValueKind.Decimal or ProfileFieldValueKind.Boolean => "s.sort_numeric COLLATE BINARY",
                        ProfileFieldValueKind.DateTime => "s.sort_ticks",
                        _ => (_query.ShowOriginalValues ? "s.raw_sort" : "s.mapped_sort") + " COLLATE BINARY"
                    };
            }
            var predicates = new List<string> { "r.generation=$generation" };
            AddColumnFilters(command, notesTable, predicates);
            for (var index = 0; index < _terms.Length; index++)
            {
                var term = "$term" + index;
                command.Parameters.AddWithValue(term, _terms[index]);
                var column = _query.ShowOriginalValues ? "c.raw_search" : "c.mapped_search";
                predicates.Add($"""
                    (instr(r.path_search,{term})>0
                    OR EXISTS(SELECT 1 FROM projection_cells c LEFT JOIN {notesTable} n
                        ON c.is_user_input=1 AND n.profile_key=$profile AND n.path_key=c.path_key AND n.field_id=c.field_id
                        WHERE c.generation=r.generation AND c.path_key=r.path_key
                        AND instr(CASE WHEN c.is_user_input=1 THEN COALESCE(n.value_search,c.raw_search) ELSE {column} END,{term})>0))
                    """);
            }
            if (_terms.Length > 0)
            {
                var korean = _scope.CultureName.StartsWith("ko", StringComparison.OrdinalIgnoreCase);
                command.Parameters.AddWithValue("$pending", korean ? "대기 범위" : "pending scope");
                command.Parameters.AddWithValue("$completed", korean ? "완료" : "completed");
            }
            if (selection is not null)
            {
                var exceptions = "EXISTS(SELECT 1 FROM selection_keys k WHERE k.path_key=r.path_key)";
                var selected = selection.Baseline is null ? exceptions
                    : $"(EXISTS(SELECT 1 FROM selection_source.selection_baseline b WHERE b.query_key=r.path_key) <> {exceptions})";
                predicates.Add((selection.AllMatching ? "NOT " : string.Empty) + selected);
                if (selection.UnavailableKeys.Count > 0)
                    predicates.Add("NOT EXISTS(SELECT 1 FROM selection_unavailable u WHERE u.path_key=r.path_key)");
            }
            command.CommandText = $"FROM projection_rows r {joins} WHERE " + string.Join(" AND ", predicates);
            if (offset is not null && limit is not null)
            {
                // The path key is NOT NULL. A redundant IS NULL expression makes
                // SQLite sort the entire generation instead of walking its PK.
                var ordering = _sortTarget.Kind == CatalogColumnTargetKind.FullPath
                    ? $"r.path_key COLLATE BINARY {(_query.Descending ? "DESC" : "ASC")}"
                    : $"({order} IS NULL) ASC, {order} {(_query.Descending ? "DESC" : "ASC")}, r.path_key COLLATE BINARY ASC";
                command.CommandText += $" ORDER BY {ordering} LIMIT $limit OFFSET $offset;";
                command.Parameters.AddWithValue("$limit", limit.Value + 1);
                command.Parameters.AddWithValue("$offset", offset.Value);
            }
            return command;
        }

        private void EnsureGeneration(SqliteConnection connection, SqliteTransaction transaction,
            bool checkPublication = true)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT state, revision FROM projection_generations WHERE generation=$generation;";
            command.Parameters.AddWithValue("$generation", _generation);
            long state;
            long revision;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) throw new ResultQueryChangedException(Identity, CurrentGeneration(connection, transaction, _scope.Key));
                state = reader.GetInt64(0);
                revision = reader.GetInt64(1);
            }
            if (revision != _buildingRevision)
                throw new ResultQueryChangedException(Identity, _generation + ":" + revision);
            if (checkPublication && _building && state == 1)
            {
                var actual = CurrentGeneration(connection, transaction, _scope.Key);
                if (actual != _generation) throw new ResultQueryChangedException(Identity, actual);
            }
            if (checkPublication && !_building)
            {
                var actual = CurrentGeneration(connection, transaction, _scope.Key);
                if (actual != _generation) throw new ResultQueryChangedException(_generation, actual);
            }
            if (checkPublication)
            {
                var actualNotesRevision = NotesRevision(connection, transaction, _scope.ProfileId);
                if (actualNotesRevision != _notesRevision)
                    throw new ResultQueryChangedException(Identity + ":notes:" + _notesRevision,
                        Identity + ":notes:" + actualNotesRevision);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _lifetime.Cancel();
            ReleaseLease();
            if (!_owner._disposed)
            {
                // Retired generations can contain hundreds of thousands of cells.
                // Lifetime cleanup never performs this deletion on the UI thread.
                await Task.Run(async () =>
                {
                    await _owner._writeGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (_owner._disposed) return;
                        using var connection = _owner.OpenConnection();
                        _owner.Prune(connection);
                    }
                    catch (ObjectDisposedException) when (_owner._disposed) { }
                    catch (SqliteException) { /* A later Begin retries idle-generation cleanup. */ }
                    finally { _owner._writeGate.Release(); }
                }).ConfigureAwait(false);
            }
        }

        private void ReleaseLease()
        {
            while (_owner._leases.TryGetValue(_generation, out var value))
            {
                if (value == 1)
                {
                    if (_owner._leases.TryRemove(new KeyValuePair<string, int>(_generation, value))) return;
                }
                else if (_owner._leases.TryUpdate(_generation, value - 1, value)) return;
            }
        }

        private static void ValidateRange(int offset, int limit)
        {
            if (offset < 0 || limit is < 1 or > 1000)
                throw new ArgumentOutOfRangeException(nameof(limit), "Offset must be nonnegative and limit must be 1–1000.");
        }
    }
}
