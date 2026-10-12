using FindEverything.Application.Catalog;
using FindEverything.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.Configuration;

public interface IProfileUserInputValueStore
{
    IReadOnlyDictionary<string, string> GetValues(string profileId, string absolutePath);
    Task SetValueAsync(string profileId, string absolutePath, string fieldId,
        string value, CancellationToken cancellationToken = default);
}

/// <summary>Per-cell SQLite updates avoid rewriting or truncating a global notes document.</summary>
public sealed class ProfileUserInputValueStore : IProfileUserInputValueStore
{
    private readonly IUserInputStore _store;
    public ProfileUserInputValueStore(AppPaths paths,
        ILogger<ProfileUserInputValueStore> logger, IUserInputStore? store = null)
    {
        _store = store ?? new SqliteCatalogProjectionStore(paths.ResultsDatabaseFile);
    }
    public IReadOnlyDictionary<string, string> GetValues(string profileId, string absolutePath) =>
        _store.GetValues(profileId, absolutePath);
    public Task SetValueAsync(string profileId, string absolutePath, string fieldId,
        string value, CancellationToken cancellationToken = default) =>
        _store.SetValueAsync(profileId, absolutePath, fieldId, value, cancellationToken);
}
