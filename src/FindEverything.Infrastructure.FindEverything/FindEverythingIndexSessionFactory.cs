using FindEverything.Application.Indexing;

namespace FindEverything.Infrastructure.FindEverything;

public sealed class FindEverythingIndexSessionFactory : IIndexSessionFactory
{
    public IIndexSession Create(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return new FindEverythingIndexSession(databasePath);
    }
}
