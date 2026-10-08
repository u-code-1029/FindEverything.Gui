using System.ComponentModel.DataAnnotations;

namespace FindEverything.Application.Options;

public sealed class WorkspaceOptions : IValidatableObject
{
    public const string SectionName = "Workspace";

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

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ProfileDatabasePaths is null)
        {
            yield break;
        }

        var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (profileId, databasePath) in ProfileDatabasePaths)
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
    }
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

public sealed class AppearanceOptions
{
    public const string SectionName = "Appearance";

    public ThemePreference Theme { get; set; } = ThemePreference.System;

    public BackdropPreference Backdrop { get; set; } = BackdropPreference.Auto;
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
