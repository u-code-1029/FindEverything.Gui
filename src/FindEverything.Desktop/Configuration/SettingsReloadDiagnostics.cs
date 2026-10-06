using FindEverything.Application.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace FindEverything.Desktop.Configuration;

public interface ISettingsReloadDiagnostics
{
    string? LastError { get; }

    event EventHandler? Changed;
}

public sealed class SettingsReloadDiagnostics :
    ISettingsReloadDiagnostics,
    IHostedService,
    IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<WorkspaceOptions> _workspace;
    private readonly IOptionsMonitor<IndexingOptions> _indexing;
    private readonly IOptionsMonitor<AppearanceOptions> _appearance;
    private readonly ILogger<SettingsReloadDiagnostics> _logger;
    private readonly object _sync = new();
    private IDisposable? _subscription;
    private CancellationTokenSource? _pendingValidation;
    private string? _lastError;

    public SettingsReloadDiagnostics(
        IConfiguration configuration,
        IOptionsMonitor<WorkspaceOptions> workspace,
        IOptionsMonitor<IndexingOptions> indexing,
        IOptionsMonitor<AppearanceOptions> appearance,
        ILogger<SettingsReloadDiagnostics> logger)
    {
        _configuration = configuration;
        _workspace = workspace;
        _indexing = indexing;
        _appearance = appearance;
        _logger = logger;
    }

    public string? LastError => Volatile.Read(ref _lastError);

    public event EventHandler? Changed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = ChangeToken.OnChange(
            _configuration.GetReloadToken,
            QueueValidation);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _pendingValidation?.Cancel();
        }

        return Task.CompletedTask;
    }

    private void QueueValidation()
    {
        CancellationTokenSource source;
        lock (_sync)
        {
            _pendingValidation?.Cancel();
            _pendingValidation?.Dispose();
            source = new CancellationTokenSource();
            _pendingValidation = source;
        }

        _ = ValidateAfterReloadAsync(source.Token);
    }

    private async Task ValidateAfterReloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            // OptionsMonitor invalidates its cache from the same configuration token.
            // Yield briefly so all token callbacks have completed before reading it.
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            _ = _workspace.CurrentValue;
            _ = _indexing.CurrentValue;
            _ = _appearance.CurrentValue;
            SetError(null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var message = $"사용자 설정 변경을 적용하지 못했습니다. 마지막 정상 설정을 유지합니다: {exception.GetBaseException().Message}";
            _logger.LogWarning(exception, "Rejected an invalid user settings reload.");
            SetError(message);
        }
    }

    private void SetError(string? message)
    {
        var previous = Interlocked.Exchange(ref _lastError, message);
        if (!string.Equals(previous, message, StringComparison.Ordinal))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        lock (_sync)
        {
            _pendingValidation?.Cancel();
            _pendingValidation?.Dispose();
            _pendingValidation = null;
        }
    }
}
