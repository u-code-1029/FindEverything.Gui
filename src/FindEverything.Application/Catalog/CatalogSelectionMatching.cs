using FindEverything.Application.Results;
using FindEverything.Profile.Runtime;

namespace FindEverything.Application.Catalog;

public sealed record CatalogSelectionMatchColumn(string FieldId,
    ProfileSelectionValueSource ValueSource = ProfileSelectionValueSource.Displayed,
    ProfileSelectionDateComparison DateComparison = ProfileSelectionDateComparison.DateOnly,
    string? DateFormat = null);

public sealed record CatalogSelectionInputRow(long RowNumber, IReadOnlyList<string?> Values);

/// <summary>Input rows stream from a bounded reader, never from the displayed grid.</summary>
public sealed record CatalogSelectionMatchInput(IReadOnlyList<CatalogSelectionMatchColumn> Columns,
    IAsyncEnumerable<CatalogSelectionInputRow> Rows, string CultureName);

public sealed record CatalogSelectionInputIssue(long RowNumber, string Message);

public sealed record CatalogSelectionMatchSummary(long InputRows, long ValidRows, long InvalidRows,
    long DuplicateInputRows, long MatchedInputRows, long UnmatchedInputRows,
    long AmbiguousInputRows, long MatchingItems, IReadOnlyList<CatalogSelectionInputIssue> Issues);

/// <summary>The immutable keyset is owned by this result until transferred to QuerySelection.</summary>
public sealed class CatalogSelectionMatchResult(IQuerySelectionKeySet keys, CatalogSelectionMatchSummary summary)
    : IDisposable
{
    public IQuerySelectionKeySet Keys { get; } = keys;
    public CatalogSelectionMatchSummary Summary { get; } = summary;
    public void Dispose() => Keys.Dispose();
}

public interface ICatalogSelectionMatchQuery
{
    /// <summary>Matches the fixed, published full query. Optional mapper supplies imported original selection identities.</summary>
    Task<CatalogSelectionMatchResult> CreateSelectionMatchAsync(CatalogSelectionMatchInput input,
        Func<string, string>? selectionKeyMapper = null, CancellationToken cancellationToken = default);
}
