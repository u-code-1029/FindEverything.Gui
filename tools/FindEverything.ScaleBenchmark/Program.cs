using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FindEverything.Application.Catalog;
using FindEverything.Infrastructure.Persistence;
using FindEverything.Profile.Runtime;
using Engine = FindEverything.Engine;

var fileCount = ReadNumber("--files", 1_000_000);
var catalogCount = ReadNumber("--catalog", 100_000);
var directory = Path.GetFullPath(ReadArgument("--directory")
    ?? Path.Combine(Path.GetTempPath(), "findeverything-scale-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(directory);
var sourceRoot = Path.Combine(directory, "synthetic-source");
Directory.CreateDirectory(sourceRoot);
var reuse = args.Contains("--reuse", StringComparer.Ordinal);
await using var metadata = new Engine.SqliteIndexStore(Path.Combine(directory, "engine.db"));
Console.WriteLine($"Benchmark directory: {directory}");
var setup = Stopwatch.StartNew();
if (!reuse)
{
    await using var writer = await metadata.AcquireWriterAsync();
    await metadata.InitializeAsync();
    var scan = Guid.NewGuid();
    await metadata.BeginScanAsync(scan, sourceRoot, sourceRoot);
    var epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    for (var offset = 0; offset < fileCount; offset += 500)
    {
        var entries = Enumerable.Range(offset, Math.Min(500, fileCount - offset)).Select(index =>
        {
            var extension = index % 3 == 0 ? "pdf" : index % 3 == 1 ? "txt" : "zip";
            var parent = Path.Combine(sourceRoot, $"project-{index % 100:D3}");
            var name = $"invoice-{index:D7}.{extension}";
            return new Engine.IndexedEntry(Path.Combine(parent, name), name, parent,
                Engine.EntryKind.File, index % 50_000, epoch.AddSeconds(index), epoch.AddSeconds(index));
        }).ToArray();
        await metadata.StageAsync(scan, entries);
        if ((offset + entries.Length) % 100_000 == 0) Console.WriteLine($"Prepared {offset + entries.Length:N0} metadata rows");
    }
    await metadata.PublishAsync(new Engine.ScanReport(scan, sourceRoot, sourceRoot,
        Engine.ScanStatus.Completed, new Engine.ScanProgress(fileCount, 0, 0, 0, 0, setup.Elapsed), []));
}
setup.Stop();
var status = await metadata.GetRootStatusAsync(sourceRoot)
    ?? throw new InvalidOperationException("No benchmark index is published.");
var query = new Engine.SearchQuery { RootPath = sourceRoot, ExpectedScanId = status.LastScanId, IncludeTotalCount = false, Limit = 500 };
var fileFirst = await Measure(async () => { _ = await metadata.SearchAsync(query); });
var fileSamples = new List<double>();
for (var index = 0; index < 20; index++)
    fileSamples.Add(await Measure(async () => { _ = await metadata.SearchAsync(query with { SearchText = $"invoice-{index * 37:D5}" }); }));
var firstPage = await metadata.SearchAsync(query);
var cursor = firstPage.NextCursor;
var sequential = Stopwatch.StartNew();
for (var index = 0; index < 20 && cursor is not null; index++)
    cursor = (await metadata.SearchAsync(query with { AfterCursor = cursor })).NextCursor;
sequential.Stop();
var exactPaths = firstPage.Entries.Take(3).Select(entry => entry.FullPath).ToArray();
var exactSelected = await Measure(async () => { _ = await metadata.SearchAsync(query with { FullPaths = exactPaths }); });

using var catalog = new SqliteCatalogProjectionStore(Path.Combine(directory, "catalog.db"));
var fields = new[]
{
    new ProfileFieldDescriptor("alias", "alias", "Alias", 1, true, ProfileFieldValueKind.String, false, null, null)
        { ValueMappings = new Dictionary<string, string> { ["A"] = "Alice", ["B"] = "Bob" } },
    new ProfileFieldDescriptor("number", "number", "Number", 2, true, ProfileFieldValueKind.Int32, false, null, null),
    new ProfileFieldDescriptor("date", "date", "Date", 3, true, ProfileFieldValueKind.DateTime, false, null, "yyyy-MM-dd"),
    new ProfileFieldDescriptor("text", "text", "Text", 4, false, ProfileFieldValueKind.String, true, null, null)
        { SourceKind = ProfileFieldSourceKind.TextFileContent },
    new ProfileFieldDescriptor("memo", "", "Memo", 5, false, ProfileFieldValueKind.String, true, null, null)
        { SourceKind = ProfileFieldSourceKind.UserInput, DefaultValue = "" }
}.Concat(Enumerable.Range(5, 15).Select(index => new ProfileFieldDescriptor(
    $"field-{index}", $"field-{index}", $"Field {index}", index + 1, true,
    ProfileFieldValueKind.String, false, null, null))).ToArray();
var scope = new CatalogProjectionScope("scale-benchmark", "scale-profile", sourceRoot,
    metadata.DatabasePath, "scale-v2-20-fields", "en-US", status.LastScanId);
CatalogProjectionSnapshot? snapshot = reuse ? await catalog.FindAsync(scope) : null;
var catalogSetup = Stopwatch.StartNew();
if (snapshot is null)
{
    using var writer = catalog.Begin(scope, fields);
    for (var index = 0; index < catalogCount; index++)
    {
        var full = Path.Combine(sourceRoot, $"project-{index % 100:D3}", $"item-{index:D7}");
        var values = new Dictionary<string, object?>
        {
            ["alias"] = index % 2 == 0 ? "A" : "B", ["number"] = index,
            ["date"] = new DateTime(2026, 1, 1).AddDays(index % 365),
            ["text"] = $"Department {index % 20} invoice metadata {index:D7}"
        };
        foreach (var field in fields.Skip(5)) values[field.FieldId] = $"Alpha client-{index % 100:D2} {field.FieldId}";
        writer.Append(new CatalogItem(full, $"item-{index}", "synthetic-rule", values, values, false));
        if ((index + 1) % 20_000 == 0) Console.WriteLine($"Prepared {index + 1:N0} structured rows");
    }
    snapshot = writer.Publish(new CatalogProjectionSummary(catalogCount, catalogCount, 0, 0, 0,
        false, catalogSetup.Elapsed, DateTimeOffset.UtcNow));
}
catalogSetup.Stop();
await using var catalogQuery = catalog.OpenQuery(snapshot, new());
var catalogFirst = await Measure(async () => { _ = await catalogQuery.ReadRangeAsync(0, 500); });
var deepOffset = Math.Max(0, Math.Min(catalogCount - 500, 100_000 - 500));
var catalogPathDeep = await Measure(async () => { _ = await catalogQuery.ReadRangeAsync(deepOffset, 500); });
await using var reversePath = catalog.OpenQuery(snapshot, new(Descending: true));
var catalogPathDescendingFirst = await Measure(async () => { _ = await reversePath.ReadRangeAsync(0, 500); });
var catalogPathDescendingDeep = await Measure(async () => { _ = await reversePath.ReadRangeAsync(deepOffset, 500); });
var catalogSamples = new List<double>();
for (var index = 0; index < 20; index++)
{
    await using var filtered = catalog.OpenQuery(snapshot, new($"metadata {index * 37:D5}"));
    catalogSamples.Add(await Measure(async () => { _ = await filtered.ReadRangeAsync(0, 500); }));
}
await using var sorted = catalog.OpenQuery(snapshot, new(SortFieldId: "number", Descending: true));
var catalogSorted = await Measure(async () => { _ = await sorted.ReadRangeAsync(0, 500); });
await using var noteQuery = catalog.OpenQuery(snapshot, new("Alice"));
var catalogCountTime = await Measure(async () => { _ = await noteQuery.CountAsync(); });
var columnFilterSamples = new List<object>();
var columnConditions = new CatalogColumnFilter[]
{
    new(CatalogColumnTarget.Field("number"), CatalogFilterOperator.NumberRange) { Minimum = catalogCount / 4, Maximum = catalogCount * 3 / 4 },
    new(CatalogColumnTarget.Field("text"), CatalogFilterOperator.DoesNotContain) { Text = "Department 19 " },
    new(CatalogColumnTarget.Field("date"), CatalogFilterOperator.DateRange) { StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 6, 30) },
    new(CatalogColumnTarget.Field("alias"), CatalogFilterOperator.Contains) { Text = "Alice" },
    new(CatalogColumnTarget.Field("memo"), CatalogFilterOperator.DoesNotContain) { Text = "skip" },
    new(CatalogColumnTarget.Field("field-5"), CatalogFilterOperator.Contains) { Text = "Alpha" },
    new(CatalogColumnTarget.Coverage, CatalogFilterOperator.CoverageEquals) { Boolean = false }
};
foreach (var conditionCount in new[] { 1, 3, 7 })
{
    await using var columnQuery = catalog.OpenQuery(snapshot, new() { Filters = columnConditions.Take(conditionCount).ToArray() });
    var firstPageMilliseconds = await Measure(async () => { _ = await columnQuery.ReadRangeAsync(0, 500); });
    long matchCount = 0;
    var countMilliseconds = await Measure(async () => { matchCount = await columnQuery.CountAsync(); });
    columnFilterSamples.Add(new { Conditions = conditionCount, MatchCount = matchCount, FirstPageMilliseconds = firstPageMilliseconds, CountMilliseconds = countMilliseconds });
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    Directory = directory, Runtime = Environment.Version.ToString(), OS = Environment.OSVersion.ToString(),
    Files = status.EntryCount, StructuredRows = snapshot.Summary.MatchedCount,
    SetupMilliseconds = setup.Elapsed.TotalMilliseconds, CatalogSetupMilliseconds = catalogSetup.Elapsed.TotalMilliseconds,
    FileFirst500Milliseconds = fileFirst, FileSearchP95Milliseconds = Percentile(fileSamples),
    File20SequentialPagesMilliseconds = sequential.Elapsed.TotalMilliseconds, Exact3SelectedMilliseconds = exactSelected,
    CatalogFirst500Milliseconds = catalogFirst, CatalogSearchP95Milliseconds = Percentile(catalogSamples),
    CatalogPathDeepOffset = deepOffset, CatalogPathDeep500Milliseconds = catalogPathDeep,
    CatalogPathDescendingFirst500Milliseconds = catalogPathDescendingFirst,
    CatalogPathDescendingDeep500Milliseconds = catalogPathDescendingDeep,
    CatalogNumericSortFirst500Milliseconds = catalogSorted, CatalogFilteredCountMilliseconds = catalogCountTime,
    StructuredColumns = fields.Length, TypedColumnFilters = columnFilterSamples,
    ManagedBytes = GC.GetTotalMemory(forceFullCollection: true), WorkingSetBytes = Environment.WorkingSet
}, new JsonSerializerOptions { WriteIndented = true }));

string? ReadArgument(string name)
{
    var index = Array.IndexOf(args, name);
    return index < 0 ? null : index + 1 < args.Length ? args[index + 1]
        : throw new ArgumentException($"A value is required for {name}.");
}
int ReadNumber(string name, int fallback) => int.TryParse(ReadArgument(name), NumberStyles.None,
    CultureInfo.InvariantCulture, out var number) ? Math.Max(1, number) : fallback;
static async Task<double> Measure(Func<Task> action)
{
    var timer = Stopwatch.StartNew(); await action(); return timer.Elapsed.TotalMilliseconds;
}
static double Percentile(IEnumerable<double> samples)
{
    var values = samples.Order().ToArray(); return values[(int)Math.Ceiling(values.Length * .95) - 1];
}
