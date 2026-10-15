using System.Diagnostics;
using FindEverything.Application.Catalog;
using FindEverything.Desktop.Localization;

namespace FindEverything.Desktop.Services;

public sealed class ExplorerPathLauncher : IPathLauncher
{
    private readonly IAppLocalizer? _localizer;
    private readonly SemaphoreSlim _launchGate = new(1, 1);

    public Task OpenDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        RunAsync(() => OpenDirectory(path), cancellationToken);
    public Task OpenPathAsync(string path, CancellationToken cancellationToken = default) =>
        RunAsync(() => OpenPath(path), cancellationToken);
    public Task ShowInFolderAsync(string path, CancellationToken cancellationToken = default) =>
        RunAsync(() => ShowInFolder(path), cancellationToken);

    private async Task RunAsync(Action launch, CancellationToken cancellationToken)
    {
        if (!await _launchGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException(_localizer?.Get("Loc.Path.Open.Pending", "이전 경로를 여는 중입니다. 잠시 후 다시 시도하세요.")
                ?? "이전 경로를 여는 중입니다. 잠시 후 다시 시도하세요.");
        try { await Task.Run(launch, cancellationToken); }
        finally { _launchGate.Release(); }
    }

    public ExplorerPathLauncher(IAppLocalizer? localizer = null) =>
        _localizer = localizer;

    public void OpenDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(F(
                "Loc.Path.Error.FolderNotFound",
                "폴더를 찾을 수 없습니다: {0}",
                path));
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
            throw new FileNotFoundException(F(
                "Loc.Path.Error.FileNotFound",
                "파일을 찾을 수 없습니다: {0}",
                path), path);
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
            throw new FileNotFoundException(F(
                "Loc.Path.Error.FileNotFound",
                "파일을 찾을 수 없습니다: {0}",
                path), path);
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

    private string F(string key, string koreanFallback, params object?[] arguments) =>
        _localizer?.Format(key, koreanFallback, arguments)
        ?? string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            koreanFallback,
            arguments);
}
