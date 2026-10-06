namespace FindEverything.Desktop.Configuration;

public sealed record AppPaths(
    string LocalDataDirectory,
    string UserSettingsFile)
{
    public string GridLayoutFile => Path.Combine(LocalDataDirectory, "grid-layout.json");

    public string UserProfilesDirectory => Path.Combine(LocalDataDirectory, "Profiles");

    public string IndexDatabaseFile => Path.Combine(LocalDataDirectory, "Indexes", "metadata.db");

    public static AppPaths Create()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        var directory = Path.Combine(localApplicationData, "UCode", "FindEverything.Gui");
        return new AppPaths(directory, Path.Combine(directory, "appsettings.user.json"));
    }
}
