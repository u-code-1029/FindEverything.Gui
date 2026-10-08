using System.Security.Cryptography;
using System.Text;
using FindEverything.Application.Options;
using FindEverything.Desktop.Configuration;

namespace FindEverything.Desktop.Services;

public interface IIndexDatabasePathResolver
{
    string ResolveFileSearchDatabasePath();

    string ResolveFileSearchDatabasePath(WorkspaceOptions options);

    string ResolveFileSearchDatabasePath(WorkspaceSnapshot workspace);

    string ResolveProfileDatabasePath(string profileId);

    string ResolveProfileDatabasePath(string profileId, WorkspaceOptions options);

    string ResolveProfileDatabasePath(string profileId, WorkspaceSnapshot workspace);
}

/// <summary>
/// Resolves effective index locations while keeping automatic locations separate
/// from user-selected overrides. Default profile paths never contain an unchecked
/// profile id, and the stable suffix prevents sanitized ids from colliding.
/// </summary>
public sealed class IndexDatabasePathResolver(
    AppPaths paths,
    IValidatedSettingsState<WorkspaceOptions> workspaceSettings)
    : IIndexDatabasePathResolver
{
    private const int MaximumSlugLength = 48;
    private const int HashCharacterCount = 16;

    public string ResolveFileSearchDatabasePath() =>
        ResolveFileSearchDatabasePath(workspaceSettings.Current);

    public string ResolveFileSearchDatabasePath(WorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // DatabasePath is a read-only migration fallback. Once settings are next
        // saved, WorkspaceContext writes the value to FileSearchDatabasePath.
        var configuredPath = FirstNonBlank(
            options.FileSearchDatabasePath,
            options.DatabasePath);
        return configuredPath is null
            ? Path.GetFullPath(paths.FileSearchIndexDatabaseFile)
            : Path.GetFullPath(configuredPath);
    }

    public string ResolveFileSearchDatabasePath(WorkspaceSnapshot workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return string.IsNullOrWhiteSpace(workspace.DatabasePath)
            ? Path.GetFullPath(paths.FileSearchIndexDatabaseFile)
            : Path.GetFullPath(workspace.DatabasePath);
    }

    public string ResolveProfileDatabasePath(string profileId) =>
        ResolveProfileDatabasePath(profileId, workspaceSettings.Current);

    public string ResolveProfileDatabasePath(string profileId, WorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ResolveProfileDatabasePathCore(profileId, options.ProfileDatabasePaths);
    }

    public string ResolveProfileDatabasePath(string profileId, WorkspaceSnapshot workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return ResolveProfileDatabasePathCore(profileId, workspace.ProfileDatabasePaths);
    }

    private string ResolveProfileDatabasePathCore(
        string profileId,
        IReadOnlyDictionary<string, string>? configuredPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var normalizedProfileId = profileId.Trim();
        var configuredPath = FindProfileOverride(configuredPaths, normalizedProfileId);
        if (configuredPath is not null)
        {
            return Path.GetFullPath(configuredPath);
        }

        var directoryName = CreateProfileDirectoryName(normalizedProfileId);
        return Path.GetFullPath(Path.Combine(
            paths.ProfileIndexesDirectory,
            directoryName,
            "db.db"));
    }

    internal static string CreateProfileDirectoryName(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var canonicalId = profileId.Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant();
        var slugBuilder = new StringBuilder(Math.Min(profileId.Length, MaximumSlugLength));
        var previousWasSeparator = false;
        foreach (var character in profileId.Trim().Normalize(NormalizationForm.FormC))
        {
            if (slugBuilder.Length >= MaximumSlugLength)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(character))
            {
                slugBuilder.Append(char.ToLowerInvariant(character));
                previousWasSeparator = false;
                continue;
            }

            if (!previousWasSeparator && slugBuilder.Length > 0)
            {
                slugBuilder.Append('-');
                previousWasSeparator = true;
            }
        }

        var slug = slugBuilder.ToString().Trim('-');
        if (slug.Length == 0)
        {
            slug = "profile";
        }

        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonicalId)))
            .ToLowerInvariant()[..HashCharacterCount];
        return $"{slug}-{hash}";
    }

    private static string? FindProfileOverride(
        IReadOnlyDictionary<string, string>? configuredPaths,
        string profileId)
    {
        if (configuredPaths is null)
        {
            return null;
        }

        if (configuredPaths.TryGetValue(profileId, out var exact)
            && !string.IsNullOrWhiteSpace(exact))
        {
            return exact.Trim();
        }

        // IConfiguration may construct a dictionary with a case-sensitive comparer,
        // while profile lookup itself is OrdinalIgnoreCase. Resolve deterministically.
        return configuredPaths
            .Where(pair => string.Equals(pair.Key, profileId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => string.IsNullOrWhiteSpace(pair.Value) ? null : pair.Value.Trim())
            .FirstOrDefault(static value => value is not null);
    }

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
