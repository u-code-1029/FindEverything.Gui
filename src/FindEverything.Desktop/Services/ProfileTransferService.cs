using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using FindEverything.Application.Sharing;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.Services;

/// <summary>Reads external manifests without weakening the editor's LocalAppData containment boundary.</summary>
public sealed class ProfileTransferService(IProfileAuthoringService authoring, IProfileCatalog catalog, IProfileCatalogPublisher publisher)
    : IProfileTransferService
{
    internal static readonly JsonSerializerOptions ReadOptions = new()
    { AllowTrailingCommas = true, PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
    internal static readonly JsonSerializerOptions WriteOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ProfileTransferInspection> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (info.Length > ProfileManifestLimits.MaximumLengthBytes) throw new InvalidDataException("Profile JSON exceeds 1 MiB.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous);
        var manifest = await JsonSerializer.DeserializeAsync<ProfileManifest>(stream, ReadOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The profile JSON is empty.");
        Validate(manifest);
        return new(manifest, HasConflict(manifest.Id!));
    }

    public async Task<ProfileTransferResult?> ImportAsync(string path,
        ProfileImportConflictChoice choice = ProfileImportConflictChoice.NewProfile, CancellationToken cancellationToken = default)
    {
        var inspection = await InspectAsync(path, cancellationToken).ConfigureAwait(false);
        return await ImportManifestAsync(inspection.Manifest, choice, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProfileTransferResult?> ImportManifestAsync(ProfileManifest manifest, ProfileImportConflictChoice choice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!Enum.IsDefined(choice)) throw new ArgumentOutOfRangeException(nameof(choice));
        if (choice == ProfileImportConflictChoice.Cancel) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await InstallCoreAsync(manifest, choice, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    internal async Task<T> InstallAtomicallyAsync<T>(ProfileManifest manifest, ProfileImportConflictChoice choice,
        Func<ProfileTransferResult, Task<T>> completeInstallation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var originalId = manifest.Id!;
            var previous = (await authoring.ListAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(item => string.Equals(item.Id, originalId, StringComparison.OrdinalIgnoreCase));
            var previousManifest = previous is null ? null : await authoring.LoadAsync(previous, cancellationToken).ConfigureAwait(false);
            var result = await InstallCoreAsync(manifest, choice, cancellationToken).ConfigureAwait(false)
                ?? throw new OperationCanceledException(cancellationToken);
            try { return await completeInstallation(result).ConfigureAwait(false); }
            catch
            {
                // No source data or existing index is changed by this transaction. Restore only the installed manifest.
                if (previousManifest is not null && string.Equals(result.ProfileId, originalId, StringComparison.OrdinalIgnoreCase))
                {
                    var current = new EditableProfileSummary(result.ProfileId, result.ProfileId, result.ManifestPath);
                    await authoring.LoadAsync(current, CancellationToken.None).ConfigureAwait(false);
                    await authoring.SaveAndApplyAsync(previousManifest, result.ProfileId, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    if (File.Exists(result.ManifestPath)) File.Delete(result.ManifestPath);
                    publisher.Publish(new ProfileCatalogSnapshot(
                        catalog.Current.Profiles.Where(p => !string.Equals(p.Descriptor.Id, result.ProfileId, StringComparison.OrdinalIgnoreCase)).ToArray(),
                        catalog.Current.Reports.Where(r => !string.Equals(r.ProfileId, result.ProfileId, StringComparison.OrdinalIgnoreCase)).ToArray(),
                        DateTimeOffset.UtcNow));
                }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<ProfileTransferResult?> InstallCoreAsync(ProfileManifest original, ProfileImportConflictChoice choice, CancellationToken ct)
    {
        Validate(original);
        var manifest = JsonSerializer.Deserialize<ProfileManifest>(JsonSerializer.SerializeToUtf8Bytes(original, WriteOptions), ReadOptions)!;
        manifest.Id = authoring.Validate(manifest).Profile!.Descriptor.Id;
        var existing = HasConflict(manifest.Id!);
        string? replaceId = null;
        if (existing && choice == ProfileImportConflictChoice.NewProfile)
        {
            var importedId = manifest.Id!;
            var prefix = importedId[..Math.Min(importedId.Length, 48)];
            do { manifest.Id = prefix + "-" + Guid.NewGuid().ToString("N")[..8]; } while (HasConflict(manifest.Id));
        }
        else if (existing && choice == ProfileImportConflictChoice.Replace)
        {
            var saved = (await authoring.ListAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(item => string.Equals(item.Id, manifest.Id, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("A bundled or assembly profile cannot be replaced. Import as a new profile.");
            await authoring.LoadAsync(saved, ct).ConfigureAwait(false);
            replaceId = manifest.Id;
        }
        var installedPath = Path.Combine(authoring.UserProfilesDirectory, manifest.Id!, "profile.json");
        var previousBytes = File.Exists(installedPath) ? await File.ReadAllBytesAsync(installedPath, ct).ConfigureAwait(false) : null;
        var priorCatalog = catalog.Current;
        var stagedBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, WriteOptions);
        try
        {
            var result = await authoring.SaveAndApplyAsync(manifest, replaceId, ct).ConfigureAwait(false);
            if (!result.FileSaved || !result.Applied)
                throw new InvalidDataException(string.Join(Environment.NewLine, result.Review.Diagnostics.Select(d => d.Message)));
            return new(manifest.Id!, installedPath);
        }
        catch
        {
            // Save can fail after the atomic file move (for example an observer throwing during Publish).
            // Restore only bytes still equal to this import; never overwrite a concurrent external edit.
            if (File.Exists(installedPath)
                && (await File.ReadAllBytesAsync(installedPath, CancellationToken.None).ConfigureAwait(false)).AsSpan().SequenceEqual(stagedBytes))
            {
                if (previousBytes is null) File.Delete(installedPath);
                else
                {
                    var restore = installedPath + "." + Guid.NewGuid().ToString("N") + ".restore";
                    try
                    {
                        await File.WriteAllBytesAsync(restore, previousBytes, CancellationToken.None).ConfigureAwait(false);
                        File.Move(restore, installedPath, true);
                    }
                    finally { if (File.Exists(restore)) File.Delete(restore); }
                }
                var current = catalog.Current;
                var id = manifest.Id!;
                var restored = new ProfileCatalogSnapshot(
                    current.Profiles.Where(p => !string.Equals(p.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase))
                        .Concat(priorCatalog.Profiles.Where(p => string.Equals(p.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase))).ToArray(),
                    current.Reports.Where(p => !string.Equals(p.ProfileId, id, StringComparison.OrdinalIgnoreCase))
                        .Concat(priorCatalog.Reports.Where(p => string.Equals(p.ProfileId, id, StringComparison.OrdinalIgnoreCase))).ToArray(),
                    DateTimeOffset.UtcNow);
                try { publisher.Publish(restored); } catch (Exception) { /* The original installation error remains authoritative. */ }
                if (previousBytes is not null)
                    await authoring.LoadAsync(new(id, id, installedPath), CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
    }

    public async Task ExportAsync(ProfileManifest manifest, string destination, CancellationToken cancellationToken = default)
    {
        Validate(manifest);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, WriteOptions);
        if (bytes.LongLength > ProfileManifestLimits.MaximumLengthBytes) throw new InvalidDataException("Profile JSON exceeds 1 MiB.");
        var target = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private bool HasConflict(string id) => catalog.Current.Profiles.Any(p => string.Equals(p.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase))
        || File.Exists(Path.Combine(authoring.UserProfilesDirectory, id, "profile.json"));

    private void Validate(ProfileManifest manifest)
    {
        if (manifest.Kind != ProfileKind.Declarative) throw new InvalidDataException("Only declarative JSON profiles can be transferred; executable assemblies are not imported.");
        var review = authoring.Validate(manifest);
        if (!review.IsValid) throw new InvalidDataException(string.Join(Environment.NewLine, review.Diagnostics.Select(d => d.Message)));
    }

    internal string GetProjectionFingerprint(ProfileManifest manifest)
    {
        Validate(manifest);
        var profile = authoring.Validate(manifest).Profile!;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(profile.Descriptor) + "|" + profile.CacheIdentity)));
    }
}
