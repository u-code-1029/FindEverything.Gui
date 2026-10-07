namespace FindEverything.Profile.Runtime;

/// <summary>
/// Converts a profile path into the fully qualified form that profile rules receive.
/// Platform-specific applications can replace the default implementation to provide
/// a stronger canonical form, such as expanding mapped drives to UNC paths.
/// </summary>
public interface IProfilePathCanonicalizer
{
    string Canonicalize(string path);
}

public sealed class AbsoluteProfilePathCanonicalizer : IProfilePathCanonicalizer
{
    public string Canonicalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "드라이브 또는 UNC 루트를 포함한 전체 폴더 경로를 입력하세요.",
                nameof(path));
        }

        RejectWindowsDeviceNamespace(path);
        var absolutePath = Path.GetFullPath(path);
        RejectWindowsDeviceNamespace(absolutePath);
        return Path.TrimEndingDirectorySeparator(absolutePath);
    }

    private static void RejectWindowsDeviceNamespace(string path)
    {
        if (OperatingSystem.IsWindows()
            && (path.StartsWith(@"\\?\", StringComparison.Ordinal)
                || path.StartsWith(@"\\.\", StringComparison.Ordinal)
                || path.StartsWith(@"\??\", StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Windows 장치 네임스페이스 대신 일반 드라이브 또는 UNC 경로를 입력하세요.",
                nameof(path));
        }
    }
}
