using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Media;
using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Application.Results;
using FindEverything.Desktop.Behaviors;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.Results;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace FindEverything.Desktop.Tests;

/// <summary>
/// The exact same fixture is run against the baseline and current checkout on
/// the smoke test's existing App dispatcher. Timings are evidence, not noisy
/// shared-runner pass/fail thresholds. No post-baseline APIs are used here.
/// </summary>
internal static class WorkspacePerformanceChecks
{
    private const int RowCount = 100_000;
    private const int FieldCount = 20;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Verify(IServiceProvider services)
    {
        var directory = Environment.GetEnvironmentVariable("FINDEVERYTHING_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        var label = Environment.GetEnvironmentVariable("FINDEVERYTHING_PERF_LABEL") ?? "current";
        label = string.Concat(label.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_'));
        if (label.Length == 0) label = "current";
        var dispatcher = Dispatcher.CurrentDispatcher;
        var fields = Enumerable.Range(0, FieldCount).Select(index => new ProfileFieldDescriptor(
            $"field-{index}", $"group-{index}", $"Column {index + 1}", index, true,
            ProfileFieldValueKind.String, false, null, null)).ToArray();
        var localizer = services.GetRequiredService<IAppLocalizer>();
        using var viewModel = ActivatorUtilities.CreateInstance<CatalogViewModel>(services);
        var query = new SyntheticQuery(fields);
        var placeholders = 0;
        var collectionEvents = new Dictionary<string, long>();
        var pageEvents = 0;
        using var collection = new VirtualResultCollection<CatalogItemViewModel>(async (offset, limit, token) =>
        {
            var page = await query.ReadRangeAsync(offset, limit, token).ConfigureAwait(false);
            return new(page.Items.Select(row => new CatalogItemViewModel(row.ToItem(), fields, localizer)).ToArray(),
                offset, page.HasMore);
        }, _ =>
        {
            placeholders++;
            return new CatalogItemViewModel(new CatalogItem("", "", "", new object(),
                new Dictionary<string, object?>(), false), fields, localizer) { IsPlaceholder = true };
        }, action => dispatcher.BeginInvoke(DispatcherPriority.Background, action),
            exception => throw new InvalidOperationException("The performance fixture page failed.", exception));
        var first = query.ReadRangeAsync(0, 500).GetAwaiter().GetResult();
        collection.Seed(new(first.Items.Select(row => new CatalogItemViewModel(row.ToItem(), fields, localizer)).ToArray(),
            0, first.HasMore), RowCount);
        collection.CollectionChanged += (_, args) =>
        {
            var key = args.Action.ToString();
            collectionEvents[key] = collectionEvents.GetValueOrDefault(key) + 1;
        };
        collection.PageLoaded += (_, _) => pageEvents++;
        SetField(viewModel, "_projectionQuery", query);
        SetField(viewModel, "_virtualItems", collection);
        SetField(viewModel, "_matchingCount", (long)RowCount);
        // Set the existing backing field without invoking the in-memory filter,
        // which would deliberately enumerate the virtual IList in this fixture.
        SetField(viewModel, "_filterText", "Alpha client-3");
        // Newer revisions publish a committed highlight query separately. Use
        // the same terms on both revisions, without requiring the new API.
        typeof(CatalogViewModel).GetProperty("AppliedHighlightQuery")?.SetValue(viewModel, "Alpha client-3");
        viewModel.Fields = fields;
        viewModel.Items = new ListCollectionView(collection);
        var grid = new DataGrid
        {
            DataContext = viewModel,
            ItemsSource = viewModel.Items,
            AutoGenerateColumns = false,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
            RowHeight = 44,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            Style = System.Windows.Application.Current?.TryFindResource("FluentDataGridStyle") as Style,
        };
        VirtualizingPanel.SetIsVirtualizing(grid, true);
        VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(grid, true);
        DynamicProfileGrid.SetLocalizer(grid, localizer);
        DynamicProfileGrid.SetFields(grid, fields);
        var window = new Window
        {
            Title = "FindEverything reproducible performance fixture",
            Width = 1280,
            Height = 780,
            Content = grid,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        try
        {
            window.Show();
            grid.UpdateLayout();
            PhaseMeasurement gridMeasurement;
            using (var heartbeat = new UiHeartbeat(dispatcher))
            {
                var started = Stopwatch.StartNew();
                var allocated = GC.GetTotalAllocatedBytes(false);
                var uiAllocated = GC.GetAllocatedBytesForCurrentThread();
                async Task ExerciseGrid()
                {
                    for (var iteration = 0; iteration < 64; iteration++)
                    {
                        var offset = (iteration * 1543) % (RowCount - 40);
                        var row = collection[offset];
                        // Mimic a viewport requesting individual items rather
                        // than accidentally enumerating 100,000 virtual rows.
                        for (var index = offset + 1; index < offset + 20; index++) _ = collection[index];
                        grid.ScrollIntoView(row);
                        await Task.Delay(5);
                        var deadline = Stopwatch.GetTimestamp();
                        while ((row = collection[offset]).IsPlaceholder)
                        {
                            if (Stopwatch.GetElapsedTime(deadline) > TimeSpan.FromSeconds(10))
                                throw new TimeoutException("The performance fixture page did not load.");
                            await Task.Delay(5);
                        }
                        await viewModel.SelectRowAsync(row, additive: true, range: false);
                        await viewModel.SelectRowAsync(row, additive: true, range: false);
                        if (iteration % 8 == 0)
                        {
                            viewModel.SelectAllCommand.Execute(null);
                            viewModel.InvertSelectionCommand.Execute(null);
                        }
                        await Task.Delay(5);
                    }
                    await Task.Delay(25);
                }
                // A watchdog detects a stuck dispatcher, not a performance
                // acceptance threshold. The unoptimized baseline needs more time.
                Pump(ExerciseGrid(), TimeSpan.FromSeconds(120));
                gridMeasurement = heartbeat.Capture(started.Elapsed,
                    GC.GetTotalAllocatedBytes(false) - allocated,
                    GC.GetAllocatedBytesForCurrentThread() - uiAllocated);
            }

            var console = new ScanConsoleViewModel(static _ => { });
            var consoleEvents = new Dictionary<string, long>();
            console.Lines.CollectionChanged += (_, args) =>
            {
                var key = args.Action.ToString();
                consoleEvents[key] = consoleEvents.GetValueOrDefault(key) + 1;
            };
            var drain = typeof(ScanConsoleViewModel).GetMethod("OnDrainTimerTick", PrivateInstance)!;
            var pendingCount = typeof(ScanConsoleViewModel).GetProperty("PendingLineCount", PrivateInstance);
            var pendingQueue = typeof(ScanConsoleViewModel).GetField("_pending", PrivateInstance);
            int GetPendingCount() => pendingCount is not null
                ? (int)pendingCount.GetValue(console)!
                : ((ICollection)pendingQueue!.GetValue(console)!).Count;
            var drainTimer = (DispatcherTimer)typeof(ScanConsoleViewModel).GetField("_drainTimer", PrivateInstance)!.GetValue(console)!;
            void Drain()
            {
                while (GetPendingCount() > 0) drain.Invoke(console, [null, EventArgs.Empty]);
            }
            var trace = new CatalogScanTraceEvent(Guid.NewGuid(), 0, DateTimeOffset.UtcNow,
                CatalogScanTraceKind.Started, "performance", "Performance", @"C:\Performance", null,
                null, null, null, null, new Dictionary<string, object?>(), [], null, "Performance fixture");
            PhaseMeasurement consoleMeasurement;
            try
            {
                using var heartbeat = new UiHeartbeat(dispatcher);
                var started = Stopwatch.StartNew();
                var allocated = GC.GetTotalAllocatedBytes(false);
                var uiAllocated = GC.GetAllocatedBytesForCurrentThread();
                async Task ExerciseConsole()
                {
                    console.Report(trace);
                    for (var sequence = 1; sequence <= RowCount; sequence++)
                    {
                        console.Report(trace with
                        {
                            Kind = CatalogScanTraceKind.DirectoryVisited,
                            Sequence = sequence,
                            FullPath = $@"C:\Performance\Item-{sequence:D6}",
                            MappingStatus = ProfileMapStatus.Success,
                            TraversalDecision = DirectoryTraversalDecision.Continue,
                        });
                        if (sequence % 500 != 0) continue;
                        Drain();
                        await Task.Delay(1);
                    }
                    console.Report(trace with { Kind = CatalogScanTraceKind.Completed, Sequence = RowCount + 1 });
                    Drain();
                    await Task.Delay(25);
                }
                Pump(ExerciseConsole(), TimeSpan.FromSeconds(60));
                consoleMeasurement = heartbeat.Capture(started.Elapsed,
                    GC.GetTotalAllocatedBytes(false) - allocated,
                    GC.GetAllocatedBytesForCurrentThread() - uiAllocated);
            }
            finally { drainTimer.Stop(); }
            var hiddenEvents = consoleEvents.Values.Sum();
            var visibleBeforeOpen = console.Lines.Count;
            var opening = Stopwatch.StartNew();
            console.IsPanelOpen = true;
            opening.Stop();

            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"performance-{label}.json"), JsonSerializer.Serialize(new
            {
                Label = label,
                Environment = CaptureEnvironment(window),
                Rows = RowCount,
                Fields = FieldCount,
                Grid = new
                {
                    Measurement = gridMeasurement,
                    Placeholders = placeholders,
                    CollectionEvents = collectionEvents,
                    PageEvents = pageEvents,
                    ProviderReads = query.ReadCount,
                    CachedRows = collection.CachedItemCount,
                },
                ClosedConsole = new
                {
                    Measurement = consoleMeasurement,
                    CollectionEvents = consoleEvents,
                    HiddenNotifications = hiddenEvents,
                    VisibleRowsWhileClosed = visibleBeforeOpen,
                    VisibleRowsAfterOpen = console.Lines.Count,
                    OpeningMilliseconds = opening.Elapsed.TotalMilliseconds,
                    Visited = console.VisitedCount,
                },
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.Close(); }
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, PrivateInstance)!.SetValue(target, value);

    private static void Pump(Task task, TimeSpan timeout)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var watchdog = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = timeout };
            watchdog.Tick += (_, _) => frame.Continue = false;
            watchdog.Start();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Send,
                new Action(() => frame.Continue = false)), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            watchdog.Stop();
        }
        if (!task.IsCompleted) throw new TimeoutException("The UI performance fixture timed out.");
        task.GetAwaiter().GetResult();
    }

    private sealed record PhaseMeasurement(double ElapsedMilliseconds, long AllocatedBytes,
        long UiAllocatedBytes, int HeartbeatSamples, double? HeartbeatDelayP95Milliseconds,
        double? HeartbeatDelayMaximumMilliseconds)
    {
        public string HeartbeatStatus => HeartbeatSamples == 0 ? "unmeasured"
            : HeartbeatSamples < 20 ? "insufficient-samples" : "measured";
    }

    internal static object CaptureEnvironment(FrameworkElement? visual)
    {
        var version = typeof(CatalogViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var sha = version is null ? null : Regex.Match(version, @"(?<![0-9a-fA-F])[0-9a-fA-F]{40}(?![0-9a-fA-F])").Value;
        var dpi = visual is null ? new DpiScale(1, 1) : VisualTreeHelper.GetDpi(visual);
        return new
        {
            CommitSha = string.IsNullOrEmpty(sha) ? null : sha,
            AssemblyInformationalVersion = version,
            WorkingTreeStatus = Environment.GetEnvironmentVariable("FINDEVERYTHING_PERF_WORKTREE") ?? "not-recorded",
            CapturedUtc = DateTimeOffset.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            LogicalProcessors = Environment.ProcessorCount,
            RunnerOS = Environment.GetEnvironmentVariable("RUNNER_OS"),
            DpiX = dpi.PixelsPerInchX, DpiY = dpi.PixelsPerInchY,
            ViewportWidth = visual?.ActualWidth, ViewportHeight = visual?.ActualHeight,
        };
    }

    private sealed class UiHeartbeat : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly List<double> _delays = [];
        private long _last = Stopwatch.GetTimestamp();

        public UiHeartbeat(Dispatcher dispatcher)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = TimeSpan.FromMilliseconds(8) };
            _timer.Tick += (_, _) =>
            {
                var current = Stopwatch.GetTimestamp();
                _delays.Add(Math.Max(0, Stopwatch.GetElapsedTime(_last, current).TotalMilliseconds - 8));
                _last = current;
            };
            _timer.Start();
        }

        public PhaseMeasurement Capture(TimeSpan elapsed, long allocated, long uiAllocated)
        {
            var samples = _delays.Order().ToArray();
            return new(elapsed.TotalMilliseconds, allocated, uiAllocated, samples.Length,
                samples.Length == 0 ? null : samples[Math.Max(0, (int)Math.Ceiling(samples.Length * 0.95) - 1)],
                samples.Length == 0 ? null : samples[^1]);
        }

        public void Dispose() => _timer.Stop();
    }

    private sealed class SyntheticQuery(IReadOnlyList<ProfileFieldDescriptor> fields) : IResultQuerySession<CatalogProjectionRow>
    {
        private int _readCount;
        public int ReadCount => _readCount;
        public string Identity => "performance-100k-20-fields";
        public Task<long> CountAsync(CancellationToken cancellationToken = default) => Task.FromResult((long)RowCount);
        public Task<ResultPage<CatalogProjectionRow>> ReadRangeAsync(int offset, int limit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _readCount);
            var rows = Enumerable.Range(offset, Math.Min(limit, RowCount - offset)).Select(index =>
                new CatalogProjectionRow($@"C:\Performance\2026\client-{index % 19}\Item-{index:D6}",
                    $"Item-{index:D6}", "performance-rule",
                    fields.ToDictionary(field => field.FieldId,
                        field => (object?)$"Alpha client-{index % 19} {field.Header} value-{index:D6}"), false)).ToArray();
            return Task.FromResult(new ResultPage<CatalogProjectionRow>(rows, offset, offset + rows.Length < RowCount));
        }
        public IAsyncEnumerable<CatalogProjectionRow> EnumerateSelectionAsync(QuerySelection selection,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
