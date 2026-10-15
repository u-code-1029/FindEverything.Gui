using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using FindEverything.Application.Options;
using FindEverything.Desktop.Localization;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.Services;

public sealed record OperationNotificationEntry(DateTimeOffset Time, string Title, string Message,
    InfoBarSeverity Severity, SymbolRegular Symbol, string SeverityName)
{
    public string Details => $"{Time:g} · {SeverityName}\n{Title}\n{Message}";
}

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
    private bool _interactionPaused;
    private readonly IAppLocalizer? _localizer;
    private readonly IClipboardService? _clipboard;

    public OperationNotificationService(
        IValidatedSettingsState<NotificationOptions> settings,
        IUiScheduler scheduler,
        TimeProvider? timeProvider = null,
        IAppLocalizer? localizer = null,
        IClipboardService? clipboard = null)
    {
        _settings = settings;
        _scheduler = scheduler;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _localizer = localizer;
        _clipboard = clipboard;
    }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private InfoBarSeverity _severity = InfoBarSeverity.Informational;

    public ObservableCollection<OperationNotificationEntry> RecentNotifications { get; } = [];
    [ObservableProperty] private OperationNotificationEntry? _selectedNotification;
    [ObservableProperty] private bool _isHistoryOpen;
    [ObservableProperty] private string _historyStatus = string.Empty;
    public bool HasHistory => RecentNotifications.Count > 0;
    public SymbolRegular SeveritySymbol => SymbolFor(Severity);
    public string SeverityName => NameFor(Severity);

    partial void OnSeverityChanged(InfoBarSeverity value)
    { OnPropertyChanged(nameof(SeveritySymbol)); OnPropertyChanged(nameof(SeverityName)); }
    partial void OnIsHistoryOpenChanged(bool value)
    {
        if (value) CancelDismiss();
        else ScheduleDismiss();
    }

    /// <summary>Keep a notice readable while pointer or keyboard focus is inside it.</summary>
    public void SetInteractionPaused(bool value)
    {
        if (_interactionPaused == value) return;
        _interactionPaused = value;
        if (value) CancelDismiss(); else ScheduleDismiss();
    }

    [RelayCommand]
    private void CopyNotificationDetails()
    {
        if (SelectedNotification is null || _clipboard is null) return;
        HistoryStatus = _clipboard.TrySetText(SelectedNotification.Details, out var error)
            ? L("Loc.Ux.Shell.Notifications.Copied", "알림 내용을 복사했습니다.")
            : error ?? L("Loc.Common.ClipboardFailed", "클립보드에 복사하지 못했습니다.");
    }

    private static SymbolRegular SymbolFor(InfoBarSeverity value) => value switch
    {
        InfoBarSeverity.Error => SymbolRegular.ErrorCircle20,
        InfoBarSeverity.Warning => SymbolRegular.Warning20,
        InfoBarSeverity.Success => SymbolRegular.CheckmarkCircle20,
        _ => SymbolRegular.Info20,
    };
    private string NameFor(InfoBarSeverity value) => value switch
    {
        InfoBarSeverity.Error => L("Loc.Ux.Shell.Notifications.Error", "오류"),
        InfoBarSeverity.Warning => L("Loc.Ux.Shell.Notifications.Warning", "확인 필요"),
        InfoBarSeverity.Success => L("Loc.Ux.Shell.Notifications.Success", "완료"),
        _ => L("Loc.Ux.Shell.Notifications.Information", "안내"),
    };
    private string L(string key, string fallback) => _localizer?.Get(key, fallback) ?? fallback;

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
            var entry = new OperationNotificationEntry(_timeProvider.GetLocalNow(), title, message, Severity, SeveritySymbol, SeverityName);
            RecentNotifications.Insert(0, entry);
            while (RecentNotifications.Count > 30) RecentNotifications.RemoveAt(RecentNotifications.Count - 1);
            if (!IsHistoryOpen || SelectedNotification is null) SelectedNotification = entry;
            OnPropertyChanged(nameof(HasHistory));
            ScheduleDismiss();
        });
    }

    private void ScheduleDismiss()
    {
            CancelDismiss();
            if (!IsOpen || _interactionPaused || IsHistoryOpen || Severity == InfoBarSeverity.Error
                || Volatile.Read(ref _disposed) != 0) return;
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
