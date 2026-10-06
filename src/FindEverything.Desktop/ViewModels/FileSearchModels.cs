using System.Globalization;
using FindEverything.Application.Indexing;

namespace FindEverything.Desktop.ViewModels;

public sealed class FileSearchItemViewModel(IndexedPathEntry entry)
{
    public IndexedPathEntry Entry { get; } = entry;

    public string FullPath => Entry.FullPath;

    public string Name => Entry.Name;

    public string ParentPath => Entry.ParentPath;

    public IndexedPathKind Kind => Entry.Kind;

    public string KindText => Kind == IndexedPathKind.Directory
        ? "폴더"
        : string.IsNullOrWhiteSpace(Path.GetExtension(Name))
            ? "파일"
            : $"{Path.GetExtension(Name).TrimStart('.').ToUpper(CultureInfo.CurrentCulture)} 파일";

    public long? SizeBytes => Entry.SizeBytes;

    public string SizeText => Entry.SizeBytes is { } bytes
        ? FileSizeFormatter.Format(bytes)
        : "—";

    public DateTime CreatedLocal => Entry.CreatedUtc.LocalDateTime;

    public DateTime ModifiedLocal => Entry.ModifiedUtc.LocalDateTime;

    public bool CoveragePending => Entry.CoveragePending;

    public string StatusText => CoveragePending ? "확인 대기" : string.Empty;
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
