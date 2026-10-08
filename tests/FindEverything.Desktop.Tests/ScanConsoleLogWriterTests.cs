using System.IO;
using System.Text;
using FindEverything.Desktop.Services;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ScanConsoleLogWriterTests
{
    [Fact]
    public async Task Write_creates_a_new_utf8_bom_file_with_windows_line_endings()
    {
        using var directory = new TemporaryDirectory();
        var destinationPath = Path.Combine(directory.Path, "scan.log");
        var writer = new ScanConsoleLogWriter();

        await writer.WriteAsync(
            destinationPath,
            ["첫 번째 로그", @"경로 C:\자료\2026"]);

        var bytes = await File.ReadAllBytesAsync(destinationPath);
        var content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes("첫 번째 로그\r\n경로 C:\\자료\\2026\r\n");
        var expected = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
            .GetPreamble()
            .Concat(content)
            .ToArray();

        Assert.Equal(expected, bytes);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task Write_replaces_an_existing_file_after_the_new_content_is_complete()
    {
        using var directory = new TemporaryDirectory();
        var destinationPath = Path.Combine(directory.Path, "scan.log");
        await File.WriteAllTextAsync(destinationPath, "이전 내용");
        var writer = new ScanConsoleLogWriter();

        await writer.WriteAsync(destinationPath, ["새 로그"]);

        var text = await File.ReadAllTextAsync(destinationPath, Encoding.UTF8);
        Assert.Equal("새 로그\r\n", text);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task Cancellation_keeps_the_existing_file_and_removes_the_temporary_file()
    {
        using var directory = new TemporaryDirectory();
        var destinationPath = Path.Combine(directory.Path, "scan.log");
        await File.WriteAllTextAsync(destinationPath, "보존할 내용");
        var writer = new ScanConsoleLogWriter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => writer.WriteAsync(destinationPath, ["교체하면 안 되는 로그"], cancellation.Token));

        Assert.Equal("보존할 내용", await File.ReadAllTextAsync(destinationPath));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"findeverything-scan-log-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
