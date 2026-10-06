using System.Diagnostics;
using FindEverything.Application.Catalog;

namespace FindEverything.Desktop.Services;

public sealed class ExplorerPathLauncher : IPathLauncher
{
    public void OpenDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"폴더를 찾을 수 없습니다: {path}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add(path);
        _ = Process.Start(startInfo);
    }

    public void OpenPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Directory.Exists(path))
        {
            OpenDirectory(path);
            return;
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"파일을 찾을 수 없습니다: {path}", path);
        }

        _ = Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    public void ShowInFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Directory.Exists(path))
        {
            OpenDirectory(path);
            return;
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"파일을 찾을 수 없습니다: {path}", path);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add("/select,");
        startInfo.ArgumentList.Add(path);
        _ = Process.Start(startInfo);
    }
}
