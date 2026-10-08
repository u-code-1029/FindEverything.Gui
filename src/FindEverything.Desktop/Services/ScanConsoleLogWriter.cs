using System.Text;
using FindEverything.Desktop.Localization;

namespace FindEverything.Desktop.Services;

public interface IScanConsoleLogWriter
{
    Task WriteAsync(
        string path,
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken = default);
}

public sealed class ScanConsoleLogWriter : IScanConsoleLogWriter
{
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: true,
        throwOnInvalidBytes: true);
    private readonly IAppLocalizer? _localizer;

    public ScanConsoleLogWriter(IAppLocalizer? localizer = null) =>
        _localizer = localizer;

    public async Task WriteAsync(
        string path,
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(lines);

        var destinationPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                _localizer?.Format(
                    "Loc.Scan.Save.DirectoryNotFound",
                    "로그를 저장할 폴더를 찾을 수 없습니다: {0}",
                    directory)
                ?? $"로그를 저장할 폴더를 찾을 수 없습니다: {directory}");
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await using (var writer = new StreamWriter(
                                 stream,
                                 Utf8WithBom,
                                 16 * 1024,
                                 leaveOpen: true)
                             {
                                 NewLine = "\r\n",
                             })
                {
                    foreach (var line in lines)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await writer.WriteLineAsync(line.AsMemory(), cancellationToken)
                            .ConfigureAwait(false);
                    }

                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destinationPath))
            {
                try
                {
                    File.Replace(temporaryPath, destinationPath, null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(temporaryPath, destinationPath, overwrite: true);
                }
            }
            else
            {
                File.Move(temporaryPath, destinationPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // Preserve the original write/replace failure. A best-effort
                    // cleanup failure must not hide the actionable export error.
                }
            }
        }
    }
}
