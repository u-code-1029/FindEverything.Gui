using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using FindEverything.Desktop.Configuration;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.Services;

public sealed record EditableProfileSummary(
    string Id,
    string DisplayName,
    string ManifestPath)
{
    public override string ToString() => DisplayName;
}

public sealed record ProfileSaveResult(
    bool FileSaved,
    bool Applied,
    ProfileDefinitionReview Review,
    ProfileCatalogSnapshot? Snapshot);

public interface IProfileAuthoringService
{
    string UserProfilesDirectory { get; }

    Task<IReadOnlyList<EditableProfileSummary>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<ProfileManifest?> LoadAsync(
        EditableProfileSummary profile,
        CancellationToken cancellationToken = default);

    ProfileDefinitionReview Validate(ProfileManifest manifest);

    ProfileDefinitionTestResult Test(ProfileManifest manifest, string samplePath);

    Task<ProfileSaveResult> SaveAndApplyAsync(
        ProfileManifest manifest,
        string? originalProfileId,
        CancellationToken cancellationToken = default);
}

public sealed class ProfileAuthoringService : IProfileAuthoringService
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly IProfileDefinitionCompiler _definitionCompiler;
    private readonly IProfileCatalog _catalog;
    private readonly IProfileCatalogPublisher _catalogPublisher;
    private readonly ILogger<ProfileAuthoringService> _logger;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _loadedFingerprints =
        new(StringComparer.OrdinalIgnoreCase);

    public ProfileAuthoringService(
        AppPaths paths,
        IProfileDefinitionCompiler definitionCompiler,
        IProfileCatalog catalog,
        IProfileCatalogPublisher catalogPublisher,
        ILogger<ProfileAuthoringService> logger)
    {
        UserProfilesDirectory = paths.UserProfilesDirectory;
        _definitionCompiler = definitionCompiler;
        _catalog = catalog;
        _catalogPublisher = catalogPublisher;
        _logger = logger;
    }

    public string UserProfilesDirectory { get; }

    public async Task<IReadOnlyList<EditableProfileSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(UserProfilesDirectory))
        {
            return [];
        }

        var profiles = new List<EditableProfileSummary>();
        foreach (var directory in Directory
                     .EnumerateDirectories(UserProfilesDirectory, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(directory, "profile.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                if (new FileInfo(manifestPath).Length > ProfileManifestLimits.MaximumLengthBytes)
                {
                    _logger.LogWarning(
                        "Editable profile manifest exceeds the 1 MiB limit: {ManifestPath}",
                        manifestPath);
                    continue;
                }

                await using var stream = File.OpenRead(manifestPath);
                var manifest = await JsonSerializer.DeserializeAsync<ProfileManifest>(
                    stream,
                    ReadOptions,
                    cancellationToken).ConfigureAwait(false);
                if (manifest?.Kind == ProfileKind.Declarative
                    && !string.IsNullOrWhiteSpace(manifest.Id))
                {
                    profiles.Add(new EditableProfileSummary(
                        manifest.Id.Trim(),
                        string.IsNullOrWhiteSpace(manifest.DisplayName)
                            ? manifest.Id.Trim()
                            : manifest.DisplayName.Trim(),
                        manifestPath));
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogWarning(exception, "Could not read editable profile {ManifestPath}.", manifestPath);
            }
        }

        return profiles
            .OrderBy(static profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<ProfileManifest?> LoadAsync(
        EditableProfileSummary profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var expectedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(UserProfilesDirectory))
            + Path.DirectorySeparatorChar;
        var manifestPath = Path.GetFullPath(profile.ManifestPath);
        if (!manifestPath.StartsWith(
                expectedRoot,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("사용자 프로필 폴더 밖의 파일은 편집할 수 없습니다.");
        }

        if (new FileInfo(manifestPath).Length > ProfileManifestLimits.MaximumLengthBytes)
        {
            throw new InvalidOperationException(
                "profile.json 파일이 허용 크기(1 MiB)를 초과해 편집기에서 열 수 없습니다.");
        }

        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<ProfileManifest>(
            stream,
            ReadOptions,
            cancellationToken).ConfigureAwait(false);
        if (manifest?.Kind == ProfileKind.Declarative
            && !string.IsNullOrWhiteSpace(manifest.Id))
        {
            stream.Position = 0;
            _loadedFingerprints[manifest.Id.Trim()] = await ComputeFingerprintAsync(
                stream,
                cancellationToken).ConfigureAwait(false);
        }

        return manifest?.Kind == ProfileKind.Declarative ? manifest : null;
    }

    public ProfileDefinitionReview Validate(ProfileManifest manifest) =>
        _definitionCompiler.Validate(manifest);

    public ProfileDefinitionTestResult Test(ProfileManifest manifest, string samplePath) =>
        _definitionCompiler.Test(manifest, samplePath);

    public async Task<ProfileSaveResult> SaveAndApplyAsync(
        ProfileManifest manifest,
        string? originalProfileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var serializedManifest = JsonSerializer.SerializeToUtf8Bytes(manifest, WriteOptions);
            if (serializedManifest.LongLength > ProfileManifestLimits.MaximumLengthBytes)
            {
                return new ProfileSaveResult(
                    false,
                    false,
                    new ProfileDefinitionReview(
                        null,
                        [new ProfileDiagnostic(
                            ProfileDiagnosticSeverity.Error,
                            "manifest_too_large",
                            "profile.json 파일이 허용 크기(1 MiB)를 초과했습니다.")]),
                    null);
            }

            var review = Validate(manifest);
            if (!review.IsValid || review.Profile is null)
            {
                return new ProfileSaveResult(false, false, review, null);
            }

            var profileId = review.Profile.Descriptor.Id;
            if (originalProfileId is not null
                && !string.Equals(profileId, originalProfileId, StringComparison.OrdinalIgnoreCase))
            {
                return InvalidSave(
                    review,
                    "profile_id_immutable",
                    "저장된 프로필의 ID는 변경할 수 없습니다. 새 프로필로 만들어 주세요.");
            }

            var profileDirectory = GetProfileDirectory(profileId);
            var manifestPath = Path.Combine(profileDirectory, "profile.json");
            var existingElsewhere = _catalog.Current.Reports.Any(report =>
                string.Equals(report.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)
                && !PathsEqual(report.SourceDirectory, profileDirectory));
            if (existingElsewhere)
            {
                return InvalidSave(
                    review,
                    "profile_id_duplicate",
                    $"다른 위치에 같은 프로필 ID가 있습니다: {profileId}");
            }

            Directory.CreateDirectory(profileDirectory);
            FileStream profileLock;
            try
            {
                profileLock = new FileStream(
                    Path.Combine(profileDirectory, ".profile.write.lock"),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    exception,
                    "Could not acquire the profile write lock for {ProfileId}.",
                    profileId);
                return InvalidSave(
                    review,
                    "profile_write_locked",
                    "다른 앱 창에서 이 프로필을 저장하고 있습니다. 잠시 후 다시 시도하세요.");
            }

            await using var heldProfileLock = profileLock;
            string? expectedFingerprint = null;
            if (originalProfileId is null && File.Exists(manifestPath))
            {
                return InvalidSave(
                    review,
                    "profile_already_exists",
                    $"이미 존재하는 GUI 프로필 ID입니다: {profileId}");
            }
            else if (originalProfileId is not null)
            {
                if (!_loadedFingerprints.TryGetValue(originalProfileId, out expectedFingerprint))
                {
                    return InvalidSave(
                        review,
                        "profile_edit_session_missing",
                        "프로필을 다시 연 뒤 저장하세요.");
                }

                if (!File.Exists(manifestPath))
                {
                    return InvalidSave(
                        review,
                        "profile_changed_externally",
                        "프로필 파일이 외부에서 이동되거나 삭제되었습니다. 목록에서 다시 여세요.");
                }

                var currentFingerprint = await ComputeFileFingerprintAsync(
                    manifestPath,
                    cancellationToken).ConfigureAwait(false);
                if (!string.Equals(
                        currentFingerprint,
                        expectedFingerprint,
                        StringComparison.Ordinal))
                {
                    return InvalidSave(
                        review,
                        "profile_changed_externally",
                        "프로필 파일이 외부에서 변경되었습니다. 변경 내용을 덮어쓰지 않도록 저장을 중단했습니다. 목록에서 다시 여세요.");
                }
            }

            var temporaryPath = Path.Combine(
                profileDirectory,
                $"profile.{Guid.NewGuid():N}.tmp");
            ProfileDefinitionReview stagedReview;
            string stagedFingerprint;
            ProfileCatalogSnapshot snapshot;
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 16 * 1024,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(serializedManifest, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.Read,
                                 16 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var stagedManifest = await JsonSerializer.DeserializeAsync<ProfileManifest>(
                        stream,
                        ReadOptions,
                        cancellationToken).ConfigureAwait(false);
                    if (stagedManifest is null)
                    {
                        return InvalidSave(
                            review,
                            "profile_staging_failed",
                            "저장할 프로필 파일을 다시 읽을 수 없습니다.");
                    }

                    stagedReview = Validate(stagedManifest);
                    if (!stagedReview.IsValid || stagedReview.Profile is null)
                    {
                        return new ProfileSaveResult(false, false, stagedReview, null);
                    }

                    stream.Position = 0;
                    stagedFingerprint = await ComputeFingerprintAsync(
                        stream,
                        cancellationToken).ConfigureAwait(false);
                }

                snapshot = CreateAppliedSnapshot(profileDirectory, stagedReview);
                if (expectedFingerprint is not null)
                {
                    var latestFingerprint = await ComputeFileFingerprintAsync(
                        manifestPath,
                        cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(
                            latestFingerprint,
                            expectedFingerprint,
                            StringComparison.Ordinal))
                    {
                        return InvalidSave(
                            review,
                            "profile_changed_externally",
                            "프로필 파일이 저장 준비 중 외부에서 변경되었습니다. 목록에서 다시 여세요.");
                    }
                }

                File.Move(temporaryPath, manifestPath, overwrite: true);
                _loadedFingerprints[profileId] = stagedFingerprint;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(
                            exception,
                            "Could not remove temporary profile manifest {TemporaryPath}.",
                            temporaryPath);
                    }
                }
            }

            _catalogPublisher.Publish(snapshot);

            return new ProfileSaveResult(true, true, stagedReview, snapshot);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private string GetProfileDirectory(string profileId)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(UserProfilesDirectory));
        var directory = Path.GetFullPath(Path.Combine(root, profileId));
        var prefix = root + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(
                prefix,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("프로필 저장 경로가 사용자 프로필 폴더를 벗어났습니다.");
        }

        return directory;
    }

    private ProfileCatalogSnapshot CreateAppliedSnapshot(
        string profileDirectory,
        ProfileDefinitionReview review)
    {
        var profile = review.Profile
            ?? throw new InvalidOperationException("검증된 프로필을 찾을 수 없습니다.");
        var current = _catalog.Current;
        var profileId = profile.Descriptor.Id;
        var profiles = current.Profiles
            .Where(item => !string.Equals(
                item.Descriptor.Id,
                profileId,
                StringComparison.OrdinalIgnoreCase))
            .Append(profile)
            .ToArray();
        var reports = current.Reports
            .Where(report => !PathsEqual(report.SourceDirectory, profileDirectory))
            .Append(new ProfilePluginReport(
                profileDirectory,
                profileId,
                profile.Descriptor.DisplayName,
                ProfilePluginStatus.Loaded,
                review.Diagnostics))
            .ToArray();
        return new ProfileCatalogSnapshot(profiles, reports, DateTimeOffset.UtcNow);
    }

    private static ProfileSaveResult InvalidSave(
        ProfileDefinitionReview review,
        string code,
        string message)
    {
        var diagnostics = review.Diagnostics.Append(new ProfileDiagnostic(
            ProfileDiagnosticSeverity.Error,
            code,
            message)).ToArray();
        return new ProfileSaveResult(
            false,
            false,
            review with { Profile = null, Diagnostics = diagnostics },
            null);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static async Task<string> ComputeFingerprintAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static async Task<string> ComputeFileFingerprintAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ComputeFingerprintAsync(stream, cancellationToken).ConfigureAwait(false);
    }
}
