using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FindEverything.Application.Catalog;
using FindEverything.Application.Options;
using FindEverything.Application.Sharing;
using FindEverything.Desktop.Configuration;
using FindEverything.Infrastructure.FindEverything;
using FindEverything.Infrastructure.Persistence;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Options;

namespace FindEverything.Desktop.Services;

/// <summary>Portable, declarative, read-only datasets. Archive databases are never opened for writing.</summary>
public sealed class IndexedDatasetTransferService(AppPaths paths, ProfileTransferService profiles,
    IProfilePresentationStore presentations, IOptions<LocalizationOptions>? localization = null) : IIndexedDatasetTransferService, IImportedWorkspaceRegistry
{
    private string ReceiverCultureName => localization?.Value.CultureName ?? System.Globalization.CultureInfo.CurrentCulture.Name;
    public Task<ImportedWorkspaceSource> EnsureCultureAsync(ImportedWorkspaceSource source,
        IProgress<WorkspaceTransferProgress>? progress = null, CancellationToken cancellationToken = default) =>
        source.CatalogSnapshot is { } snapshot && !string.Equals(snapshot.Scope.CultureName, ReceiverCultureName, StringComparison.OrdinalIgnoreCase)
            ? RelinkAsync(source, source.EffectiveRoot, progress, cancellationToken) : Task.FromResult(source);
    private const long MaximumDatabaseBytes = 8L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> AllowedFiles = new(StringComparer.Ordinal)
    { "manifest.json", "profile.json", "metadata.db", "catalog.db", "presentation.json" };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string ImportsDirectory => Path.Combine(paths.LocalDataDirectory, "Imports");
    public event EventHandler? Changed;

    private sealed record PackageFile(string Name, long Length, string Sha256);
    private sealed record PackageManifest(int FormatVersion, string Format, string OriginalRoot, string StoredRoot,
        Guid? IndexScanId, bool IncludesUserInputs, long EntryCount, long CatalogRowCount,
        bool HasPendingScopes, IReadOnlyList<PackageFile> Files);

    public async Task<IReadOnlyList<ImportedWorkspaceSource>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<ImportedWorkspaceSource>();
        if (!Directory.Exists(ImportsDirectory)) return result;
        foreach (var directory in Directory.EnumerateDirectories(ImportsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            var file = Path.Combine(directory, "source.json");
            if (!File.Exists(file) || new FileInfo(file).Length > 4 * ProfileManifestLimits.MaximumLengthBytes) continue;
            try
            {
                var source = JsonSerializer.Deserialize<ImportedWorkspaceSource>(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false), ProfileTransferService.ReadOptions);
                if (source is null || source.Id != Path.GetFileName(directory)) continue;
                // Persist relative ownership through one known directory, never honor arbitrary DB paths from registry JSON.
                var linked = Path.Combine(directory, "linked-" + source.LinkRevision);
                var metadata = Path.Combine(linked, "metadata.db");
                var catalog = source.HasCatalog ? Path.Combine(linked, "catalog.db") : null;
                if (!File.Exists(metadata) || catalog is not null && !File.Exists(catalog)) continue;
                result.Add(source with { DirectoryPath = directory, MetadataDatabasePath = metadata, CatalogDatabasePath = catalog });
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { /* One damaged registry entry does not hide other imports. */ }
        }
        return result.OrderByDescending(s => s.ImportedUtc).ToArray();
    }

    public async Task<WorkspacePackageInspection> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        var stage = CreateStage();
        try
        {
            var (manifest, profile, _) = await ReadPackageAsync(path, stage, cancellationToken).ConfigureAwait(false);
            return ToInspection(manifest, profile);
        }
        finally { DeleteStage(stage); }
    }

    public async Task ExportAsync(WorkspacePackageExportRequest request, IProgress<WorkspaceTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Resolve caller input before taking the gate. Every task-owned staging operation below is inside its finally.
        var destination = Path.GetFullPath(request.DestinationPath);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stage = null;
        var ownsTemporary = false;
        try
        {
            stage = CreateStage();
            await profiles.ExportAsync(request.Profile, Path.Combine(stage, "profile.json"), cancellationToken).ConfigureAwait(false);
            var canonicalRoot = WorkspacePathMapping.NormalizeRoot(request.CanonicalRootPath ?? request.RootPath);
            var metadata = await Task.Run(() => MetadataSnapshotTransfer.CopyAsync(request.MetadataDatabasePath,
                request.RootPath, Path.Combine(stage, "metadata.db"), canonicalRoot, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
            CatalogProjectionSnapshot? projection = null;
            if (request.CatalogSnapshot is not null)
            {
                if (request.CatalogDatabasePath is null) throw new ArgumentException("A catalog database is required with the structured snapshot.");
                if (!string.Equals(request.CatalogSnapshot.Scope.ProfileId, request.Profile.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The structured snapshot belongs to a different profile.");
                if (!string.Equals(request.CatalogSnapshot.Scope.Fingerprint, profiles.GetProjectionFingerprint(request.Profile), StringComparison.Ordinal))
                    throw new InvalidDataException("The profile was changed after these structured results were created. Scan again, or export metadata only.");
                if (request.CatalogSnapshot.Scope.IndexScanId != metadata.ScanId)
                    throw new InvalidDataException("Metadata and structured results are from different scans. Export metadata only or finish loading the current scan first.");
                using var store = new SqliteCatalogProjectionStore(request.CatalogDatabasePath);
                projection = await Task.Run(() => store.ExportSnapshotAsync(request.CatalogSnapshot,
                    Path.Combine(stage, "catalog.db"), request.IncludeUserInputs, canonicalRoot, canonicalRoot,
                    request.Profile.Id!, Path.Combine(stage, "metadata.db"), request.CatalogSnapshot.Scope.CultureName, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            if (request.PresentationPath is not null && File.Exists(request.PresentationPath))
            {
                var presentation = await ReadPresentationAsync(request.PresentationPath, cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(stage, "presentation.json"), JsonSerializer.Serialize(presentation), cancellationToken).ConfigureAwait(false);
            }
            var files = new List<PackageFile>();
            foreach (var name in AllowedFiles.Where(f => f != "manifest.json" && File.Exists(Path.Combine(stage, f))))
            {
                var file = Path.Combine(stage, name);
                files.Add(new(name, new FileInfo(file).Length, await HashFileAsync(file, cancellationToken).ConfigureAwait(false)));
            }
            var manifest = new PackageManifest(2, "FindEverything.PortableWorkspace", canonicalRoot, canonicalRoot,
                metadata.ScanId, request.IncludeUserInputs && projection is not null, metadata.EntryCount,
                projection?.Summary.MatchedCount ?? 0, metadata.HasPendingScopes, files);
            await File.WriteAllTextAsync(Path.Combine(stage, "manifest.json"), JsonSerializer.Serialize(manifest, ProfileTransferService.WriteOptions), cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                ownsTemporary = true;
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var name in AllowedFiles.Where(f => File.Exists(Path.Combine(stage, f))))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                    await using var input = File.OpenRead(Path.Combine(stage, name));
                    await using var entryStream = entry.Open();
                    await input.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally
        {
            try
            {
                if (ownsTemporary) DeleteTemporaryFile(temporary);
                DeleteStage(stage);
            }
            finally { _gate.Release(); }
        }
    }

    public async Task<ImportedWorkspaceSource?> ImportAsync(string path, ProfileImportConflictChoice choice,
        string? effectiveRoot = null, IProgress<WorkspaceTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(choice)) throw new ArgumentOutOfRangeException(nameof(choice));
        if (choice == ProfileImportConflictChoice.Cancel) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stage = null;
        try
        {
            stage = CreateStage();
            var archive = Path.Combine(stage, "archive");
            Directory.CreateDirectory(archive);
            var (manifest, profile, snapshot) = await ReadPackageAsync(path, archive, cancellationToken).ConfigureAwait(false);
            var root = WorkspacePathMapping.NormalizeRoot(effectiveRoot ?? manifest.OriginalRoot);
            var id = Guid.NewGuid().ToString("N");
            var installedDirectory = Path.Combine(ImportsDirectory, id);
            return await profiles.InstallAtomicallyAsync(profile, choice, async result =>
            {
                var linked = Path.Combine(stage, "linked-0");
                Directory.CreateDirectory(linked);
                var derived = await BuildDerivedAsync(archive, linked, installedDirectory, "linked-0", root,
                    result.ProfileId, manifest, snapshot, progress, cancellationToken).ConfigureAwait(false);
                var source = new ImportedWorkspaceSource(id, result.ProfileId, profile.DisplayName ?? result.ProfileId,
                    manifest.OriginalRoot, root, installedDirectory, derived.Metadata, derived.Catalog,
                    derived.Snapshot, manifest.EntryCount, manifest.HasPendingScopes, manifest.IncludesUserInputs,
                    DateTimeOffset.UtcNow, 0, profile);
                await WriteSourceAsync(Path.Combine(stage, "source.json"), source, cancellationToken).ConfigureAwait(false);
                var presentationFile = Path.Combine(archive, "presentation.json");
                var oldPresentation = await presentations.LoadAsync(result.ProfileId, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (File.Exists(presentationFile))
                        await presentations.SaveAsync(result.ProfileId, await ReadPresentationAsync(presentationFile, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.Move(stage, installedDirectory);
                }
                catch
                {
                    await presentations.SaveAsync(result.ProfileId, oldPresentation, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                RaiseChanged();
                return source;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { try { DeleteStage(stage); } finally { _gate.Release(); } }
    }

    public async Task<ImportedWorkspaceSource> RelinkAsync(ImportedWorkspaceSource source, string effectiveRoot,
        IProgress<WorkspaceTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stage = null;
        try
        {
            stage = CreateStage();
            var installed = Path.Combine(ImportsDirectory, source.Id);
            if (!Guid.TryParseExact(source.Id, "N", out _) || !Directory.Exists(installed))
                throw new InvalidDataException("The imported workspace is not installed.");
            var archive = Path.Combine(installed, "archive");
            var manifest = await ReadManifestAsync(Path.Combine(archive, "manifest.json"), cancellationToken).ConfigureAwait(false);
            var snapshot = File.Exists(Path.Combine(archive, "catalog.db")) ? await Task.Run(() => SqliteCatalogProjectionStore.ValidateTransferredSnapshot(Path.Combine(archive, "catalog.db"), cancellationToken), cancellationToken).ConfigureAwait(false) : null;
            var root = WorkspacePathMapping.NormalizeRoot(effectiveRoot);
            var revision = checked(source.LinkRevision + 1);
            var linkName = "linked-" + revision;
            var derived = await BuildDerivedAsync(archive, stage, installed, linkName, root, source.ProfileId,
                manifest, snapshot, progress, cancellationToken).ConfigureAwait(false);
            var updated = source with { EffectiveRoot = root, MetadataDatabasePath = derived.Metadata,
                CatalogDatabasePath = derived.Catalog, CatalogSnapshot = derived.Snapshot, LinkRevision = revision };
            cancellationToken.ThrowIfCancellationRequested();
            var final = Path.Combine(installed, linkName);
            Directory.Move(stage, final);
            try { await WriteSourceAsync(Path.Combine(installed, "source.json"), updated, cancellationToken).ConfigureAwait(false); }
            catch { Directory.Delete(final, true); throw; }
            // Older linked generations can still be leased by a query. Keep them until an explicit cleanup workflow.
            RaiseChanged();
            return updated;
        }
        finally { try { DeleteStage(stage); } finally { _gate.Release(); } }
    }

    public ICatalogProjectionStore OpenCatalogStore(ImportedWorkspaceSource source) => new ImportedCatalogProjectionStore(source);

    private async Task<(string Metadata, string? Catalog, CatalogProjectionSnapshot? Snapshot)> BuildDerivedAsync(
        string archive, string stage, string installed, string linkName, string root, string profileId,
        PackageManifest manifest, CatalogProjectionSnapshot? snapshot, IProgress<WorkspaceTransferProgress>? progress, CancellationToken ct)
    {
        var metadata = Path.Combine(installed, linkName, "metadata.db");
        await Task.Run(() => MetadataSnapshotTransfer.CopyAsync(Path.Combine(archive, "metadata.db"), manifest.StoredRoot,
            Path.Combine(stage, "metadata.db"), root, progress, ct), ct).ConfigureAwait(false);
        if (snapshot is null) return (metadata, null, null);
        using var store = new SqliteCatalogProjectionStore(Path.Combine(archive, "catalog.db"), readOnly: true);
        var derived = await Task.Run(() => store.ExportSnapshotAsync(snapshot, Path.Combine(stage, "catalog.db"),
            manifest.IncludesUserInputs, manifest.OriginalRoot, root, profileId, metadata,
            ReceiverCultureName, progress, ct), ct).ConfigureAwait(false);
        return (metadata, Path.Combine(installed, linkName, "catalog.db"), derived);
    }

    private async Task<(PackageManifest Manifest, ProfileManifest Profile, CatalogProjectionSnapshot? Snapshot)> ReadPackageAsync(
        string path, string directory, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (zip.Entries.Count is < 3 or > 5) throw new InvalidDataException("The workspace archive has unexpected entries.");
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!AllowedFiles.Contains(entry.FullName) || !names.Add(entry.FullName))
                throw new InvalidDataException("The archive contains duplicate, unknown, or unsafe file paths.");
            var limit = entry.Name.EndsWith(".db", StringComparison.Ordinal) ? MaximumDatabaseBytes : ProfileManifestLimits.MaximumLengthBytes;
            if (entry.Length < 0 || entry.Length > limit) throw new InvalidDataException("An archive entry exceeds the supported size limit.");
            var file = Path.Combine(directory, entry.FullName);
            await using var destination = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            await using var entryStream = entry.Open();
            var buffer = new byte[65536];
            var written = 0L;
            while (true)
            {
                var count = await entryStream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (count == 0) break;
                written += count;
                if (written > limit || written > entry.Length) throw new InvalidDataException("An archive entry has an invalid expanded length.");
                await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            }
            if (written != entry.Length) throw new InvalidDataException("An archive entry is truncated.");
        }
        if (!names.IsSupersetOf(["manifest.json", "profile.json", "metadata.db"])) throw new InvalidDataException("The archive is missing required files.");
        var manifest = await ReadManifestAsync(Path.Combine(directory, "manifest.json"), ct).ConfigureAwait(false);
        if (manifest.Files.Any(f => f is null) || manifest.Files.Count != names.Count - 1 || manifest.Files.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != manifest.Files.Count)
            throw new InvalidDataException("The workspace file manifest is inconsistent.");
        foreach (var file in manifest.Files)
        {
            if (file.Name == "manifest.json" || !AllowedFiles.Contains(file.Name) || !names.Contains(file.Name)) throw new InvalidDataException("The workspace hash manifest is unsafe.");
            var extracted = Path.Combine(directory, file.Name);
            if (new FileInfo(extracted).Length != file.Length || !string.Equals(await HashFileAsync(extracted, ct).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The workspace contains a damaged file or incorrect checksum.");
        }
        var profile = (await profiles.InspectAsync(Path.Combine(directory, "profile.json"), ct).ConfigureAwait(false)).Manifest;
        var metadata = await Task.Run(() => MetadataSnapshotTransfer.Inspect(Path.Combine(directory, "metadata.db"), manifest.StoredRoot, true, ct), ct).ConfigureAwait(false);
        if (metadata.ScanId != manifest.IndexScanId || metadata.EntryCount != manifest.EntryCount || metadata.HasPendingScopes != manifest.HasPendingScopes)
            throw new InvalidDataException("The workspace metadata summary is inconsistent.");
        var catalog = names.Contains("catalog.db") ? await Task.Run(() => SqliteCatalogProjectionStore.ValidateTransferredSnapshot(Path.Combine(directory, "catalog.db"), ct), ct).ConfigureAwait(false) : null;
        if (catalog is not null && (catalog.Scope.IndexScanId != metadata.ScanId
            || !string.Equals(catalog.Scope.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase)
            || catalog.Summary.MatchedCount != manifest.CatalogRowCount
            || !string.Equals(WorkspacePathMapping.NormalizeRoot(catalog.Scope.RootPath), manifest.OriginalRoot, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The structured results do not match this package's profile, root, or scan.");
        if (catalog is null && (manifest.CatalogRowCount != 0 || manifest.IncludesUserInputs))
            throw new InvalidDataException("The workspace advertises missing structured data.");
        // Compare portable semantics, not a producer runtime-module fingerprint or the recipient's newly assigned profile ID.
        if (catalog is not null) profiles.ValidateSnapshotFields(profile, catalog.Fields);
        if (names.Contains("presentation.json")) await ReadPresentationAsync(Path.Combine(directory, "presentation.json"), ct).ConfigureAwait(false);
        return (manifest, profile, catalog);
    }

    private static async Task<PackageManifest> ReadManifestAsync(string path, CancellationToken ct)
    {
        var manifest = JsonSerializer.Deserialize<PackageManifest>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), ProfileTransferService.ReadOptions)
            ?? throw new InvalidDataException("The workspace manifest is empty.");
        if (manifest.FormatVersion is not (1 or 2) || manifest.Format != "FindEverything.PortableWorkspace" || manifest.Files is null
            || manifest.EntryCount < 0 || manifest.CatalogRowCount < 0)
            throw new InvalidDataException("The workspace format or version is not supported.");
        return manifest with { OriginalRoot = WorkspacePathMapping.NormalizeRoot(manifest.OriginalRoot), StoredRoot = WorkspacePathMapping.NormalizeRoot(manifest.StoredRoot) };
    }
    private static WorkspacePackageInspection ToInspection(PackageManifest m, ProfileManifest p) =>
        new(p, m.OriginalRoot, m.StoredRoot, m.IndexScanId, m.Files.Any(f => f.Name == "catalog.db"),
            m.IncludesUserInputs, m.EntryCount, m.CatalogRowCount, m.HasPendingScopes);
    private string CreateStage()
    {
        Directory.CreateDirectory(ImportsDirectory);
        var stage = Path.Combine(ImportsDirectory, ".stage-" + Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(stage); return stage; }
        catch { DeleteStage(stage); throw; }
    }
    private void RaiseChanged()
    {
        // Installation has committed. An observer failure must not roll back its profile while leaving its dataset installed.
        if (Changed is not { } handlers) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
            try { handler(this, EventArgs.Empty); } catch (Exception) { /* Registry observers refresh independently on the next list request. */ }
    }
    private static void DeleteStage(string? stage)
    {
        // Only our fresh .stage-* directory is best-effort cleanup. Published source/profile rollback is never suppressed.
        if (stage is null) return;
        try { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
    private static void DeleteTemporaryFile(string temporary)
    {
        try { if (File.Exists(temporary)) File.Delete(temporary); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false));
    }
    private static async Task WriteSourceAsync(string path, ImportedWorkspaceSource source, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(source, ProfileTransferService.WriteOptions), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task<IReadOnlyDictionary<string, ColumnWidthPreset>> ReadPresentationAsync(string path, CancellationToken ct)
    {
        if (new FileInfo(path).Length > ProfileManifestLimits.MaximumLengthBytes) throw new InvalidDataException("Presentation settings exceed 1 MiB.");
        var settings = JsonSerializer.Deserialize<Dictionary<string, ColumnWidthPreset>>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false),
            new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter<ColumnWidthPreset>() } })
            ?? throw new InvalidDataException("Presentation settings are empty.");
        if (settings.Any(p => string.IsNullOrWhiteSpace(p.Key) || !Enum.IsDefined(p.Value))) throw new InvalidDataException("Presentation settings contain invalid columns or presets.");
        return settings;
    }
}
