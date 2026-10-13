namespace FindEverything.Desktop.Services;

/// <summary>
/// A retained page session. Activation owns passive reads and derived presentation
/// work; user-started operations belong to the application operation coordinator.
/// The DI container disposes the session when the host shuts down.
/// </summary>
public interface IPageSession : IDisposable
{
    Task ActivateAsync(CancellationToken cancellationToken = default);

    void Deactivate();
}
