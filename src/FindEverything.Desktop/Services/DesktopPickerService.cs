using Microsoft.Win32;
using FindEverything.Desktop.Localization;

namespace FindEverything.Desktop.Services;

public interface IDesktopPickerService
{
    string? PickRootDirectory(string? initialDirectory);

    string? PickDatabasePath(string? currentPath);

    string? PickScanLogPath(string suggestedFileName);

    CatalogSelectionExportDestination? PickCatalogExportPath(string suggestedFileName);
}

public sealed class DesktopPickerService(IAppLocalizer localizer) : IDesktopPickerService
{
    public string? PickRootDirectory(string? initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = localizer.Get(
                "Loc.Picker.Root.Title",
                "검색할 디스크 또는 폴더 선택"),
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
            Title = localizer.Get(
                "Loc.Picker.Database.Title",
                "FindEverything 인덱스 데이터베이스 선택"),
            Filter = localizer.Get(
                "Loc.Picker.Database.Filter",
                "SQLite 데이터베이스 (*.db)|*.db|모든 파일 (*.*)|*.*"),
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
            Title = localizer.Get("Loc.Picker.ScanLog.Title", "탐색 로그 저장"),
            Filter = localizer.Get(
                "Loc.Picker.ScanLog.Filter",
                "로그 파일 (*.log)|*.log|텍스트 파일 (*.txt)|*.txt|모든 파일 (*.*)|*.*"),
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

    public CatalogSelectionExportDestination? PickCatalogExportPath(string suggestedFileName)
    {
        var fileName = CreateCatalogExportSuggestion(suggestedFileName, DateTime.Now);
        var dialog = new SaveFileDialog
        {
            Title = localizer.Get(
                "Loc.Picker.CatalogExport.Title",
                "선택한 구조화 결과 내보내기"),
            Filter = localizer.Get(
                "Loc.Picker.CatalogExport.Filter",
                "Excel 통합 문서 (*.xlsx)|*.xlsx|CSV 파일 (*.csv)|*.csv"),
            AddExtension = true,
            FilterIndex = 1,
            OverwritePrompt = true,
            CreatePrompt = false,
            FileName = fileName,
        };

        if (dialog.ShowDialog(System.Windows.Application.Current.MainWindow) != true)
        {
            return null;
        }

        var extension = Path.GetExtension(dialog.FileName);
        var format = extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
            ? CatalogSelectionExportFormat.Csv
            : extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                ? CatalogSelectionExportFormat.Xlsx
                : dialog.FilterIndex == 2
                    ? CatalogSelectionExportFormat.Csv
                    : CatalogSelectionExportFormat.Xlsx;
        return new CatalogSelectionExportDestination(dialog.FileName, format);
    }

    internal static string CreateCatalogExportSuggestion(
        string? suggestedFileName,
        DateTime timestamp)
    {
        var fileName = string.IsNullOrWhiteSpace(suggestedFileName)
            ? $"FindEverything-catalog-{timestamp:yyyyMMdd-HHmmss}"
            : Path.GetFileName(suggestedFileName.Trim());

        // Leave the suggestion extensionless. SaveFileDialog then appends the
        // extension from the filter the user actually selected; a prefilled
        // '.xlsx' (or DefaultExt) otherwise makes switching to CSV look as if it
        // succeeded while still returning an Excel destination.
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(fileName)
                : fileName;
    }
}
