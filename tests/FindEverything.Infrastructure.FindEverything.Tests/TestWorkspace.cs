namespace FindEverything.Infrastructure.FindEverything.Tests;

internal sealed class TestWorkspace : IDisposable
{
    private readonly string _workspacePath;

    public TestWorkspace()
    {
        var tempPath = Path.GetFullPath(Path.GetTempPath());
        if (OperatingSystem.IsMacOS() && tempPath.StartsWith("/var/", StringComparison.Ordinal))
            tempPath = "/private" + tempPath;

        _workspacePath = Path.Combine(tempPath, "find-everything-gui-tests", Guid.NewGuid().ToString("N"));
        SourcePath = Path.Combine(_workspacePath, "source");
        DatabasePath = Path.Combine(_workspacePath, "index", "metadata.db");
        Directory.CreateDirectory(SourcePath);
    }

    public string SourcePath { get; }

    public string DatabasePath { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspacePath))
                Directory.Delete(_workspacePath, recursive: true);
        }
        catch (IOException)
        {
            // A failed test can leave a native SQLite handle briefly alive. The OS temp
            // directory remains the safe cleanup boundary for that diagnostic artifact.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
