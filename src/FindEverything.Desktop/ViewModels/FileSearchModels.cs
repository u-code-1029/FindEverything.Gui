using System.Globalization;
using FindEverything.Application.Indexing;
using FindEverything.Desktop.Localization;

namespace FindEverything.Desktop.ViewModels;

public sealed class FileSearchItemViewModel(
    IndexedPathEntry entry,
    IAppLocalizer? localizer = null)
{
    public IndexedPathEntry Entry { get; } = entry;

    public string FullPath => Entry.FullPath;

    public string Name => Entry.Name;

    public string ParentPath => Entry.ParentPath;

    public string FolderPath => Kind == IndexedPathKind.Directory ? FullPath : ParentPath;

    public IndexedPathKind Kind => Entry.Kind;

    public string KindText => Kind == IndexedPathKind.Directory
        ? Get("Loc.Files.Kind.Folder", "폴더")
        : string.IsNullOrWhiteSpace(Path.GetExtension(Name))
            ? Get("Loc.Files.Kind.File", "파일")
            : string.Format(
                CultureInfo.CurrentCulture,
                Get("Loc.Files.Kind.ExtensionFile", "{0} 파일"),
                Path.GetExtension(Name).TrimStart('.').ToUpper(CultureInfo.CurrentCulture));

    public long? SizeBytes => Entry.SizeBytes;

    public string SizeText => Entry.SizeBytes is { } bytes
        ? FileSizeFormatter.Format(bytes)
        : "—";

    public DateTime CreatedLocal => Entry.CreatedUtc.LocalDateTime;

    public DateTime ModifiedLocal => Entry.ModifiedUtc.LocalDateTime;

    public bool CoveragePending => Entry.CoveragePending;

    public string StatusText => CoveragePending
        ? Get("Loc.Files.Status.Pending", "확인 대기")
        : string.Empty;

    private string Get(string key, string koreanFallback) =>
        localizer?.Get(key, koreanFallback) ?? koreanFallback;
}

public sealed record EntryKindChoice(IndexedPathKind? Value, string DisplayName);

public sealed record EntrySortChoice(EntrySortField Value, string DisplayName);

public enum DateFilterPreset
{
    Any,
    Today,
    LastSevenDays,
    LastThirtyDays,
    Custom,
}

public sealed record DateFilterChoice(DateFilterPreset Value, string DisplayName);

public static class FileSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "—";
        }

        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes:N0} {Units[unit]}"
            : $"{value:N1} {Units[unit]}";
    }
}
