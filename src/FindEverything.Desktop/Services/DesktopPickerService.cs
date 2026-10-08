using Microsoft.Win32;

namespace FindEverything.Desktop.Services;

public interface IDesktopPickerService
{
    string? PickRootDirectory(string? initialDirectory);

    string? PickDatabasePath(string? currentPath);

    string? PickScanLogPath(string suggestedFileName);
}

public sealed class DesktopPickerService : IDesktopPickerService
{
    public string? PickRootDirectory(string? initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "검색할 디스크 또는 폴더 선택",
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(System.Windows.Application.Current.MainWindow) == true
            ? dialog.FolderName
            : null;
    }

    public string? PickDatabasePath(string? currentPath)
    {
        var dialog = new SaveFileDialog
        {
            Title = "FindEverything 인덱스 데이터베이스 선택",
            Filter = "SQLite 데이터베이스 (*.db)|*.db|모든 파일 (*.*)|*.*",
            AddExtension = true,
            DefaultExt = ".db",
            OverwritePrompt = false,
            CreatePrompt = false,
            FileName = string.IsNullOrWhiteSpace(currentPath)
                ? "findeverything.db"
                : Path.GetFileName(currentPath),
        };

        var directory = string.IsNullOrWhiteSpace(currentPath)
            ? null
            : Path.GetDirectoryName(currentPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
        }

        return dialog.ShowDialog(System.Windows.Application.Current.MainWindow) == true
            ? dialog.FileName
            : null;
    }

    public string? PickScanLogPath(string suggestedFileName)
    {
        var fileName = string.IsNullOrWhiteSpace(suggestedFileName)
            ? $"FindEverything-scan-{DateTime.Now:yyyyMMdd-HHmmss}.log"
            : Path.GetFileName(suggestedFileName.Trim());
        var dialog = new SaveFileDialog
        {
            Title = "탐색 로그 저장",
            Filter = "로그 파일 (*.log)|*.log|텍스트 파일 (*.txt)|*.txt|모든 파일 (*.*)|*.*",
            AddExtension = true,
            DefaultExt = ".log",
            OverwritePrompt = true,
            CreatePrompt = false,
            FileName = fileName,
        };

        return dialog.ShowDialog(System.Windows.Application.Current.MainWindow) == true
            ? dialog.FileName
            : null;
    }
}
