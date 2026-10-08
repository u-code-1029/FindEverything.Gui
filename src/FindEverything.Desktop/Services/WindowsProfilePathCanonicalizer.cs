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

internal sealed class MappedDrivePathResolutionException : IOException
{
    public MappedDrivePathResolutionException(
        string mappedPath,
        uint? providerErrorCode = null,
        string? providerDetail = null)
        : base(CreateDiagnosticMessage(mappedPath, providerErrorCode, providerDetail))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mappedPath);
        MappedPath = mappedPath;
        ProviderErrorCode = providerErrorCode;
        ProviderDetail = providerDetail;
    }

    public string MappedPath { get; }

    public uint? ProviderErrorCode { get; }

    public string? ProviderDetail { get; }

    private static string CreateDiagnosticMessage(
        string mappedPath,
        uint? providerErrorCode,
        string? providerDetail)
    {
        var providerSuffix = providerErrorCode is null
            ? string.Empty
            : $" Windows error {providerErrorCode}: {providerDetail ?? "No provider detail was returned."}";
        return $"Could not resolve mapped network path '{mappedPath}' to a UNC path.{providerSuffix}";
    }
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
                        throw new MappedDrivePathResolutionException(absolutePath);
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

    private static MappedDrivePathResolutionException CreateResolutionException(
        string path,
        uint errorCode)
    {
        if (errorCode == NoError)
        {
            return new MappedDrivePathResolutionException(path);
        }

        var detail = new Win32Exception(checked((int)errorCode)).Message;
        return new MappedDrivePathResolutionException(path, errorCode, detail);
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
