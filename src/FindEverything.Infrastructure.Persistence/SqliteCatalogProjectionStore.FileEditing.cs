using System.Globalization;
using System.Text.Json;
using FindEverything.Application.Catalog;
using FindEverything.Application.Results;
using FindEverything.Profile.Runtime;

namespace FindEverything.Infrastructure.Persistence;

public sealed partial class SqliteCatalogProjectionStore
{
    /// <summary>Updates one file-backed cell, not every row. Revision fences invalidate older readers.</summary>
    public Task<CatalogProjectionSnapshot> UpdateTextCellAsync(CatalogProjectionSnapshot snapshot,
        string fullPath, string fieldId, string value, ProfileFieldResourceInfo resource,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(resource);
        if (snapshot.IsBuilding) throw new InvalidOperationException("A scan must finish before editing a file.");
        var field = snapshot.Fields.FirstOrDefault(f => string.Equals(f.FieldId, fieldId, StringComparison.OrdinalIgnoreCase));
        if (field?.SourceKind != ProfileFieldSourceKind.TextFileContent)
            throw new ArgumentException("Only file-backed text cells can be edited.", nameof(fieldId));
        if (resource.State != ProfileFieldResourceState.Present || string.IsNullOrWhiteSpace(resource.FileName))
            throw new ArgumentException("A saved cell must identify its existing file.", nameof(resource));
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var interrupt = new SqliteReadCancellation(connection, cancellationToken);
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT revision FROM projection_generations WHERE generation=$g AND state=1 AND EXISTS(SELECT 1 FROM projection_publications WHERE generation=$g AND scope_key=$scope);";
            command.Parameters.AddWithValue("$g", snapshot.Generation);
            command.Parameters.AddWithValue("$scope", snapshot.Scope.Key);
            var actual = command.ExecuteScalar();
            if (actual is null || Convert.ToInt64(actual, CultureInfo.InvariantCulture) != snapshot.Revision)
                throw new ResultQueryChangedException(snapshot.Generation + ":" + snapshot.Revision, actual?.ToString());
            command.Parameters.AddWithValue("$path", PathKey(fullPath));
            command.CommandText = "SELECT values_json,errors_json,resources_json FROM projection_rows WHERE generation=$g AND path_key=$path;";
            Dictionary<string, object?> values;
            Dictionary<string, string> errors;
            Dictionary<string, ProfileFieldResourceInfo> resources;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) throw new KeyNotFoundException("The file's result row no longer exists.");
                values = DeserializeValues(reader.GetString(0)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                errors = Deserialize<Dictionary<string, string>>(reader.GetString(1));
                resources = Deserialize<Dictionary<string, ProfileFieldResourceInfo>>(reader.GetString(2));
            }
            values[field.FieldId] = value;
            var hadIssues = errors.Count > 0;
            errors.Remove(field.FieldId);
            resources[field.FieldId] = resource;
            command.CommandText = "UPDATE projection_rows SET values_json=$values,errors_json=$errors,resources_json=$resources WHERE generation=$g AND path_key=$path;";
            command.Parameters.AddWithValue("$values", SerializeValues(values));
            command.Parameters.AddWithValue("$errors", JsonSerializer.Serialize(errors));
            command.Parameters.AddWithValue("$resources", JsonSerializer.Serialize(resources));
            command.ExecuteNonQuery();
            command.CommandText = "UPDATE projection_cells SET raw_search=$text,mapped_search=$text,raw_sort=$text,mapped_sort=$text WHERE generation=$g AND path_key=$path AND field_id=$field;";
            command.Parameters.AddWithValue("$field", field.FieldId.ToLowerInvariant());
            command.Parameters.AddWithValue("$text", value.ToLowerInvariant());
            if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("The file cell has no search metadata.");
            var summary = snapshot.Summary with { UpdatedUtc = DateTimeOffset.UtcNow,
                FieldIssueRowCount = Math.Max(0, snapshot.Summary.FieldIssueRowCount - (hadIssues && errors.Count == 0 ? 1 : 0)) };
            command.CommandText = "UPDATE projection_generations SET revision=revision+1,summary_json=$summary WHERE generation=$g;";
            command.Parameters.AddWithValue("$summary", JsonSerializer.Serialize(summary));
            command.ExecuteNonQuery();
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return snapshot with { Summary = summary, Revision = snapshot.Revision + 1 };
        }
        catch (Microsoft.Data.Sqlite.SqliteException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        finally { _writeGate.Release(); }
    }, cancellationToken);
}
