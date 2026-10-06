using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FindEverything.Profile.Runtime;

internal sealed class ProfilePluginLoader : IProfilePluginLoader
{
    private readonly PluginDiscoveryOptions _options;
    private readonly ProfileManifestReader _manifestReader;
    private readonly ProfileModelCompiler _modelCompiler;
    private readonly ILogger<ProfilePluginLoader> _logger;

    public ProfilePluginLoader(
        IOptions<PluginDiscoveryOptions> options,
        ProfileManifestReader manifestReader,
        ProfileModelCompiler modelCompiler,
        ILogger<ProfilePluginLoader> logger)
    {
        _options = options.Value;
        _manifestReader = manifestReader;
        _modelCompiler = modelCompiler;
        _logger = logger;
    }

    public async Task<ProfileCatalogSnapshot> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var profilesDirectory = ResolveProfilesDirectory(_options.ProfilesDirectory);
        string[] sourceDirectories;
        try
        {
            if (!Directory.Exists(profilesDirectory))
            {
                return SnapshotWithDirectoryError(
                    profilesDirectory,
                    "profiles_directory_missing",
                    "프로필 디렉터리를 찾을 수 없습니다.");
            }

            sourceDirectories = Directory
                .EnumerateDirectories(profilesDirectory, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(exception, "Could not enumerate profile directory {ProfilesDirectory}", profilesDirectory);
            return SnapshotWithDirectoryError(
                profilesDirectory,
                "profiles_directory_read_failed",
                "프로필 디렉터리를 읽을 수 없습니다.",
                exception.Message);
        }

        var reports = new List<ProfilePluginReport>();
        var manifestResults = new List<(string SourceDirectory, ManifestReadResult Result)>();
        var candidates = new List<ValidatedProfileManifest>();

        foreach (var sourceDirectory in sourceDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _manifestReader
                .ReadAsync(sourceDirectory, _options.ContractMajor, cancellationToken)
                .ConfigureAwait(false);
            manifestResults.Add((sourceDirectory, result));
        }

        var duplicateIds = manifestResults
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Result.ProfileId))
            .GroupBy(static entry => entry.Result.ProfileId!, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Skip(1).Any())
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in manifestResults)
        {
            var result = entry.Result;
            if (result.ProfileId is { } profileId && duplicateIds.Contains(profileId))
            {
                var diagnostics = result.Diagnostics
                    .Append(new ProfileDiagnostic(
                        ProfileDiagnosticSeverity.Error,
                        "profile_id_duplicate",
                        $"중복된 프로필 id가 발견되어 모든 '{profileId}' 프로필을 비활성화했습니다."))
                    .ToArray();
                reports.Add(new ProfilePluginReport(
                    entry.SourceDirectory,
                    result.ProfileId,
                    result.DisplayName,
                    ProfilePluginStatus.Disabled,
                    diagnostics));
                continue;
            }

            if (result.Manifest is null)
            {
                reports.Add(new ProfilePluginReport(
                    entry.SourceDirectory,
                    result.ProfileId,
                    result.DisplayName,
                    ProfilePluginStatus.Disabled,
                    result.Diagnostics));
                continue;
            }

            candidates.Add(result.Manifest);
        }

        var loadedProfiles = new List<ILoadedProfile>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LoadCandidate(candidate, loadedProfiles, reports);
        }

        return new ProfileCatalogSnapshot(
            loadedProfiles,
            reports,
            DateTimeOffset.UtcNow);
    }

    private void LoadCandidate(
        ValidatedProfileManifest candidate,
        ICollection<ILoadedProfile> loadedProfiles,
        ICollection<ProfilePluginReport> reports)
    {
        try
        {
            var loadContext = new ProfileLoadContext(candidate.EntryAssemblyPath);
            var assembly = loadContext.LoadFromAssemblyPath(candidate.EntryAssemblyPath);
            var compilation = _modelCompiler.Compile(assembly, candidate);

            if (compilation.Profile is null)
            {
                reports.Add(Disabled(candidate, compilation.Diagnostics));
                return;
            }

            loadedProfiles.Add(compilation.Profile);
            reports.Add(new ProfilePluginReport(
                candidate.SourceDirectory,
                candidate.Id,
                candidate.DisplayName,
                ProfilePluginStatus.Loaded,
                compilation.Diagnostics));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Could not load profile {ProfileId} from {ProfileDirectory}",
                candidate.Id,
                candidate.SourceDirectory);
            reports.Add(Disabled(
                candidate,
                new ProfileDiagnostic(
                    ProfileDiagnosticSeverity.Error,
                    "profile_load_failed",
                    "프로필 어셈블리를 로드할 수 없습니다.",
                    exception.GetBaseException().Message)));
        }
    }

    private static ProfilePluginReport Disabled(
        ValidatedProfileManifest candidate,
        params ProfileDiagnostic[] diagnostics) =>
        Disabled(candidate, (IReadOnlyList<ProfileDiagnostic>)diagnostics);

    private static ProfilePluginReport Disabled(
        ValidatedProfileManifest candidate,
        IReadOnlyList<ProfileDiagnostic> diagnostics) =>
        new(
            candidate.SourceDirectory,
            candidate.Id,
            candidate.DisplayName,
            ProfilePluginStatus.Disabled,
            diagnostics);

    private static string ResolveProfilesDirectory(string configuredPath) =>
        Path.GetFullPath(
            Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(AppContext.BaseDirectory, configuredPath));

    private static ProfileCatalogSnapshot SnapshotWithDirectoryError(
        string profilesDirectory,
        string code,
        string message,
        string? detail = null)
    {
        var report = new ProfilePluginReport(
            profilesDirectory,
            null,
            null,
            ProfilePluginStatus.Disabled,
            new[]
            {
                new ProfileDiagnostic(ProfileDiagnosticSeverity.Error, code, message, detail),
            });

        return new ProfileCatalogSnapshot(
            Array.Empty<ILoadedProfile>(),
            new[] { report },
            DateTimeOffset.UtcNow);
    }
}
