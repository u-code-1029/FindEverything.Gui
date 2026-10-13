namespace FindEverything.Desktop.Services;

public sealed record LiveDirectoryEntry(string Name, string FullPath, bool IsDirectory, bool IsReparsePoint);
public sealed record LiveDirectoryPage(IReadOnlyList<LiveDirectoryEntry> Entries, int NextOffset, bool HasMore);

/// <summary>Explicit, bounded filesystem reads; never uses or updates the index.</summary>
public interface ILiveDirectoryService
{
    Task<LiveDirectoryPage> ReadPageAsync(string path, int offset = 0, CancellationToken cancellationToken = default);
}

public sealed class LiveDirectoryService : ILiveDirectoryService
{
    public const int PageSize = 256;

    public Task<LiveDirectoryPage> ReadPageAsync(string path, int offset = 0, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        // Network filesystem enumeration can block in an OS call. The UI stops
        // awaiting immediately on cancellation; the worker has no UI references.
        return Task.Run(() => Read(path, offset, cancellationToken), CancellationToken.None).WaitAsync(cancellationToken);
    }

    private static LiveDirectoryPage Read(string path, int offset, CancellationToken token)
    {
        var entries = new List<LiveDirectoryEntry>(PageSize);
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false,
            ReturnSpecialDirectories = false, AttributesToSkip = 0 };
        using var enumerator = new DirectoryInfo(path).EnumerateFileSystemInfos("*", options).GetEnumerator();
        var visited = 0;
        while (enumerator.MoveNext())
        {
            token.ThrowIfCancellationRequested();
            if (visited++ < offset) continue;
            if (entries.Count == PageSize)
                return new(entries.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                    offset + entries.Count, true);
            var info = enumerator.Current;
            var attributes = info.Attributes;
            entries.Add(new(info.Name, info.FullName, (attributes & FileAttributes.Directory) != 0,
                (attributes & FileAttributes.ReparsePoint) != 0));
        }
        return new(entries.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            offset + entries.Count, false);
    }
}
