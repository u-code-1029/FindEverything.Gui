using System.ComponentModel.DataAnnotations;
using FindEverything.Profile.Runtime;

namespace FindEverything.Application.Options;

public sealed class WorkspaceOptions : IValidatableObject
{
    public const string SectionName = "Workspace";

    public const int MaximumProfileEntryPathProfiles = 256;

    public const int MaximumProfileEntryPathProfileIdLength = 128;

    public string? SelectedProfileId { get; set; }

    public string? RootPath { get; set; }

    /// <summary>
    /// Legacy shared index path. New settings write file-search and profile paths
    /// separately, but this property remains readable so existing installations do
    /// not lose their file-search index after upgrading.
    /// </summary>
    public string? DatabasePath { get; set; }

    /// <summary>
    /// Optional user-selected database for the unstructured file-search page.
    /// Null uses the application-local default.
    /// </summary>
    public string? FileSearchDatabasePath { get; set; }

    /// <summary>
    /// Optional user-selected database paths keyed by profile id. Profiles without
    /// an entry use their isolated application-local database.
    /// </summary>
    public Dictionary<string, string>? ProfileDatabasePaths { get; set; }

    /// <summary>
    /// Machine-local scan entry paths keyed by profile id. Null leaves existing
    /// workspace settings unspecified; an empty list explicitly clears a profile's paths.
    /// Profile lists are stored independently from parser descriptors. The legacy/file-search
    /// RootPath fallback may still follow the most recently selected location.
    /// </summary>
    public Dictionary<string, List<string>>? ProfileEntryPaths { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (profileId, databasePath) in ProfileDatabasePaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(profileId))
            {
                yield return new ValidationResult(
                    "Profile database path keys must contain a profile id.",
                    [nameof(ProfileDatabasePaths)]);
                continue;
            }

            if (!profileIds.Add(profileId.Trim()))
            {
                yield return new ValidationResult(
                    $"Profile database path keys must be unique ignoring case: {profileId}",
                    [nameof(ProfileDatabasePaths)]);
            }

            if (string.IsNullOrWhiteSpace(databasePath))
            {
                yield return new ValidationResult(
                    $"The database path for profile '{profileId}' cannot be empty.",
                    [nameof(ProfileDatabasePaths)]);
            }
        }

        foreach (var result in ValidateProfileEntryPaths())
        {
            yield return result;
        }
    }

    private IEnumerable<ValidationResult> ValidateProfileEntryPaths()
    {
        if (ProfileEntryPaths is null)
        {
            yield break;
        }

        if (ProfileEntryPaths.Count > MaximumProfileEntryPathProfiles)
        {
            yield return EntryPathError(
                $"Entry paths can be stored for at most {MaximumProfileEntryPathProfiles} profiles.");
            yield break;
        }

        var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (profileId, entryPaths) in ProfileEntryPaths)
        {
            if (string.IsNullOrWhiteSpace(profileId)
                || profileId.Length > MaximumProfileEntryPathProfileIdLength)
            {
                yield return EntryPathError(
                    $"Entry path keys must contain a profile id of at most {MaximumProfileEntryPathProfileIdLength} characters.");
            }
            else if (!profileIds.Add(profileId.Trim()))
            {
                yield return EntryPathError(
                    $"Entry path profile ids must be unique ignoring case: {profileId}");
            }

            if (entryPaths is null)
            {
                yield return EntryPathError(
                    $"Entry paths for profile '{profileId}' must be a list; use an empty list to clear them.");
                continue;
            }

            if (entryPaths.Count > ProfileWorkspaceManifestValidator.MaximumEntryPaths)
            {
                yield return EntryPathError(
                    $"Profile '{profileId}' can have at most {ProfileWorkspaceManifestValidator.MaximumEntryPaths} entry paths.");
                continue;
            }

            foreach (var path in entryPaths)
            {
                if (!ProfileWorkspaceManifestValidator.IsAbsoluteEntryPath(path))
                {
                    yield return EntryPathError(
                        $"Every entry path for profile '{profileId}' must be a nonempty absolute path of at most {ProfileWorkspaceManifestValidator.MaximumEntryPathLength} characters.");
                }
            }
        }
    }

    private static ValidationResult EntryPathError(string message) =>
        new(message, [nameof(ProfileEntryPaths)]);
}

public sealed class IndexingOptions
{
    public const string SectionName = "Indexing";

    [Range(1, 1000)]
    public int SearchPageSize { get; set; } = 1000;

    [Range(1, int.MaxValue)]
    public int MaxEntriesPerSecond { get; set; } = 2000;

    [Range(0, int.MaxValue)]
    public int DirectoryDelayMilliseconds { get; set; } = 5;

    /// <summary>Publish scan progress and newly discovered rows together after this many candidates.</summary>
    [Range(1, 1000)]
    public int UiUpdateBatchSize { get; set; } = 10;
}

public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    [Range(1, 60)]
    public int AutoDismissSeconds { get; set; } = 3;
}

public enum ThemePreference
{
    System,
    Light,
    Dark
}

public enum BackdropPreference
{
    Auto,
    None
}

public enum CatalogRowDensity
{
    Narrow,
    Normal,
    Wide
}

public sealed class AppearanceOptions : IValidatableObject
{
    public const string SectionName = "Appearance";

    public ThemePreference Theme { get; set; } = ThemePreference.System;

    public BackdropPreference Backdrop { get; set; } = BackdropPreference.Auto;

    public CatalogRowDensity CatalogRowDensity { get; set; } = CatalogRowDensity.Normal;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enum.IsDefined(Theme) || !Enum.IsDefined(Backdrop) || !Enum.IsDefined(CatalogRowDensity))
            yield return new ValidationResult("Appearance values must be supported enum values.",
                [nameof(Theme), nameof(Backdrop), nameof(CatalogRowDensity)]);
    }
}

public sealed class LocalizationOptions
{
    public const string SectionName = "Localization";

    public const string KoreanCultureName = "ko-KR";

    public const string EnglishCultureName = "en-US";

    public string CultureName { get; set; } = KoreanCultureName;

    public static bool IsSupported(string? cultureName) =>
        string.Equals(cultureName, KoreanCultureName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(cultureName, EnglishCultureName, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string cultureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cultureName);
        if (string.Equals(cultureName, KoreanCultureName, StringComparison.OrdinalIgnoreCase))
        {
            return KoreanCultureName;
        }

        if (string.Equals(cultureName, EnglishCultureName, StringComparison.OrdinalIgnoreCase))
        {
            return EnglishCultureName;
        }

        throw new ArgumentOutOfRangeException(
            nameof(cultureName),
            cultureName,
            "Only ko-KR and en-US are supported.");
    }
}
