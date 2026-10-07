using System.ComponentModel;
using System.Runtime.InteropServices;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.Services;

internal sealed class WindowsProfilePathCanonicalizer(
    AbsoluteProfilePathCanonicalizer absolutePathCanonicalizer,
    IWindowsMappedDrivePathResolver mappedDrivePathResolver) : IProfilePathCanonicalizer
{
    public string Canonicalize(string path)
    {
        var absolutePath = absolutePathCanonicalizer.Canonicalize(path);
        return mappedDrivePathResolver.ExpandToUnc(absolutePath);
    }
}

internal interface IWindowsMappedDrivePathResolver
{
    string ExpandToUnc(string absolutePath);
}

internal sealed partial class WindowsMappedDrivePathResolver : IWindowsMappedDrivePathResolver
{
    private const uint UniversalNameInfoLevel = 1;
    private const uint NoError = 0;
    private const uint ErrorMoreData = 234;
    private const uint ErrorNotConnected = 2250;
    private const uint InitialUniversalNameBufferBytes = 1024;
    private const uint MaximumUniversalNameBufferBytes = 1024 * 1024;

    public string ExpandToUnc(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        // A UNC path is already independent of any per-user drive mapping. This
        // also preserves IPv4/FQDN hosts exactly as the user supplied them.
        if (absolutePath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return absolutePath;
        }

        var root = Path.GetPathRoot(absolutePath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return absolutePath;
        }

        var driveType = TryGetDriveType(root);
        if (driveType is DriveType.Fixed
            or DriveType.Removable
            or DriveType.CDRom
            or DriveType.Ram)
        {
            return absolutePath;
        }

        return ResolveUniversalName(absolutePath, driveType == DriveType.Network);
    }

    private static DriveType? TryGetDriveType(string root)
    {
        try
        {
            return new DriveInfo(root).DriveType;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Let WNet decide when DriveInfo cannot classify the root. Falling
            // back to the drive-letter path here could violate the UNC contract.
            return null;
        }
    }

    private static string ResolveUniversalName(string absolutePath, bool knownNetworkDrive)
    {
        var bufferSize = InitialUniversalNameBufferBytes;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var suppliedBufferSize = bufferSize;
            var buffer = Marshal.AllocHGlobal(checked((int)suppliedBufferSize));
            uint result;
            try
            {
                result = WNetGetUniversalName(
                    absolutePath,
                    UniversalNameInfoLevel,
                    buffer,
                    ref bufferSize);
                if (result == NoError)
                {
                    var universalNamePointer = Marshal.ReadIntPtr(buffer);
                    var universalName = Marshal.PtrToStringUni(universalNamePointer);
                    if (string.IsNullOrWhiteSpace(universalName)
                        || !universalName.StartsWith(@"\\", StringComparison.Ordinal))
                    {
                        throw new IOException(
                            $"매핑된 네트워크 경로 '{absolutePath}'의 UNC 경로를 확인할 수 없습니다.");
                    }

                    return Path.TrimEndingDirectorySeparator(Path.GetFullPath(universalName));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (result == ErrorNotConnected && !knownNetworkDrive)
            {
                return absolutePath;
            }

            if (result != ErrorMoreData
                || bufferSize <= suppliedBufferSize
                || bufferSize > MaximumUniversalNameBufferBytes)
            {
                throw CreateResolutionException(absolutePath, result);
            }
        }

        throw CreateResolutionException(absolutePath, ErrorMoreData);
    }

    private static IOException CreateResolutionException(string path, uint errorCode)
    {
        var detail = errorCode == NoError
            ? "UNC 경로 정보가 반환되지 않았습니다."
            : new Win32Exception(checked((int)errorCode)).Message;
        return new IOException(
            $"매핑된 네트워크 경로 '{path}'를 UNC 경로로 변환하지 못했습니다. "
            + $"연결을 다시 확인하거나 UNC 경로를 직접 입력하세요. "
            + $"Windows 오류 {errorCode}: {detail}");
    }

    [LibraryImport(
        "mpr.dll",
        EntryPoint = "WNetGetUniversalNameW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint WNetGetUniversalName(
        string localPath,
        uint infoLevel,
        IntPtr buffer,
        ref uint bufferSize);
}
