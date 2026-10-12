using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Application.Options;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.Services;

/// <summary>
/// Routes existing operation-result notifications to one app-bottom InfoBar.
/// Timing is controlled by user settings instead of individual callers; Windows
/// completion toasts remain a separate notification channel.
/// </summary>
public sealed partial class OperationNotificationService : ObservableObject, ISnackbarService, IDisposable
{
    private readonly IValidatedSettingsState<NotificationOptions> _settings;
    private readonly IUiScheduler _scheduler;
    private readonly TimeProvider _timeProvider;
    private readonly object _timerGate = new();
    private CancellationTokenSource? _dismissSource;
    private SnackbarPresenter? _legacyPresenter;
    private long _revision;
    private int _disposed;

    public OperationNotificationService(
        IValidatedSettingsState<NotificationOptions> settings,
        IUiScheduler scheduler,
        TimeProvider? timeProvider = null)
    {
        _settings = settings;
        _scheduler = scheduler;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private InfoBarSeverity _severity = InfoBarSeverity.Informational;

    // Retained for WPF-UI's service contract. Show deliberately uses the shared
    // validated setting, so old call sites cannot accidentally override it.
    public TimeSpan DefaultTimeOut { get; set; } = TimeSpan.FromSeconds(3);

    public void SetSnackbarPresenter(SnackbarPresenter contentPresenter) =>
        _legacyPresenter = contentPresenter;

    public SnackbarPresenter? GetSnackbarPresenter() => _legacyPresenter;

    public void Show(string title, string message, ControlAppearance appearance, IconElement? icon, TimeSpan timeout)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _scheduler.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            CancelDismiss();
            Title = title;
            Message = message;
            Severity = appearance switch
            {
                ControlAppearance.Danger => InfoBarSeverity.Error,
                ControlAppearance.Caution => InfoBarSeverity.Warning,
                ControlAppearance.Success => InfoBarSeverity.Success,
                _ => InfoBarSeverity.Informational,
            };
            IsOpen = true;

            var duration = TimeSpan.FromSeconds(_settings.Current.AutoDismissSeconds);
            var source = new CancellationTokenSource();
            var token = source.Token;
            long revision;
            lock (_timerGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    source.Dispose();
                    return;
                }

                _dismissSource = source;
                revision = Interlocked.Increment(ref _revision);
            }

            _ = AutoDismissAsync(duration, revision, token);
        });
    }

    [RelayCommand]
    private void Dismiss() => _scheduler.Post(() => IsOpen = false);

    partial void OnIsOpenChanged(bool value)
    {
        if (!value) CancelDismiss();
    }

    private async Task AutoDismissAsync(TimeSpan duration, long revision, CancellationToken token)
    {
        try
        {
            await Task.Delay(duration, _timeProvider, token).ConfigureAwait(false);
            _scheduler.Post(() =>
            {
                if (!token.IsCancellationRequested
                    && Volatile.Read(ref _disposed) == 0
                    && Volatile.Read(ref _revision) == revision)
                {
                    IsOpen = false;
                }
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private void CancelDismiss()
    {
        lock (_timerGate)
        {
            Interlocked.Increment(ref _revision);
            _dismissSource?.Cancel();
            _dismissSource?.Dispose();
            _dismissSource = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) CancelDismiss();
    }
}
