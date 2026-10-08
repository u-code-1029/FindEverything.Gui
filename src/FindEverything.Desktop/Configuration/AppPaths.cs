namespace FindEverything.Desktop.Configuration;

public sealed record AppPaths(
    string LocalDataDirectory,
    string UserSettingsFile)
{
    public string GridLayoutFile => Path.Combine(LocalDataDirectory, "grid-layout.json");

    public string SelectionOutputFormatsFile =>
        Path.Combine(LocalDataDirectory, "selection-output-formats.json");

    public string UserProfilesDirectory => Path.Combine(LocalDataDirectory, "Profiles");

    public string IndexesDirectory => Path.Combine(LocalDataDirectory, "Indexes");

    /// <summary>
    /// The file-search database keeps the original location so an upgrade can use
    /// an existing index without copying a potentially large SQLite file.
    /// </summary>
    public string FileSearchIndexDatabaseFile => Path.Combine(IndexesDirectory, "metadata.db");

    public string ProfileIndexesDirectory => Path.Combine(IndexesDirectory, "Profiles");

    // Kept as a source-compatible alias for older callers.
    public string IndexDatabaseFile => FileSearchIndexDatabaseFile;

    public static AppPaths Create()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        var directory = Path.Combine(localApplicationData, "UCode", "FindEverything.Gui");
        return new AppPaths(directory, Path.Combine(directory, "appsettings.user.json"));
    }
}
