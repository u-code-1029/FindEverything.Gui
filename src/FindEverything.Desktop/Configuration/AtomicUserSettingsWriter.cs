using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using FindEverything.Application.Options;

namespace FindEverything.Desktop.Configuration;

public sealed class AtomicUserSettingsWriter(
    AppPaths paths,
    IValidatedSettingsUpdater<WorkspaceOptions> workspaceUpdater,
    IValidatedSettingsUpdater<IndexingOptions> indexingUpdater,
    IValidatedSettingsUpdater<AppearanceOptions> appearanceUpdater) : IUserSettingsWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task SaveAsync(
        UserSettingsUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        Validate(update);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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

        Directory.CreateDirectory(paths.LocalDataDirectory);
        var temporaryPath = Path.Combine(
            paths.LocalDataDirectory,
            $"appsettings.user.{Guid.NewGuid():N}.tmp");

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
                var document = await ReadExistingDocumentAsync(cancellationToken).ConfigureAwait(false);
                ReplaceSection(document, WorkspaceOptions.SectionName, update.Workspace);
                ReplaceSection(document, IndexingOptions.SectionName, update.Indexing);
                ReplaceSection(document, AppearanceOptions.SectionName, update.Appearance);
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(paths.UserSettingsFile))
            {
                try
                {
                    File.Replace(temporaryPath, paths.UserSettingsFile, null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(temporaryPath, paths.UserSettingsFile, overwrite: true);
                }
            }
            else
            {
                File.Move(temporaryPath, paths.UserSettingsFile);
            }

            workspaceUpdater.Publish(update.Workspace);
            indexingUpdater.Publish(update.Indexing);
            appearanceUpdater.Publish(update.Appearance);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<JsonObject> ReadExistingDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.UserSettingsFile))
        {
            return new JsonObject();
        }

        await using var stream = new FileStream(
            paths.UserSettingsFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var node = await JsonNode.ParseAsync(
            stream,
            documentOptions: default,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return node as JsonObject
            ?? throw new JsonException("사용자 설정 파일의 최상위 값은 JSON 객체여야 합니다.");
    }

    private static void ReplaceSection<T>(JsonObject document, string sectionName, T value)
    {
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
        Validator.ValidateObject(
            update.Workspace,
            new ValidationContext(update.Workspace),
            validateAllProperties: true);
        Validator.ValidateObject(
            update.Indexing,
            new ValidationContext(update.Indexing),
            validateAllProperties: true);
        Validator.ValidateObject(
            update.Appearance,
            new ValidationContext(update.Appearance),
            validateAllProperties: true);

        if (!Enum.IsDefined(update.Appearance.Theme))
        {
            throw new ValidationException("지원되지 않는 테마 설정입니다.");
        }

        if (!Enum.IsDefined(update.Appearance.Backdrop))
        {
            throw new ValidationException("지원되지 않는 배경 효과 설정입니다.");
        }
    }
}
