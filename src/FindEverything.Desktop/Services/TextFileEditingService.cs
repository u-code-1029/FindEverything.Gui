using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.Services;

public sealed record TextFileEditDocument(string DirectoryPath, string FilePath, string Content,
    bool IsCreation, ProfileTextFileFieldDescriptor Field, Encoding Encoding, bool HasBom, string NewLine,
    string? Version)
{
    public long DirectoryCreationTicks { get; init; }
    public string? DirectoryIdentity { get; init; }
}
public sealed record TextFileWriteResult(string Content, ProfileFieldResourceInfo Resource);

public interface ITextFileEditingService
{
    Task<TextFileEditDocument> OpenAsync(string directory, ProfileTextFileFieldDescriptor field,
        ProfileFieldResourceInfo resource, IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default);
    Task<TextFileWriteResult> SaveAsync(TextFileEditDocument document, string content,
        CancellationToken cancellationToken = default);
}

public sealed class TextFileEditingService : ITextFileEditingService
{
    private static readonly Regex TokenPattern = new(@"\{(?<token>[^{}]+)\}", RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public Task<TextFileEditDocument> OpenAsync(string directory, ProfileTextFileFieldDescriptor field,
        ProfileFieldResourceInfo resource, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
        Task.Run(() => Open(directory, field, resource, values, cancellationToken), CancellationToken.None).WaitAsync(cancellationToken);

    public Task<TextFileWriteResult> SaveAsync(TextFileEditDocument document, string content, CancellationToken cancellationToken = default) =>
        // Once the atomic commit begins, report its actual outcome even if the
        // page is closed. Cancelling only the await could hide a successful write.
        Task.Run(() => Save(document, content, cancellationToken), CancellationToken.None);

    public static string FormatCreationName(string template, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        var result = TokenPattern.Replace(template, match =>
        {
            var token = match.Groups["token"].Value;
            if (token.Equals("RandomGuid", StringComparison.Ordinal)) return Guid.NewGuid().ToString("N");
            if (!token.StartsWith("Field:", StringComparison.Ordinal)) throw new InvalidDataException("Unsupported file-name token.");
            var parts = token[6..].Split(':', 2);
            if (!values.TryGetValue(parts[0], out var value) || value is null)
                throw new InvalidDataException($"The file-name field '{parts[0]}' has no value.");
            return value is IFormattable formattable
                ? formattable.ToString(parts.Length == 2 ? parts[1] : null, CultureInfo.InvariantCulture) ?? string.Empty
                : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        });
        if (result.Contains('{') || result.Contains('}')) throw new InvalidDataException("Invalid file-name template.");
        ValidateLeaf(result);
        return result;
    }

    private static TextFileEditDocument Open(string directory, ProfileTextFileFieldDescriptor field,
        ProfileFieldResourceInfo resource, IReadOnlyDictionary<string, object?> values, CancellationToken token)
    {
        if (field.EditOptions?.Enabled != true) throw new InvalidOperationException("File editing is disabled in this profile.");
        if (resource.State is not ProfileFieldResourceState.Present and not ProfileFieldResourceState.Missing)
            throw new InvalidOperationException("Refresh this column before editing an unconfirmed file.");
        var creation = resource.State == ProfileFieldResourceState.Missing;
        var leaf = creation ? FormatCreationName(field.EditOptions.CreationFileNameTemplate ?? string.Empty, values)
            : resource.FileName ?? throw new InvalidDataException("The indexed file name is unavailable. Refresh this column.");
        ValidateLeaf(leaf);
        ValidatePattern(leaf, field);
        var parent = Path.GetFullPath(directory);
        var target = Path.Combine(parent, leaf);
        using var directoryLease = AcquireDirectoryLease(parent);
        VerifySafePath(parent, target, mustExist: !creation);
        VerifySelectedFile(parent, field, creation ? null : leaf, ownedTemporaryPath: null, token);
        token.ThrowIfCancellationRequested();
        if (creation)
        {
            if (File.Exists(target) || Directory.Exists(target)) throw new IOException("A file or folder with this name already exists.");
            return new(parent, target, string.Empty, true, field, new UTF8Encoding(false, true), false,
                Environment.NewLine, null) { DirectoryCreationTicks = Directory.GetCreationTimeUtc(parent).Ticks,
                    DirectoryIdentity = directoryLease?.Identity };
        }
        var bytes = ReadBytes(target, field.MaxBytes, token);
        var (encoding, bomLength) = DetectEncoding(bytes);
        var content = encoding.GetString(bytes.AsSpan(bomLength));
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
            : content.Contains('\n') ? "\n" : content.Contains('\r') ? "\r" : Environment.NewLine;
        return new(parent, target, content, false, field, encoding, bomLength != 0, newline, Hash(bytes))
        { DirectoryCreationTicks = Directory.GetCreationTimeUtc(parent).Ticks, DirectoryIdentity = directoryLease?.Identity };
    }

    private static TextFileWriteResult Save(TextFileEditDocument document, string content, CancellationToken token)
    {
        using var directoryLease = AcquireDirectoryLease(document.DirectoryPath);
        if (document.DirectoryIdentity is not null && directoryLease?.Identity != document.DirectoryIdentity)
            throw new IOException("The parent folder was replaced. Close and reopen this editor.");
        VerifySafePath(document.DirectoryPath, document.FilePath, mustExist: !document.IsCreation);
        if (Directory.GetCreationTimeUtc(document.DirectoryPath).Ticks != document.DirectoryCreationTicks)
            throw new IOException("The parent folder was replaced. Close and reopen this editor.");
        ValidatePattern(Path.GetFileName(document.FilePath), document.Field);
        VerifySelectedFile(document.DirectoryPath, document.Field, document.IsCreation ? null : Path.GetFileName(document.FilePath),
            ownedTemporaryPath: null, token);
        var normalized = NormalizeNewLines(content, document.NewLine);
        var payload = document.Encoding.GetBytes(normalized);
        var preamble = document.HasBom ? document.Encoding.GetPreamble() : [];
        if ((long)payload.Length + preamble.Length > document.Field.MaxBytes)
            throw new InvalidDataException($"The file exceeds the profile limit ({document.Field.MaxBytes:N0} bytes).");
        var bytes = new byte[payload.Length + preamble.Length];
        preamble.CopyTo(bytes, 0); payload.CopyTo(bytes, preamble.Length);
        token.ThrowIfCancellationRequested();
        var temporary = Path.Combine(document.DirectoryPath, ".fe-edit-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            token.ThrowIfCancellationRequested();
            VerifySafePath(document.DirectoryPath, document.FilePath, mustExist: !document.IsCreation);
            if (Directory.GetCreationTimeUtc(document.DirectoryPath).Ticks != document.DirectoryCreationTicks)
                throw new IOException("The parent folder was replaced. Close and reopen this editor.");
            if (document.IsCreation)
            {
                VerifySelectedFile(document.DirectoryPath, document.Field, expectedLeaf: null, temporary, token);
                // Same-folder atomic rename also prevents leaving a partial
                // created file if serialization or disk flushing fails.
                File.Move(temporary, document.FilePath, overwrite: false);
            }
            else
            {
                // Permit the atomic replacement's delete access, but deny new
                // content writers from the final version check through commit.
                using var targetReadLease = new FileStream(document.FilePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Delete);
                if (!string.Equals(Hash(ReadBytes(document.FilePath, document.Field.MaxBytes, token)), document.Version, StringComparison.Ordinal))
                    throw new IOException("The file changed outside this editor. Close and reopen it before saving.");
                VerifySelectedFile(document.DirectoryPath, document.Field, Path.GetFileName(document.FilePath), temporary, token);
                File.Replace(temporary, document.FilePath, null, ignoreMetadataErrors: false);
            }
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
        return new(normalized, new(ProfileFieldResourceState.Present, Path.GetFileName(document.FilePath),
            document.Encoding.WebName, document.HasBom, document.NewLine, Hash(bytes)));
    }

    private static byte[] ReadBytes(string path, long maximum, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum || stream.Length > int.MaxValue) throw new InvalidDataException("The file exceeds the profile read limit.");
        using var output = new MemoryStream((int)stream.Length);
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            token.ThrowIfCancellationRequested();
            if (output.Length + read > maximum) throw new InvalidDataException("The file exceeds the profile read limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static (Encoding Encoding, int Preamble) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) return (new UTF32Encoding(true, true, true), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) return (new UTF32Encoding(false, true, true), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return (new UTF8Encoding(true, true), 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return (new UnicodeEncoding(true, true, true), 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return (new UnicodeEncoding(false, true, true), 2);
        return (new UTF8Encoding(false, true), 0);
    }

    private static string NormalizeNewLines(string value, string newline) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", newline, StringComparison.Ordinal);
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    internal static void ValidateLeaf(string leaf)
    {
        if (string.IsNullOrWhiteSpace(leaf) || leaf is "." or ".." || leaf.Length > 255
            || leaf.EndsWith(' ') || leaf.EndsWith('.') || leaf.IndexOfAny(['/', '\\', ':', '<', '>', '"', '|', '?', '*']) >= 0
            || leaf.Any(char.IsControl) || leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("The generated name must be one valid file name, not a path.");
        var stem = leaf.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9')
            throw new InvalidDataException("Reserved Windows file names are not allowed.");
    }

    private static void ValidatePattern(string leaf, ProfileTextFileFieldDescriptor field)
    {
        if (!CreateNameRegex(field).IsMatch(leaf))
            throw new InvalidDataException("The file name does not match the profile's read pattern.");
    }

    private static Regex CreateNameRegex(ProfileTextFileFieldDescriptor field)
    {
        var pattern = field.MatchMode == ProfileRegexMatchMode.Full ? $"\\A(?:{field.FileNamePattern})\\z" : field.FileNamePattern;
        return new Regex(pattern, RegexOptions.CultureInvariant
            | (field.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromMilliseconds(field.TimeoutMilliseconds));
    }

    private static void VerifySelectedFile(string directory, ProfileTextFileFieldDescriptor field,
        string? expectedLeaf, string? ownedTemporaryPath, CancellationToken token)
    {
        var selected = FindSelectedLeaf(directory, field, ownedTemporaryPath, token);
        if (expectedLeaf is null ? selected is not null : !string.Equals(selected, expectedLeaf, StringComparison.Ordinal))
            throw new IOException(expectedLeaf is null
                ? "A matching file already exists. Refresh this column before creating a file."
                : "The file selected by the profile changed. Refresh this column before editing.");
    }

    /// <summary>Same deterministic selection as the runtime, with O(1) retained filenames.</summary>
    private static string? FindSelectedLeaf(string directory, ProfileTextFileFieldDescriptor field,
        string? ownedTemporaryPath, CancellationToken token)
    {
        var regex = CreateNameRegex(field);
        string? first = null;
        using var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).GetEnumerator();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!files.MoveNext()) return first;
            // Ignore only this operation's stage, not arbitrary .fe-edit names
            // that a broad user pattern may legitimately select.
            if (string.Equals(files.Current, ownedTemporaryPath, StringComparison.Ordinal)) continue;
            var leaf = Path.GetFileName(files.Current);
            if (!regex.IsMatch(leaf)) continue;
            if (first is null || CompareLeaf(leaf, first) < 0) first = leaf;
        }
    }
    private static int CompareLeaf(string left, string right)
    {
        var ignoreCase = StringComparer.OrdinalIgnoreCase.Compare(left, right);
        return ignoreCase != 0 ? ignoreCase : StringComparer.Ordinal.Compare(left, right);
    }

    private static void VerifySafePath(string directory, string target, bool mustExist)
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(target))!), parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The target must be directly inside the selected folder.");
        DirectoryInfo? current = new DirectoryInfo(parent);
        while (current is not null)
        {
            if (!current.Exists) throw new DirectoryNotFoundException(current.FullName);
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Writing through a symbolic link or junction is not allowed.");
            current = current.Parent;
        }
        if (Directory.Exists(target)) throw new IOException("The target is a directory.");
        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Writing to a symbolic link is not allowed.");
        if (mustExist && !File.Exists(target)) throw new FileNotFoundException("The file no longer exists.", target);
    }

    /// <summary>
    /// Windows directory handles deny rename/delete for each ancestor during the
    /// write. This closes the check-then-follow junction race without preventing
    /// normal child file creation or replacement. Stable file IDs catch retargeting
    /// between opening the editor and Save, even if timestamps were copied.
    /// </summary>
    private sealed class DirectoryLease(List<SafeFileHandle> handles, string identity) : IDisposable
    {
        public string Identity { get; } = identity;
        public void Dispose() { for (var index = handles.Count - 1; index >= 0; index--) handles[index].Dispose(); }
    }

    private static DirectoryLease? AcquireDirectoryLease(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var ancestors = new Stack<string>();
        for (DirectoryInfo? item = new DirectoryInfo(Path.GetFullPath(path)); item is not null; item = item.Parent)
            ancestors.Push(item.FullName);
        var handles = new List<SafeFileHandle>(); string identity = string.Empty;
        try
        {
            foreach (var ancestor in ancestors)
            {
                var handle = CreateFileW(ancestor, 0x80, 0x1 | 0x2, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot protect the parent folder from path changes.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error())); }
                handles.Add(handle);
                if (!GetFileInformationByHandle(handle, out var information))
                    throw new IOException("Cannot verify the parent folder identity.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                if ((information.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Writing through a symbolic link or junction is not allowed.");
                identity = $"{information.VolumeSerial:X8}:{information.FileIndexHigh:X8}{information.FileIndexLow:X8}";
            }
            return new(handles, identity);
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerial, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out NativeFileInformation information);
}
