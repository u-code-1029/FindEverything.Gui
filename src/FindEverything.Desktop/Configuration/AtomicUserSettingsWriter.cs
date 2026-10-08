using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using FindEverything.Application.Options;

namespace FindEverything.Desktop.Configuration;

public sealed class AtomicUserSettingsWriter(
    AppPaths paths,
    IValidatedSettingsUpdater<WorkspaceOptions> workspaceUpdater,
    IValidatedSettingsUpdater<IndexingOptions> indexingUpdater,
    IValidatedSettingsUpdater<AppearanceOptions> appearanceUpdater,
    IValidatedSettingsUpdater<LocalizationOptions> localizationUpdater) : IUserSettingsWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly IAtomicUserSettingsCommitHook _commitHook =
        NoOpAtomicUserSettingsCommitHook.Instance;
    private const int LockRetryDelayMilliseconds = 25;
    private const int LockRetryCount = 200;

    internal AtomicUserSettingsWriter(
        AppPaths paths,
        IValidatedSettingsUpdater<WorkspaceOptions> workspaceUpdater,
        IValidatedSettingsUpdater<IndexingOptions> indexingUpdater,
        IValidatedSettingsUpdater<AppearanceOptions> appearanceUpdater,
        IValidatedSettingsUpdater<LocalizationOptions> localizationUpdater,
        IAtomicUserSettingsCommitHook commitHook)
        : this(
            paths,
            workspaceUpdater,
            indexingUpdater,
            appearanceUpdater,
            localizationUpdater)
    {
        _commitHook = commitHook ?? throw new ArgumentNullException(nameof(commitHook));
    }

    public async Task SaveAsync(
        UserSettingsUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        Validate(update);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(paths.LocalDataDirectory);
            await using var interprocessLock = await AcquireInterprocessLockAsync(
                cancellationToken).ConfigureAwait(false);
            await SaveCoreAsync(update, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task SaveCoreAsync(
        UserSettingsUpdate update,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(
            paths.LocalDataDirectory,
            $"appsettings.user.{Guid.NewGuid():N}.tmp");

        try
        {
            ExistingDocument existing;
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                existing = await ReadExistingDocumentAsync(cancellationToken).ConfigureAwait(false);
                ReplaceSectionIfPresent(existing.Document, WorkspaceOptions.SectionName, update.Workspace);
                ReplaceSectionIfPresent(existing.Document, IndexingOptions.SectionName, update.Indexing);
                ReplaceSectionIfPresent(existing.Document, AppearanceOptions.SectionName, update.Appearance);
                ReplaceSectionIfPresent(existing.Document, LocalizationOptions.SectionName, update.Localization);
                await JsonSerializer.SerializeAsync(
                    stream,
                    existing.Document,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

                if (!await HasSameRevisionAsync(
                        existing.Fingerprint,
                        cancellationToken).ConfigureAwait(false))
                {
                    throw new IOException(
                        "The user settings file changed while it was being saved. Try again so the external changes are preserved.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            _commitHook.BeforeCommit(paths.UserSettingsFile);
            var writtenFingerprint = SHA256.HashData(
                await ReadFileBytesAsync(temporaryPath, cancellationToken).ConfigureAwait(false));
            await CommitAsync(
                temporaryPath,
                existing.Fingerprint,
                writtenFingerprint).ConfigureAwait(false);

            if (update.Workspace is { } workspace)
            {
                workspaceUpdater.Publish(workspace);
            }

            if (update.Indexing is { } indexing)
            {
                indexingUpdater.Publish(indexing);
            }

            if (update.Appearance is { } appearance)
            {
                appearanceUpdater.Publish(appearance);
            }

            if (update.Localization is { } localization)
            {
                localizationUpdater.Publish(localization);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task CommitAsync(
        string temporaryPath,
        byte[]? expectedFingerprint,
        byte[] writtenFingerprint)
    {
        if (!File.Exists(paths.UserSettingsFile))
        {
            // File.Move without overwrite is a conservative create-if-absent. If a
            // non-cooperating editor creates the file now, Move fails instead of
            // replacing that external content.
            if (expectedFingerprint is not null)
            {
                throw CreateRevisionConflictException();
            }

            File.Move(temporaryPath, paths.UserSettingsFile);
            return;
        }

        var backupPath = Path.Combine(
            paths.LocalDataDirectory,
            $"appsettings.user.{Guid.NewGuid():N}.previous.tmp");
        try
        {
            // File.Replace atomically captures the exact revision displaced at
            // the commit point. Comparing the backup closes the gap between the
            // earlier SHA check and the replacement itself.
            File.Replace(temporaryPath, paths.UserSettingsFile, backupPath);
        }
        catch (PlatformNotSupportedException exception)
        {
            // An overwrite fallback would silently reintroduce the TOCTOU bug.
            throw new IOException(
                "Atomic replacement of the user settings file is unavailable on this platform.",
                exception);
        }

        byte[] displacedFingerprint;
        try
        {
            displacedFingerprint = SHA256.HashData(
                await ReadFileBytesAsync(
                    backupPath,
                    CancellationToken.None).ConfigureAwait(false));
        }
        catch (Exception verificationException)
        {
            try
            {
                await RestoreExternalRevisionAsync(
                    backupPath,
                    writtenFingerprint).ConfigureAwait(false);
            }
            catch (Exception restoreException)
            {
                throw new AggregateException(
                    "The displaced user settings revision could not be verified or restored. Its backup was preserved.",
                    verificationException,
                    restoreException);
            }

            throw new IOException(
                "The displaced user settings revision could not be verified, so the previous file was restored.",
                verificationException);
        }

        if (expectedFingerprint is not null
            && CryptographicOperations.FixedTimeEquals(
                expectedFingerprint,
                displacedFingerprint))
        {
            TryDeleteBackup(backupPath);
            return;
        }

        await RestoreExternalRevisionAsync(
            backupPath,
            writtenFingerprint).ConfigureAwait(false);
        throw CreateRevisionConflictException();
    }

    private async Task RestoreExternalRevisionAsync(
        string backupPath,
        byte[] writtenFingerprint)
    {
        if (!File.Exists(paths.UserSettingsFile))
        {
            File.Move(backupPath, paths.UserSettingsFile);
            return;
        }

        var activeFingerprint = SHA256.HashData(
            await ReadFileBytesAsync(
                paths.UserSettingsFile,
                CancellationToken.None).ConfigureAwait(false));
        if (!CryptographicOperations.FixedTimeEquals(
                activeFingerprint,
                writtenFingerprint))
        {
            // Another editor already replaced our rejected output. Keep its current
            // file active and retain the displaced revision as a conflict artifact.
            File.Move(backupPath, CreateConflictPath());
            return;
        }

        var rejectedPath = Path.Combine(
            paths.LocalDataDirectory,
            $"appsettings.user.{Guid.NewGuid():N}.rejected.tmp");
        File.Replace(backupPath, paths.UserSettingsFile, rejectedPath);

        // Normally this is exactly our rejected output. If a second external save
        // raced the rollback, retain that revision instead of deleting user data.
        var rejectedFingerprint = SHA256.HashData(
            await ReadFileBytesAsync(rejectedPath, CancellationToken.None).ConfigureAwait(false));
        if (CryptographicOperations.FixedTimeEquals(
                rejectedFingerprint,
                writtenFingerprint))
        {
            File.Delete(rejectedPath);
        }
        else
        {
            File.Move(rejectedPath, CreateConflictPath());
        }
    }

    private string CreateConflictPath() => Path.Combine(
        paths.LocalDataDirectory,
        $"appsettings.user.conflict.{DateTime.UtcNow:yyyyMMddHHmmssfffffff}.{Guid.NewGuid():N}.json");

    private static void TryDeleteBackup(string backupPath)
    {
        try
        {
            File.Delete(backupPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static IOException CreateRevisionConflictException() => new(
        "The user settings file changed while it was being saved. Try again so the external changes are preserved.");

    private async Task<ExistingDocument> ReadExistingDocumentAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.UserSettingsFile))
        {
            return new ExistingDocument(new JsonObject(), null);
        }

        var bytes = await ReadFileBytesAsync(
            paths.UserSettingsFile,
            cancellationToken).ConfigureAwait(false);
        var node = JsonNode.Parse(bytes);
        var document = node as JsonObject
            ?? throw new JsonException("사용자 설정 파일의 최상위 값은 JSON 객체여야 합니다.");
        return new ExistingDocument(document, SHA256.HashData(bytes));
    }

    private async Task<bool> HasSameRevisionAsync(
        byte[]? expectedFingerprint,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.UserSettingsFile))
        {
            return expectedFingerprint is null;
        }

        if (expectedFingerprint is null)
        {
            return false;
        }

        try
        {
            var bytes = await ReadFileBytesAsync(
                paths.UserSettingsFile,
                cancellationToken).ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(
                expectedFingerprint,
                SHA256.HashData(bytes));
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private async Task<FileStream> AcquireInterprocessLockAsync(
        CancellationToken cancellationToken)
    {
        var lockPath = paths.UserSettingsFile + ".lock";
        IOException? lastException = null;
        for (var attempt = 0; attempt < LockRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException exception)
            {
                lastException = exception;
                await Task.Delay(
                    LockRetryDelayMilliseconds,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException(
            "Timed out waiting for another FindEverything instance to finish saving user settings.",
            lastException);
    }

    private static async Task<byte[]> ReadFileBytesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static void ReplaceSectionIfPresent<T>(
        JsonObject document,
        string sectionName,
        T? value)
        where T : class
    {
        if (value is null)
        {
            return;
        }

        var existingNames = document
            .Select(static property => property.Key)
            .Where(name => string.Equals(name, sectionName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var existingName in existingNames)
        {
            document.Remove(existingName);
        }

        document[sectionName] = JsonSerializer.SerializeToNode(value, SerializerOptions);
    }

    private static void Validate(UserSettingsUpdate update)
    {
        if (update.Workspace is null
            && update.Indexing is null
            && update.Appearance is null
            && update.Localization is null)
        {
            throw new ValidationException("At least one settings section must be provided.");
        }

        ValidateIfPresent(update.Workspace);
        ValidateIfPresent(update.Indexing);
        ValidateIfPresent(update.Appearance);
        ValidateIfPresent(update.Localization);

        if (update.Appearance is { } appearance && !Enum.IsDefined(appearance.Theme))
        {
            throw new ValidationException("지원되지 않는 테마 설정입니다.");
        }

        if (update.Appearance is { } backdropAppearance
            && !Enum.IsDefined(backdropAppearance.Backdrop))
        {
            throw new ValidationException("지원되지 않는 배경 효과 설정입니다.");
        }

        if (update.Localization is not { } localization)
        {
            return;
        }

        if (!LocalizationOptions.IsSupported(localization.CultureName))
        {
            throw new ValidationException("지원되는 언어는 ko-KR과 en-US입니다.");
        }

        localization.CultureName = LocalizationOptions.Normalize(localization.CultureName);
    }

    private static void ValidateIfPresent<T>(T? value)
        where T : class
    {
        if (value is null)
        {
            return;
        }

        Validator.ValidateObject(
            value,
            new ValidationContext(value),
            validateAllProperties: true);
    }

    private sealed record ExistingDocument(
        JsonObject Document,
        byte[]? Fingerprint);
}

internal interface IAtomicUserSettingsCommitHook
{
    void BeforeCommit(string userSettingsFile);
}

internal sealed class NoOpAtomicUserSettingsCommitHook :
    IAtomicUserSettingsCommitHook
{
    public static NoOpAtomicUserSettingsCommitHook Instance { get; } = new();

    public void BeforeCommit(string userSettingsFile)
    {
    }
}
