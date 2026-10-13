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
    private sealed record Installation(ProfileTransferResult Result, byte[]? PreviousBytes,
        byte[] InstalledBytes, ProfileCatalogSnapshot PriorCatalog);

    public async Task<ProfileTransferInspection> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (info.Length > ProfileManifestLimits.MaximumLengthBytes) throw new InvalidDataException("Profile JSON exceeds 1 MiB.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous);
        if (stream.Length > ProfileManifestLimits.MaximumLengthBytes) throw new InvalidDataException("Profile JSON exceeds 1 MiB.");
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
        try { return (await InstallCoreAsync(manifest, choice, cancellationToken).ConfigureAwait(false)).Result; }
        finally { _gate.Release(); }
    }

    internal async Task<T> InstallAtomicallyAsync<T>(ProfileManifest manifest, ProfileImportConflictChoice choice,
        Func<ProfileTransferResult, Task<T>> completeInstallation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!Enum.IsDefined(choice)) throw new ArgumentOutOfRangeException(nameof(choice));
        if (choice == ProfileImportConflictChoice.Cancel) throw new OperationCanceledException(cancellationToken);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var installation = await InstallCoreAsync(manifest, choice, cancellationToken).ConfigureAwait(false);
            try { return await completeInstallation(installation.Result).ConfigureAwait(false); }
            catch (Exception failure)
            {
                bool restored;
                try
                {
                    restored = await RollbackAsync(installation).ConfigureAwait(false);
                }
                catch (Exception rollback)
                {
                    throw new AggregateException("The import failed and its profile could not be restored. No external changes were overwritten.", failure, rollback);
                }
                if (!restored)
                    throw new InvalidOperationException("The import failed, but its profile was changed externally. The external edit was preserved; reload the profile before retrying.", failure);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<Installation> InstallCoreAsync(ProfileManifest original, ProfileImportConflictChoice choice, CancellationToken ct)
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
        var installation = new Installation(new(manifest.Id!, installedPath), previousBytes, stagedBytes, priorCatalog);
        try
        {
            var result = await authoring.SaveAndApplyAsync(manifest, replaceId, ct).ConfigureAwait(false);
            if (!result.FileSaved || !result.Applied)
                throw new InvalidDataException(string.Join(Environment.NewLine, result.Review.Diagnostics.Select(d => d.Message)));
            return installation;
        }
        catch (Exception failure)
        {
            // Save can fail after the atomic file move (for example an observer throwing during Publish).
            // Restore only bytes still equal to this import; never overwrite a concurrent external edit.
            bool restored = true;
            try
            {
                if (File.Exists(installedPath)
                    && (await File.ReadAllBytesAsync(installedPath, CancellationToken.None).ConfigureAwait(false)).AsSpan().SequenceEqual(stagedBytes))
                    restored = await RollbackAsync(installation).ConfigureAwait(false);
            }
            catch (Exception rollback)
            {
                // Preserve the original save failure as the first cause; never replace it with a read/observer/cleanup error.
                throw new AggregateException("The profile save failed and its previous state could not be restored.", failure, rollback);
            }
            if (!restored) throw new InvalidOperationException("The profile was changed externally during rollback. The external edit was preserved; reload it before retrying.", failure);
            throw;
        }
    }

    private async Task<bool> RollbackAsync(Installation installation)
    {
        var result = installation.Result;
        // Respect the same cross-process authoring lock while comparing and restoring our own bytes.
        await using var profileLock = new FileStream(Path.Combine(Path.GetDirectoryName(result.ManifestPath)!, ".profile.write.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        if (!File.Exists(result.ManifestPath) || !(await File.ReadAllBytesAsync(result.ManifestPath).ConfigureAwait(false)).AsSpan().SequenceEqual(installation.InstalledBytes))
        {
            await RefreshExternalProfileAsync(result).ConfigureAwait(false);
            return false;
        }
        if (installation.PreviousBytes is null) File.Delete(result.ManifestPath);
        else
        {
            var restore = result.ManifestPath + "." + Guid.NewGuid().ToString("N") + ".restore";
            try
            {
                await File.WriteAllBytesAsync(restore, installation.PreviousBytes).ConfigureAwait(false);
                File.Move(restore, result.ManifestPath, true);
            }
            finally { if (File.Exists(restore)) File.Delete(restore); }
        }
        var current = catalog.Current;
        var id = result.ProfileId;
        publisher.Publish(new ProfileCatalogSnapshot(
            current.Profiles.Where(p => !string.Equals(p.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase))
                .Concat(installation.PriorCatalog.Profiles.Where(p => string.Equals(p.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase))).ToArray(),
            current.Reports.Where(p => !string.Equals(p.ProfileId, id, StringComparison.OrdinalIgnoreCase))
                .Concat(installation.PriorCatalog.Reports.Where(p => string.Equals(p.ProfileId, id, StringComparison.OrdinalIgnoreCase))).ToArray(),
            DateTimeOffset.UtcNow));
        if (installation.PreviousBytes is not null) await authoring.LoadAsync(new(id, id, result.ManifestPath)).ConfigureAwait(false);
        return true;
    }

    private async Task RefreshExternalProfileAsync(ProfileTransferResult result)
    {
        ProfileDefinitionReview? review = null;
        try
        {
            if (File.Exists(result.ManifestPath))
            {
                var changed = await authoring.LoadAsync(new(result.ProfileId, result.ProfileId, result.ManifestPath)).ConfigureAwait(false);
                if (changed is not null && string.Equals(changed.Id, result.ProfileId, StringComparison.OrdinalIgnoreCase)) review = authoring.Validate(changed);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        var current = catalog.Current;
        var valid = review is { IsValid: true, Profile: not null };
        var profiles = current.Profiles.Where(p => !string.Equals(p.Descriptor.Id, result.ProfileId, StringComparison.OrdinalIgnoreCase));
        if (valid) profiles = profiles.Append(review!.Profile!);
        var report = new ProfilePluginReport(Path.GetDirectoryName(result.ManifestPath)!, result.ProfileId,
            valid ? review!.Profile!.Descriptor.DisplayName : result.ProfileId, valid ? ProfilePluginStatus.Loaded : ProfilePluginStatus.Disabled,
            review?.Diagnostics ?? [new(ProfileDiagnosticSeverity.Error, "profile_changed_externally", "The profile was changed externally during import; reload it before retrying.")]);
        publisher.Publish(new ProfileCatalogSnapshot(profiles.ToArray(),
            current.Reports.Where(r => !string.Equals(r.ProfileId, result.ProfileId, StringComparison.OrdinalIgnoreCase)).Append(report).ToArray(), DateTimeOffset.UtcNow));
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

    internal void ValidateSnapshotFields(ProfileManifest manifest, IReadOnlyList<ProfileFieldDescriptor> fields)
    {
        Validate(manifest);
        var expected = authoring.Validate(manifest).Profile!.Descriptor.Fields;
        if (fields is null || fields.Count != expected.Count
            || fields.Any(field => field is null || string.IsNullOrWhiteSpace(field.FieldId))
            || fields.Select(field => field.FieldId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Count)
            throw new InvalidDataException("The structured column definitions do not match the packaged profile.");
        foreach (var definition in expected)
        {
            var field = fields.FirstOrDefault(candidate => string.Equals(candidate.FieldId, definition.FieldId, StringComparison.OrdinalIgnoreCase));
            if (field is null || field.Kind != definition.Kind || field.SourceKind != definition.SourceKind
                || field.Required != definition.Required || field.IsNullable != definition.IsNullable || field.Order != definition.Order
                || field.Header != definition.Header || field.GroupName != definition.GroupName
                || field.ParseFormat != definition.ParseFormat || field.DisplayFormat != definition.DisplayFormat
                || field.Placeholder != definition.Placeholder || field.DefaultValue != definition.DefaultValue
                || field.GroupNames is null || !field.GroupNames.SequenceEqual(definition.GroupNames, StringComparer.Ordinal)
                || field.ValueMappings is null || field.ValueMappings.Count != definition.ValueMappings.Count
                || definition.ValueMappings.Any(mapping => !field.ValueMappings.TryGetValue(mapping.Key, out var display) || display != mapping.Value))
                throw new InvalidDataException("The structured column types, mappings, or display definitions do not match the packaged profile.");
        }
    }
}
