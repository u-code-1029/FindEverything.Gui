using FindEverything.Application.Catalog;
using FindEverything.Application.Results;
using FindEverything.Profile.Runtime;

namespace FindEverything.Application.Sharing;

public enum ProfileImportConflictChoice { NewProfile, Replace, Cancel }

public sealed record ProfileTransferInspection(ProfileManifest Manifest, bool HasIdConflict);
public sealed record ProfileTransferResult(string ProfileId, string ManifestPath);

public interface IProfileTransferService
{
    Task<ProfileTransferInspection> InspectAsync(string path, CancellationToken cancellationToken = default);
    Task<ProfileTransferResult?> ImportAsync(string path, ProfileImportConflictChoice choice = ProfileImportConflictChoice.NewProfile,
        CancellationToken cancellationToken = default);
    Task<ProfileTransferResult?> ImportManifestAsync(ProfileManifest manifest, ProfileImportConflictChoice choice,
        CancellationToken cancellationToken = default);
    Task ExportAsync(ProfileManifest manifest, string destination, CancellationToken cancellationToken = default);
}

public sealed record WorkspacePackageInspection(ProfileManifest Profile, string OriginalRoot,
    string StoredRoot, Guid? IndexScanId, bool HasCatalog, bool IncludesUserInputs, long EntryCount,
    long CatalogRowCount, bool HasPendingScopes);

public sealed record WorkspacePackageExportRequest(ProfileManifest Profile, string MetadataDatabasePath,
    string RootPath, string DestinationPath)
{
    public string? CatalogDatabasePath { get; init; }
    public CatalogProjectionSnapshot? CatalogSnapshot { get; init; }
    public bool IncludeUserInputs { get; init; }
    public string? PresentationPath { get; init; }
    /// <summary>UNC root after producer-side mapped-drive resolution, if different from the engine root.</summary>
    public string? CanonicalRootPath { get; init; }
}

public sealed record WorkspaceTransferProgress(string Stage, long Completed, long? Total = null);

public sealed record ImportedWorkspaceSource(string Id, string ProfileId, string DisplayName,
    string OriginalRoot, string EffectiveRoot, string DirectoryPath, string MetadataDatabasePath,
    string? CatalogDatabasePath, CatalogProjectionSnapshot? CatalogSnapshot,
    long EntryCount, bool HasPendingScopes, bool IncludesUserInputs, DateTimeOffset ImportedUtc,
    int LinkRevision, ProfileManifest FrozenProfile)
{
    public bool IsReadOnly => true;
    public bool HasCatalog => CatalogSnapshot is not null && CatalogDatabasePath is not null;
    public override string ToString() => DisplayName;
}

public interface IImportedWorkspaceRegistry
{
    event EventHandler? Changed;
    Task<IReadOnlyList<ImportedWorkspaceSource>> ListAsync(CancellationToken cancellationToken = default);
}

public interface IIndexedDatasetTransferService
{
    Task<WorkspacePackageInspection> InspectAsync(string path, CancellationToken cancellationToken = default);
    Task ExportAsync(WorkspacePackageExportRequest request, IProgress<WorkspaceTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<ImportedWorkspaceSource?> ImportAsync(string path, ProfileImportConflictChoice choice,
        string? effectiveRoot = null, IProgress<WorkspaceTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<ImportedWorkspaceSource> RelinkAsync(ImportedWorkspaceSource source, string effectiveRoot,
        IProgress<WorkspaceTransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<ImportedWorkspaceSource> EnsureCultureAsync(ImportedWorkspaceSource source,
        IProgress<WorkspaceTransferProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(source);
    ICatalogProjectionStore OpenCatalogStore(ImportedWorkspaceSource source);
}

/// <summary>Source keys remain immutable when effective paths are reconnected to another root.</summary>
public interface IImportedCatalogProjectionStore : ICatalogProjectionStore, IDisposable
{
    IResultQuerySession<CatalogProjectionRow> OpenImportedQuery(CatalogProjectionQuery query);
}
