using System.ComponentModel.DataAnnotations;

namespace FindEverything.Application.Options;

public sealed class WorkspaceOptions
{
    public const string SectionName = "Workspace";

    public string? SelectedProfileId { get; set; }

    public string? RootPath { get; set; }

    public string? DatabasePath { get; set; }
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
