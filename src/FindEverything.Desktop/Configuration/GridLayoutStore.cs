using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.Configuration;

public sealed record GridColumnLayout(string FieldId, int DisplayIndex, double Width);

public interface IGridLayoutStore
{
    IReadOnlyDictionary<string, GridColumnLayout> Get(string profileId);

    void Save(string profileId, IEnumerable<GridColumnLayout> columns);
}

public sealed class GridLayoutStore : IGridLayoutStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly AppPaths _paths;
    private readonly ILogger<GridLayoutStore> _logger;
    private Dictionary<string, Dictionary<string, GridColumnLayout>>? _layouts;

    public GridLayoutStore(AppPaths paths, ILogger<GridLayoutStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, GridColumnLayout> Get(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        lock (_sync)
        {
            EnsureLoaded();
            return _layouts!.TryGetValue(profileId, out var layout)
                ? new Dictionary<string, GridColumnLayout>(layout, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridColumnLayout>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save(string profileId, IEnumerable<GridColumnLayout> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(columns);
        lock (_sync)
        {
            EnsureLoaded();
            _layouts![profileId] = columns.ToDictionary(
                static column => column.FieldId,
                StringComparer.OrdinalIgnoreCase);
            WriteAtomically();
        }
    }

    private void EnsureLoaded()
    {
        if (_layouts is not null)
        {
            return;
        }

        try
        {
            if (!File.Exists(_paths.GridLayoutFile))
            {
                _layouts = new Dictionary<string, Dictionary<string, GridColumnLayout>>(
                    StringComparer.OrdinalIgnoreCase);
                return;
            }

            var source = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, GridColumnLayout>>>(
                File.ReadAllText(_paths.GridLayoutFile),
                SerializerOptions) ?? [];
            _layouts = new Dictionary<string, Dictionary<string, GridColumnLayout>>(
                source,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(exception, "Could not read grid layout file {LayoutFile}.", _paths.GridLayoutFile);
            _layouts = new Dictionary<string, Dictionary<string, GridColumnLayout>>(
                StringComparer.OrdinalIgnoreCase);
        }
    }

    private void WriteAtomically()
    {
        Directory.CreateDirectory(_paths.LocalDataDirectory);
        var temporaryPath = _paths.GridLayoutFile + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_layouts, SerializerOptions));
            File.Move(temporaryPath, _paths.GridLayoutFile, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not save grid layout file {LayoutFile}.", _paths.GridLayoutFile);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
