using System.IO;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class WindowsProfilePathCanonicalizerTests
{
    [Fact]
    public void Canonicalize_passes_a_fully_qualified_path_to_the_mapped_drive_resolver()
    {
        var resolver = new StubMappedDrivePathResolver(
            @"\\192.168.10.20\archive\Projects\Alpha");
        var canonicalizer = new WindowsProfilePathCanonicalizer(
            new AbsoluteProfilePathCanonicalizer(),
            resolver);

        var result = canonicalizer.Canonicalize(@"Z:\Projects\.\Alpha\");

        Assert.Equal(@"Z:\Projects\Alpha", resolver.ReceivedPath);
        Assert.Equal(@"\\192.168.10.20\archive\Projects\Alpha", result);
    }

    [Fact]
    public void Mapped_drive_resolver_preserves_an_ip_based_unc_path()
    {
        var resolver = new WindowsMappedDrivePathResolver();
        const string path = @"\\192.168.10.20\archive\Projects\Alpha";

        var result = resolver.ExpandToUnc(path);

        Assert.Equal(path, result);
    }

    [Fact]
    public void Mapped_drive_resolver_keeps_a_local_drive_path()
    {
        var resolver = new WindowsMappedDrivePathResolver();
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));

        var result = resolver.ExpandToUnc(path);

        Assert.Equal(path, result);
    }

    private sealed class StubMappedDrivePathResolver(string result)
        : IWindowsMappedDrivePathResolver
    {
        public string? ReceivedPath { get; private set; }

        public string ExpandToUnc(string absolutePath)
        {
            ReceivedPath = absolutePath;
            return result;
        }
    }
}
