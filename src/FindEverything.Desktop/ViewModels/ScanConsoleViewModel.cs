using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.Localization;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public enum ScanConsoleLineTone
{
    Default = 0,
    Lifecycle = 1,
    Match = 2,
    NoMatch = 3,
    Pruned = 4,
    Error = 5,
    Warning = 6,
}

public sealed record ScanConsoleLineViewModel(
    Guid OperationId,
    long Sequence,
    CatalogScanTraceKind Kind,
    ScanConsoleLineTone Tone,
    string Text,
    string SessionSummary,
    string? Path)
{
    public bool HasPath => !string.IsNullOrWhiteSpace(Path);
}

public partial class ScanConsoleViewModel : ObservableObject, ICatalogScanTraceSink, IScanConsolePanelController
{
    private const int DrainBatchSize = 500;
    private static readonly TimeSpan DrainInterval = TimeSpan.FromMilliseconds(75);
    private const int MaximumPendingLines = 10_000;
    private const int MaximumVisibleLines = 20_000;
    private const int VisibleTrimChunk = 2_000;
    private static readonly Brush IdleBrush = CreateBrush(0x8A, 0x88, 0x88);
    private static readonly Brush RunningBrush = CreateBrush(0x00, 0x78, 0xD4);
    private static readonly Brush SuccessBrush = CreateBrush(0x10, 0x7C, 0x10);
    private static readonly Brush WarningBrush = CreateBrush(0xCA, 0x50, 0x10);
    private static readonly Brush ErrorBrush = CreateBrush(0xC4, 0x2B, 0x1C);

    private readonly object _pendingGate = new();
    private readonly LinkedList<ScanConsoleLineViewModel> _pending = [];
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _drainTimer;
    private readonly Action<string> _setClipboardText;
    private readonly Func<string, string?> _pickScanLogPath;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task> _writeScanLogAsync;
    private readonly Action<string, string, ControlAppearance> _showSnackbar;
    private readonly Action<Exception> _logExportFailure;
    private readonly IAppLocalizer? _localizer;
    private int _drainScheduled;
    private long _visited;
    private long _matched;
    private long _noMatch;
    private long _invalid;
    private long _pruned;
    private long _dropped;

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanelToggleToolTip))]
    private bool _isPanelOpen;

    [ObservableProperty]
    private string _statusText = "대기 중";

    [ObservableProperty]
    private string _sessionSummary = "바로 스캔을 시작하면 방문 경로가 여기에 표시됩니다.";

    [ObservableProperty]
    private Brush _statusBrush = IdleBrush;

    [ObservableProperty]
    private long _visitedCount;

    [ObservableProperty]
    private long _matchedCount;

    [ObservableProperty]
    private long _noMatchCount;

    [ObservableProperty]
    private long _invalidCount;

    [ObservableProperty]
    private long _prunedCount;

    [ObservableProperty]
    private string _retentionSummary = $"표시 0 / 최대 {MaximumVisibleLines:N0}줄";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedMessageCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedPathCommand))]
    private ScanConsoleLineViewModel? _selectedLine;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveLogCommand))]
    private bool _isSavingLog;

    public ScanConsoleViewModel(
        IDesktopPickerService pickerService,
        IScanConsoleLogWriter logWriter,
        ISnackbarService snackbarService,
        IAppLocalizer localizer,
        ILogger<ScanConsoleViewModel> logger)
        : this(
            System.Windows.Clipboard.SetText,
            pickerService.PickScanLogPath,
            logWriter.WriteAsync,
            (title, message, appearance) => snackbarService.Show(
                title,
                message,
                appearance,
                null,
                TimeSpan.FromSeconds(5)),
            exception => logger.LogError(exception, "Could not export the scan console log."),
            localizer)
    {
    }

    internal ScanConsoleViewModel(Action<string> setClipboardText)
        : this(
            setClipboardText,
            static _ => null,
            static (_, _, _) => Task.CompletedTask,
            static (_, _, _) => { },
            static _ => { },
            null)
    {
    }

    internal ScanConsoleViewModel(
        Action<string> setClipboardText,
        Func<string, string?> pickScanLogPath,
        Func<string, IReadOnlyList<string>, CancellationToken, Task> writeScanLogAsync,
        Action<string, string, ControlAppearance> showSnackbar,
        Action<Exception>? logExportFailure = null,
        IAppLocalizer? localizer = null)
    {
        ArgumentNullException.ThrowIfNull(setClipboardText);
        ArgumentNullException.ThrowIfNull(pickScanLogPath);
        ArgumentNullException.ThrowIfNull(writeScanLogAsync);
        ArgumentNullException.ThrowIfNull(showSnackbar);
        _setClipboardText = setClipboardText;
        _pickScanLogPath = pickScanLogPath;
        _writeScanLogAsync = writeScanLogAsync;
        _showSnackbar = showSnackbar;
        _logExportFailure = logExportFailure ?? (static _ => { });
        _localizer = localizer;
        _dispatcher = System.Windows.Application.Current?.Dispatcher
            ?? Dispatcher.CurrentDispatcher;
        _drainTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = DrainInterval,
        };
        _drainTimer.Tick += OnDrainTimerTick;
        StatusText = L("Loc.Scan.Status.Idle", "대기 중");
        SessionSummary = L(
            "Loc.Scan.Status.Instruction",
            "바로 스캔을 시작하면 방문 경로가 여기에 표시됩니다.");
        RetentionSummary = F(
            "Loc.Scan.Retention.Empty",
            "표시 0 / 최대 {0:N0}줄",
            MaximumVisibleLines);
    }

    public ObservableCollection<ScanConsoleLineViewModel> Lines { get; } =
        new ScanConsoleLineCollection();

    public string PanelToggleToolTip => IsPanelOpen
        ? L("Loc.Scan.Panel.Close", "하단 탐색 로그 닫기")
        : L("Loc.Scan.Panel.Open", "하단 탐색 로그 열기");

    public void Show()
    {
        if (_dispatcher.CheckAccess())
        {
            IsPanelOpen = true;
            return;
        }

        if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
        {
            _ = _dispatcher.BeginInvoke(() => IsPanelOpen = true);
        }
    }

    [RelayCommand]
    private void TogglePanel() => IsPanelOpen = !IsPanelOpen;

    [RelayCommand]
    private void ClosePanel() => IsPanelOpen = false;

    public void Report(CatalogScanTraceEvent value)
    {
        if (value is null)
        {
            return;
        }

        try
        {
            UpdateBackgroundCounters(value);
            Enqueue(CreateLine(value, _localizer));
        }
        catch (Exception)
        {
            // Diagnostics are observational. A formatting or dispatcher failure must
            // never be able to interrupt the file-system scan.
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopyAll))]
    private void CopyAll()
    {
        var text = string.Join(Environment.NewLine, Lines.Select(static line => line.Text));
        if (text.Length > 0)
        {
            TrySetClipboardText(text);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopySelectedMessage))]
    private void CopySelectedMessage()
    {
        if (SelectedLine is { Text.Length: > 0 } line)
        {
            TrySetClipboardText(line.Text);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopySelectedPath))]
    private void CopySelectedPath()
    {
        if (SelectedLine is { HasPath: true, Path: { } path })
        {
            TrySetClipboardText(path);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveLog))]
    private async Task SaveLogAsync()
    {
        var snapshot = Lines.Select(static line => line.Text).ToArray();
        if (snapshot.Length == 0)
        {
            return;
        }

        IsSavingLog = true;
        try
        {
            var suggestedFileName =
                $"FindEverything-scan-{DateTime.Now:yyyyMMdd-HHmmss}.log";
            var selectedPath = _pickScanLogPath(suggestedFileName);
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            await _writeScanLogAsync(
                    selectedPath,
                    snapshot,
                    CancellationToken.None)
                .ConfigureAwait(true);
            _showSnackbar(
                L("Loc.Scan.Save.Completed", "탐색 로그 저장 완료"),
                F(
                    "Loc.Scan.Save.Completed.Message",
                    "현재 표시된 로그 {0:N0}줄을 저장했습니다.\n{1}",
                    snapshot.Length,
                    selectedPath),
                ControlAppearance.Success);
        }
        catch (Exception exception)
        {
            _logExportFailure(exception);
            _showSnackbar(
                L("Loc.Scan.Save.Failed", "탐색 로그 저장 실패"),
                exception.Message,
                ControlAppearance.Danger);
        }
        finally
        {
            IsSavingLog = false;
        }
    }

    [RelayCommand]
    private void Clear()
    {
        lock (_pendingGate)
        {
            var pendingLifecycle = _pending
                .Where(static line => line.Kind is
                    CatalogScanTraceKind.Started
                    or CatalogScanTraceKind.Completed
                    or CatalogScanTraceKind.Cancelled
                    or CatalogScanTraceKind.Failed)
                .ToArray();
            _pending.Clear();
            foreach (var line in pendingLifecycle)
            {
                _pending.AddLast(line);
            }
        }

        Lines.Clear();
        SelectedLine = null;
        Interlocked.Exchange(ref _dropped, 0);
        UpdateCounterProperties();
        CopyAllCommand.NotifyCanExecuteChanged();
        SaveLogCommand.NotifyCanExecuteChanged();
    }

    private bool CanCopyAll() => Lines.Count > 0;

    private bool CanSaveLog() => Lines.Count > 0 && !IsSavingLog;

    private bool CanCopySelectedMessage() => SelectedLine is { Text.Length: > 0 };

    private bool CanCopySelectedPath() => SelectedLine is { HasPath: true };

    private void TrySetClipboardText(string text)
    {
        try
        {
            _setClipboardText(text);
        }
        catch (Exception)
        {
            // The clipboard can be temporarily locked or unavailable. Diagnostics
            // are observational, so a failed copy must not close the panel or stop
            // the active file-system scan. The user can retry from the same row.
        }
    }

    private void UpdateBackgroundCounters(CatalogScanTraceEvent value)
    {
        if (value.Kind == CatalogScanTraceKind.Started)
        {
            Interlocked.Exchange(ref _visited, 0);
            Interlocked.Exchange(ref _matched, 0);
            Interlocked.Exchange(ref _noMatch, 0);
            Interlocked.Exchange(ref _invalid, 0);
            Interlocked.Exchange(ref _pruned, 0);
            Interlocked.Exchange(ref _dropped, 0);
            return;
        }

        if (value.Kind == CatalogScanTraceKind.DirectoryExcluded)
        {
            _ = Interlocked.Increment(ref _visited);
            _ = Interlocked.Increment(ref _pruned);
            return;
        }

        if (value.Kind != CatalogScanTraceKind.DirectoryVisited)
        {
            return;
        }

        _ = Interlocked.Increment(ref _visited);
        switch (value.MappingStatus)
        {
            case ProfileMapStatus.Success:
                _ = Interlocked.Increment(ref _matched);
                break;
            case ProfileMapStatus.NoMatch:
                _ = Interlocked.Increment(ref _noMatch);
                break;
            case ProfileMapStatus.Invalid:
                _ = Interlocked.Increment(ref _invalid);
                break;
        }

        if (value.TraversalDecision == DirectoryTraversalDecision.SkipDescendants)
        {
            _ = Interlocked.Increment(ref _pruned);
        }
    }

    private void Enqueue(ScanConsoleLineViewModel line)
    {
        lock (_pendingGate)
        {
            if (line.Kind == CatalogScanTraceKind.Started)
            {
                // A new operation owns the console. Discard any not-yet-rendered
                // tail from the previous operation before its START marker is queued.
                _pending.Clear();
            }

            if (_pending.Count >= MaximumPendingLines)
            {
                // Keep the current session's START marker so a saturated queue can
                // never mix new lines into the previous session's visible log.
                var nodeToDrop = _pending.First?.Value.Kind == CatalogScanTraceKind.Started
                    ? _pending.First.Next
                    : _pending.First;
                if (nodeToDrop is not null)
                {
                    _pending.Remove(nodeToDrop);
                    _ = Interlocked.Increment(ref _dropped);
                }
            }

            _pending.AddLast(line);
        }

        ScheduleDrain();
    }

    private void ScheduleDrain()
    {
        if (Interlocked.Exchange(ref _drainScheduled, 1) != 0)
        {
            return;
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            Interlocked.Exchange(ref _drainScheduled, 0);
            return;
        }

        _ = _dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () => _drainTimer.Start());
    }

    private void OnDrainTimerTick(object? sender, EventArgs eventArgs)
    {
        List<ScanConsoleLineViewModel> batch = [];
        lock (_pendingGate)
        {
            while (batch.Count < DrainBatchSize && _pending.First is { } node)
            {
                batch.Add(node.Value);
                _pending.RemoveFirst();
            }
        }

        foreach (var line in batch)
        {
            ApplyLine(line);
        }

        UpdateCounterProperties();
        CopyAllCommand.NotifyCanExecuteChanged();
        SaveLogCommand.NotifyCanExecuteChanged();

        lock (_pendingGate)
        {
            if (_pending.Count > 0)
            {
                return;
            }

            _drainTimer.Stop();
            Interlocked.Exchange(ref _drainScheduled, 0);
        }
    }

    private void ApplyLine(ScanConsoleLineViewModel line)
    {
        if (line.Kind == CatalogScanTraceKind.Started)
        {
            Lines.Clear();
            SelectedLine = null;
        }

        if (Lines.Count >= MaximumVisibleLines)
        {
            var removeCount = Math.Min(VisibleTrimChunk, Lines.Count);
            ((ScanConsoleLineCollection)Lines).RemoveFirst(removeCount);
            _ = Interlocked.Add(ref _dropped, removeCount);
        }

        Lines.Add(line);

        switch (line.Kind)
        {
            case CatalogScanTraceKind.Started:
                StatusText = L("Loc.Scan.Status.Running", "탐색 중");
                StatusBrush = RunningBrush;
                SessionSummary = line.SessionSummary;
                break;
            case CatalogScanTraceKind.Completed:
                StatusText = L("Loc.Common.Completed", "완료");
                StatusBrush = SuccessBrush;
                break;
            case CatalogScanTraceKind.Cancelled:
                StatusText = L("Loc.Common.Cancelled", "취소됨");
                StatusBrush = WarningBrush;
                break;
            case CatalogScanTraceKind.Failed:
                StatusText = L("Loc.Scan.Status.Failed", "실패");
                StatusBrush = ErrorBrush;
                break;
        }
    }

    private void UpdateCounterProperties()
    {
        VisitedCount = Interlocked.Read(ref _visited);
        MatchedCount = Interlocked.Read(ref _matched);
        NoMatchCount = Interlocked.Read(ref _noMatch);
        InvalidCount = Interlocked.Read(ref _invalid);
        PrunedCount = Interlocked.Read(ref _pruned);
        var dropped = Interlocked.Read(ref _dropped);
        RetentionSummary = dropped > 0
            ? F(
                "Loc.Scan.Retention.Dropped",
                "최근 {0:N0}줄 · 이전 {1:N0}줄 생략",
                Lines.Count,
                dropped)
            : F(
                "Loc.Scan.Retention.Visible",
                "표시 {0:N0} / 최대 {1:N0}줄",
                Lines.Count,
                MaximumVisibleLines);
    }

    internal static ScanConsoleLineViewModel CreateLine(
        CatalogScanTraceEvent value,
        IAppLocalizer? localizer = null)
    {
        var timestamp = value.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var builder = new StringBuilder(timestamp.Length + 160);
        builder.Append(timestamp).Append(' ');
        var tone = ScanConsoleLineTone.Default;

        switch (value.Kind)
        {
            case CatalogScanTraceKind.Started:
                tone = ScanConsoleLineTone.Lifecycle;
                builder.Append("[START] ")
                    .Append("profile=\"").Append(Escape(value.ProfileDisplayName)).Append("\"")
                    .Append(" | id=").Append(value.ProfileId)
                    .Append(" | root=\"").Append(Escape(value.RootPath)).Append('"');
                break;
            case CatalogScanTraceKind.DirectoryVisited:
                tone = AppendDirectoryVisit(builder, value, localizer);
                break;
            case CatalogScanTraceKind.DirectoryExcluded:
                tone = AppendDirectoryExclusion(builder, value, localizer);
                break;
            case CatalogScanTraceKind.DirectoryExclusionIssue:
                tone = AppendDirectoryExclusionIssue(builder, value, localizer);
                break;
            case CatalogScanTraceKind.DiscoveryError:
                tone = ScanConsoleLineTone.Error;
                builder.Append("[IO ERROR] path=\"").Append(Escape(value.FullPath)).Append("\"")
                    .Append(" | ").Append(Escape(value.Message));
                break;
            case CatalogScanTraceKind.Completed:
                tone = ScanConsoleLineTone.Lifecycle;
                builder.Append("[DONE] ").Append(Escape(
                    localizer?.Get(
                        "Loc.Scan.Trace.Completed",
                        "바로 스캔을 완료했습니다.")
                    ?? value.Message));
                break;
            case CatalogScanTraceKind.Cancelled:
                tone = ScanConsoleLineTone.Pruned;
                builder.Append("[CANCELLED] ").Append(Escape(
                    localizer?.Get(
                        "Loc.Scan.Trace.Cancelled",
                        "바로 스캔이 취소되었습니다.")
                    ?? value.Message));
                break;
            case CatalogScanTraceKind.Failed:
                tone = ScanConsoleLineTone.Error;
                builder.Append("[FAILED] ").Append(Escape(
                    localizer?.Get(
                        "Loc.Scan.Trace.Failed",
                        "바로 스캔에 실패했습니다. 상태 메시지에서 자세한 내용을 확인하세요.")
                    ?? value.Message));
                break;
            default:
                builder.Append("[TRACE] ").Append(Escape(value.Message));
                break;
        }

        return new ScanConsoleLineViewModel(
            value.OperationId,
            value.Sequence,
            value.Kind,
            tone,
            builder.ToString(),
            CreateLifecycleSummary(value, localizer),
            GetCopyablePath(value));
    }

    private static string? GetCopyablePath(CatalogScanTraceEvent value)
    {
        var path = value.Kind switch
        {
            CatalogScanTraceKind.Started => value.RootPath,
            CatalogScanTraceKind.DirectoryVisited
                or CatalogScanTraceKind.DirectoryExcluded
                or CatalogScanTraceKind.DirectoryExclusionIssue
                or CatalogScanTraceKind.DiscoveryError => value.FullPath,
            _ => null,
        };

        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static ScanConsoleLineTone AppendDirectoryVisit(
        StringBuilder builder,
        CatalogScanTraceEvent value,
        IAppLocalizer? localizer)
    {
        var tone = value.MappingStatus switch
        {
            ProfileMapStatus.Success => ScanConsoleLineTone.Match,
            ProfileMapStatus.NoMatch => ScanConsoleLineTone.NoMatch,
            ProfileMapStatus.Invalid => ScanConsoleLineTone.Error,
            _ => ScanConsoleLineTone.Default,
        };
        var status = value.MappingStatus switch
        {
            ProfileMapStatus.Success => "MATCH",
            ProfileMapStatus.NoMatch => "NO MATCH",
            ProfileMapStatus.Invalid => "INVALID",
            _ => "UNKNOWN",
        };
        var pruned = value.TraversalDecision == DirectoryTraversalDecision.SkipDescendants;
        if (pruned && value.MappingStatus != ProfileMapStatus.Invalid)
        {
            tone = ScanConsoleLineTone.Pruned;
        }

        builder.Append('[').Append(status).Append("] ")
            .Append(pruned ? "[PRUNE] " : "[CONTINUE] ")
            .Append('#').Append(value.Sequence.ToString("000000", CultureInfo.InvariantCulture))
            .Append(" input=\"").Append(Escape(value.MatchInput)).Append('"');

        if (!string.IsNullOrWhiteSpace(value.MatchedRuleId))
        {
            builder.Append(" | rule=").Append(value.MatchedRuleId);
        }

        if (value.Values.Count > 0)
        {
            builder.Append(" | values={");
            var first = true;
            foreach (var pair in value.Values.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                if (!first)
                {
                    builder.Append("; ");
                }

                first = false;
                builder.Append(pair.Key).Append('=').Append(Escape(FormatValue(pair.Value)));
            }

            builder.Append('}');
        }

        if (value.Issues.Count > 0)
        {
            builder.Append(" | issues={");
            for (var index = 0; index < value.Issues.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append("; ");
                }

                var issue = value.Issues[index];
                builder.Append(issue.Code);
                if (!string.IsNullOrWhiteSpace(issue.FieldId))
                {
                    builder.Append('[').Append(issue.FieldId).Append(']');
                }

                builder.Append(": ").Append(Escape(
                    ProfileDiagnosticLocalizer.Translate(localizer, issue)));
            }

            builder.Append('}');
        }

        builder.Append(" | full=\"").Append(Escape(value.FullPath)).Append('"');
        return tone;
    }

    private static ScanConsoleLineTone AppendDirectoryExclusion(
        StringBuilder builder,
        CatalogScanTraceEvent value,
        IAppLocalizer? localizer)
    {
        builder.Append("[EXCLUDED] [PRUNE] ")
            .Append('#').Append(value.Sequence.ToString("000000", CultureInfo.InvariantCulture))
            .Append(" name=\"").Append(Escape(value.MatchInput)).Append('"');

        if (!string.IsNullOrWhiteSpace(value.MatchedRuleId))
        {
            builder.Append(" | rule=").Append(value.MatchedRuleId);
        }

        if (value.Issues.Count > 0)
        {
            builder.Append(" | issues={");
            for (var index = 0; index < value.Issues.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append("; ");
                }

                var issue = value.Issues[index];
                builder.Append(issue.Code).Append(": ").Append(Escape(
                    ProfileDiagnosticLocalizer.Translate(localizer, issue)));
            }

            builder.Append('}');
        }

        builder.Append(" | full=\"").Append(Escape(value.FullPath)).Append('"');
        return ScanConsoleLineTone.Pruned;
    }

    private static ScanConsoleLineTone AppendDirectoryExclusionIssue(
        StringBuilder builder,
        CatalogScanTraceEvent value,
        IAppLocalizer? localizer)
    {
        builder.Append("[EXCLUDE WARNING] [CONTINUE] ")
            .Append('#').Append(value.Sequence.ToString("000000", CultureInfo.InvariantCulture))
            .Append(" name=\"").Append(Escape(value.MatchInput)).Append('"');

        if (value.Issues.Count > 0)
        {
            builder.Append(" | issues={");
            for (var index = 0; index < value.Issues.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append("; ");
                }

                var issue = value.Issues[index];
                builder.Append(issue.Code).Append(": ").Append(Escape(
                    ProfileDiagnosticLocalizer.Translate(localizer, issue)));
            }

            builder.Append('}');
        }

        builder.Append(" | full=\"").Append(Escape(value.FullPath)).Append('"');
        return ScanConsoleLineTone.Warning;
    }

    private static string CreateLifecycleSummary(
        CatalogScanTraceEvent value,
        IAppLocalizer? localizer) =>
        value.Kind == CatalogScanTraceKind.Started
            ? localizer?.Format(
                "Loc.Scan.Session.Summary",
                "{0} · 절대 경로 · {1}",
                value.ProfileDisplayName,
                value.RootPath)
              ?? $"{value.ProfileDisplayName} · 절대 경로 · {value.RootPath}"
            : string.Empty;

    private string L(string key, string koreanFallback) =>
        _localizer?.Get(key, koreanFallback) ?? koreanFallback;

    private string F(string key, string koreanFallback, params object?[] arguments) =>
        _localizer?.Format(key, koreanFallback, arguments)
        ?? string.Format(CultureInfo.CurrentCulture, koreanFallback, arguments);

    private static string FormatValue(object? value) =>
        value switch
        {
            null => "<null>",
            DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.CurrentCulture),
            _ => value.ToString() ?? string.Empty,
        };

    private static string Escape(string? value) =>
        (value ?? string.Empty)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

    private static Brush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private sealed class ScanConsoleLineCollection : ObservableCollection<ScanConsoleLineViewModel>
    {
        public void RemoveFirst(int count)
        {
            if (count <= 0)
            {
                return;
            }

            CheckReentrancy();
            var actualCount = Math.Min(count, Items.Count);
            for (var index = 0; index < actualCount; index++)
            {
                Items.RemoveAt(0);
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Reset));
        }
    }
}
