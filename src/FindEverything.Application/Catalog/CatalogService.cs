using FindEverything.Application.Indexing;
using FindEverything.Application.Options;
using FindEverything.Profile.Runtime;

namespace FindEverything.Application.Catalog;

internal sealed class CatalogService(
    IIndexSessionFactory sessionFactory,
    IProfileResolver profileResolver,
    IValidatedSettingsState<IndexingOptions> indexingSettings) : ICatalogService, IDisposable
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public Task<CatalogResult> LoadExistingAsync(
        CatalogRequest request,
        IProgress<CatalogOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, scanFirst: false, progress, cancellationToken);

    public Task<CatalogResult> ScanAndLoadAsync(
        CatalogRequest request,
        IProgress<CatalogOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, scanFirst: true, progress, cancellationToken);

    private async Task<CatalogResult> ExecuteAsync(
        CatalogRequest request,
        bool scanFirst,
        IProgress<CatalogOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Validate(request);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            progress?.Report(new(CatalogOperationPhase.Preparing, "프로필과 인덱스를 준비하고 있습니다."));
            if (!profileResolver.TryResolve(request.ProfileId, out var profile))
                throw new InvalidOperationException($"Profile '{request.ProfileId}' is not loaded.");

            var operationSettings = indexingSettings.Current;
            await using var session = sessionFactory.Create(request.DatabasePath);
            IndexScanReport? scanReport = null;

            if (scanFirst)
            {
                var scanProgress = progress is null
                    ? null
                    : new Progress<IndexScanProgress>(value => progress.Report(new(
                        CatalogOperationPhase.Scanning,
                        $"폴더 {value.Directories:N0}개, 항목 {value.Entries:N0}개를 확인했습니다.",
                        value.Entries,
                        value)));
                scanReport = await session.ScanAsync(
                    new IndexScanRequest(request.RootPath)
                    {
                        Options = new IndexScanOptions
                        {
                            MaxEntriesPerSecond = operationSettings.MaxEntriesPerSecond,
                            DirectoryDelay = TimeSpan.FromMilliseconds(operationSettings.DirectoryDelayMilliseconds),
                        },
                    },
                    scanProgress,
                    cancellationToken).ConfigureAwait(false);

                if (scanReport.Status == IndexScanStatus.Cancelled)
                    throw new OperationCanceledException(cancellationToken);
            }

            progress?.Report(new(CatalogOperationPhase.Searching, "색인에서 폴더를 조회하고 있습니다."));
            var searchResult = await session.SearchDirectoriesAsync(
                new DirectorySearchRequest(request.RootPath)
                {
                    PageSize = operationSettings.SearchPageSize,
                },
                cancellationToken).ConfigureAwait(false);

            var items = new List<CatalogItem>();
            var invalidItems = new List<CatalogInvalidItem>();
            var noMatchCount = 0;
            for (var index = 0; index < searchResult.Directories.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = searchResult.Directories[index];
                var relativePath = Path.GetRelativePath(request.RootPath, directory.FullPath);
                var mapping = profile.Map(new ProfilePathCandidate(directory.FullPath, relativePath));
                switch (mapping.Status)
                {
                    case ProfileMapStatus.Success when mapping.Item is not null:
                        items.Add(new CatalogItem(
                            mapping.Item.FullPath,
                            mapping.Item.RelativePath,
                            mapping.Item.MatchedRuleId,
                            mapping.Item.Model,
                            mapping.Item.Values,
                            directory.CoveragePending));
                        break;
                    case ProfileMapStatus.Invalid:
                        invalidItems.Add(new CatalogInvalidItem(directory.FullPath, relativePath, mapping.Issues));
                        break;
                    case ProfileMapStatus.NoMatch:
                        noMatchCount++;
                        break;
                }

                if (index == 0 || (index + 1) % 250 == 0 || index + 1 == searchResult.Directories.Count)
                {
                    progress?.Report(new(
                        CatalogOperationPhase.Mapping,
                        $"경로 {index + 1:N0}개를 프로필 규칙으로 판별했습니다.",
                        index + 1));
                }
            }

            return new CatalogResult(
                profile.Descriptor,
                items.AsReadOnly(),
                invalidItems.AsReadOnly(),
                searchResult.Directories.Count,
                noMatchCount,
                searchResult.HasPendingScopes
                    || searchResult.Directories.Any(static directory => directory.CoveragePending),
                scanReport);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static void Validate(CatalogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DatabasePath);
    }

    public void Dispose() => _operationGate.Dispose();
}
