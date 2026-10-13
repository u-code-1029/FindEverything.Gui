using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views;
using FindEverything.Desktop.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;

namespace FindEverything.Desktop.Tests;

/// <summary>Same real-page workload on both revisions; no post-baseline APIs required.</summary>
internal static class PageResponsivenessChecks
{
    public static void Verify(IServiceProvider services)
    {
        var directory = Environment.GetEnvironmentVariable("FINDEVERYTHING_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        var label = Environment.GetEnvironmentVariable("FINDEVERYTHING_PERF_LABEL") ?? "current";
        label = string.Concat(label.Where(char.IsLetterOrDigit));
        var phases = new List<Measurement>();
        MainWindow? window = null;
        Measure("ShellConstructionFirstRender", () =>
        {
            window = services.GetRequiredService<MainWindow>();
            System.Windows.Application.Current.MainWindow = window;
            window.Width = 1280;
            window.Height = 800;
            window.Show();
            services.GetRequiredService<INavigationService>().Navigate(typeof(HomePage));
        });
        var navigation = services.GetRequiredService<INavigationService>();
        Measure("ProfilesFirstEntry", () => navigation.Navigate(typeof(ProfilesPage)));
        var vm = services.GetRequiredService<ProfilesViewModel>();
        var page = services.GetRequiredService<ProfilesPage>();
        while (vm.DraftFields.Count < 20) vm.AddFieldCommand.Execute(null);
        vm.GoToEditorStepCommand.Execute(ProfileEditorStep.PathAndFields);
        Pump(Task.Delay(400));
        var stepControlCounts = new Dictionary<string, int>();
        foreach (var step in Enum.GetValues<ProfileEditorStep>())
        {
            Measure("Step." + step, () => vm.GoToEditorStepCommand.Execute(step));
            stepControlCounts[step.ToString()] = CountVisuals(page);
        }
        vm.GoToEditorStepCommand.Execute(ProfileEditorStep.PathAndFields);
        Pump(Task.Delay(40));
        Measure("FieldCaptionTyping20Columns", () =>
        {
            // This exercises the production binding target/notifications and rendered cards,
            // without adding native input's deliberate stabilization waits to the timings.
            for (var i = 0; i < 40; i++)
            {
                vm.DraftFields[0].Header = "Caption " + i;
                Pump(Task.Delay(5));
            }
        });
        Measure("FieldTypeChanges20Columns", () =>
        {
            for (var i = 0; i < 12; i++)
            {
                vm.DraftFields[0].Kind = i % 2 == 0
                    ? FindEverything.Profile.Runtime.ProfileFieldValueKind.DateTime
                    : FindEverything.Profile.Runtime.ProfileFieldValueKind.String;
                Pump(Task.Delay(5));
            }
        });
        Measure("TemplateTyping20Columns", () =>
        {
            for (var i = 0; i < 30; i++)
            {
                vm.DraftPathTemplate = $"C:/sample-{i}/{{name@name}}";
                Pump(Task.Delay(5));
            }
        });
        Pump(Task.Delay(400));
        for (var i = 0; i < 5; i++)
        {
            navigation.Navigate(typeof(HomePage));
            Pump(Task.Delay(30));
            Measure("ProfilesReentry", () => navigation.Navigate(typeof(ProfilesPage)));
        }
        vm.DiscardChanges();
        Pump(vm.NewDraftCommand.ExecuteAsync(null));
        navigation.Navigate(typeof(HomePage));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"performance-pages-{label}.json"),
            JsonSerializer.Serialize(new
            {
                Label = label, Runtime = Environment.Version.ToString(), FieldCount = 20,
                Workload = "Production shell/navigation/profile cards; bound edits; no disk scan",
                Phases = phases, StepVisualCounts = stepControlCounts,
            }, new JsonSerializerOptions { WriteIndented = true }));

        void Measure(string name, Action action)
        {
            var samples = new List<double>();
            var previous = Stopwatch.GetTimestamp();
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(8) };
            timer.Tick += (_, _) =>
            {
                var now = Stopwatch.GetTimestamp();
                samples.Add(Math.Max(0, Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds - 8));
                previous = now;
            };
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            timer.Start();
            try
            {
                action();
                // Include layout/render dispatch, then drain input heartbeat. The fixed
                // 20 ms floor is identical for both revisions and stated in the report.
                window?.UpdateLayout();
                Pump(Task.Delay(20));
                samples.Sort();
                phases.Add(new(name, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    GC.GetAllocatedBytesForCurrentThread() - allocated,
                    samples.Count == 0 ? 0 : samples[(int)Math.Ceiling(samples.Count * .95) - 1],
                    samples.Count == 0 ? 0 : samples[^1]));
            }
            finally { timer.Stop(); }
        }
    }

    private static int CountVisuals(DependencyObject root)
    {
        var count = 1;
        if (root is not Visual) return count;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            count += CountVisuals(VisualTreeHelper.GetChild(root, i));
        return count;
    }

    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(30) };
            timer.Tick += (_, _) => frame.Continue = false;
            timer.Start();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Send,
                new Action(() => frame.Continue = false)), TaskScheduler.Default);
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
        }
        if (!task.IsCompleted) throw new TimeoutException("Profile responsiveness workload stalled.");
        task.GetAwaiter().GetResult();
    }

    private sealed record Measurement(string Scenario, double ElapsedMilliseconds,
        long UiAllocatedBytes, double HeartbeatDelayP95Milliseconds, double HeartbeatDelayMaximumMilliseconds);
}
