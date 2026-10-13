using FindEverything.Application.Catalog;
using FindEverything.Infrastructure.Persistence;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.Services;

public sealed record ProfileSelectionPlaygroundResult(long ParsedRows, IReadOnlyList<string> PreviewRows,
    CatalogSelectionMatchSummary? MatchSummary);

/// <summary>Uses the production reader and matcher against only the already evaluated sample row.</summary>
public static class ProfileSelectionPlayground
{
    public static async Task<ProfileSelectionPlaygroundResult> TestAsync(ILoadedProfile profile,
        MappedProfileItem? sample, ProfileSelectionMatchRuleManifest rule, string text, string cultureName,
        CancellationToken cancellationToken = default)
    {
        var isHtml = text.TrimStart().StartsWith("<", StringComparison.Ordinal)
            && text.Contains("<table", StringComparison.OrdinalIgnoreCase);
        using var document = await new SelectionImportReader().ReadTextAsync(text, rule, isHtml,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var preview = document.PreviewRows.Select(row => $"{row.RowNumber}: " + string.Join(" · ", row.Values.Select(
            (value, index) => rule.Columns[index].FieldId + " = " + (value ?? "—")))).ToArray();
        if (sample is null) return new(document.RowCount, preview, null);
        var directory = Directory.CreateTempSubdirectory("FindEverything-playground-selection-").FullName;
        try
        {
            return await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var store = new SqliteCatalogProjectionStore(Path.Combine(directory, "sample.db"));
                var scope = new CatalogProjectionScope(Guid.NewGuid().ToString("N"), profile.Descriptor.Id,
                    sample.FullPath, Path.Combine(directory, "unused-metadata.db"), "playground", cultureName, null);
                using var writer = store.Begin(scope, profile.Descriptor.Fields);
                writer.Append(new(sample.FullPath, ".", sample.MatchedRuleId, sample.Model, sample.Values, false)
                    { Resources = sample.Resources, FieldErrors = sample.FieldErrors });
                var snapshot = writer.Publish(new(1, 1, 0, 0, 0, false, null, DateTimeOffset.UtcNow));
                await using var query = store.OpenQuery(snapshot, new());
                var columns = rule.Columns.Select(column => new CatalogSelectionMatchColumn(column.FieldId,
                    column.ValueSource, column.DateComparison, column.DateFormat)).ToArray();
                using var matched = await ((ICatalogSelectionMatchQuery)query).CreateSelectionMatchAsync(
                    new(columns, document.ReadRowsAsync(cancellationToken), cultureName), cancellationToken: cancellationToken).ConfigureAwait(false);
                return new ProfileSelectionPlaygroundResult(document.RowCount, preview, matched.Summary);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Only this task's generated directory is removed, after every DB handle is disposed.
            try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
