using System.Diagnostics;
using FindEverything.Application.Options;
using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.Hosting;

public interface IApplicationLifetimeController
{
    void Shutdown();
}

public interface IApplicationProcessLauncher
{
    void LaunchReplacement();
}

public sealed record ApplicationLaunchContext(
    string EntryCommandPath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory)
{
    internal const string ManagedLocaleMarkerArgument =
        "--FindEverything:ManagedRestartLocale=true";
    internal const string LocaleArgumentPrefix = "--Localization:CultureName=";

    public static ApplicationLaunchContext Capture(
        string entryCommandPath,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var preservedCount = arguments.Count;
        if (preservedCount >= 2
            && string.Equals(
                arguments[preservedCount - 2],
                ManagedLocaleMarkerArgument,
                StringComparison.Ordinal)
            && arguments[preservedCount - 1].StartsWith(
                LocaleArgumentPrefix,
                StringComparison.OrdinalIgnoreCase)
            && LocalizationOptions.IsSupported(
                arguments[preservedCount - 1][LocaleArgumentPrefix.Length..]))
        {
            preservedCount -= 2;
        }

        return new ApplicationLaunchContext(
            entryCommandPath,
            Array.AsReadOnly(arguments.Take(preservedCount).ToArray()),
            workingDirectory);
    }
}

public interface IApplicationRestartCoordinator
{
    bool IsRestartRequested { get; }

    void RequestRestart();

    void RestartAfterShutdownIfRequested();
}

public sealed class WpfApplicationLifetimeController : IApplicationLifetimeController
{
    public void Shutdown()
    {
        var application = System.Windows.Application.Current
            ?? throw new InvalidOperationException("WPF Application is not available.");
        if (application.Dispatcher.CheckAccess())
        {
            application.Shutdown();
        }
        else
        {
            application.Dispatcher.Invoke(() => application.Shutdown());
        }
    }
}

public sealed class CurrentApplicationProcessLauncher(
    ApplicationLaunchContext launchContext,
    IValidatedSettingsState<LocalizationOptions> localizationSettings) :
    IApplicationProcessLauncher
{
    public void LaunchReplacement()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            throw new InvalidOperationException("The application executable path is unavailable.");
        }

        _ = Process.Start(CreateStartInfo(
            executablePath,
            launchContext,
            localizationSettings.Current.CultureName))
            ?? throw new InvalidOperationException(
                "The replacement application process did not start.");
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executablePath,
        ApplicationLaunchContext launchContext,
        string cultureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(launchContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(launchContext.WorkingDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = launchContext.WorkingDirectory,
            UseShellExecute = true,
        };

        // A normal Windows publish runs through the generated apphost. Keep the
        // DLL entry point as the first argument only for framework-dependent
        // launches that were explicitly started through dotnet.exe.
        if (string.Equals(
                Path.GetFileNameWithoutExtension(executablePath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase)
            && launchContext.EntryCommandPath.EndsWith(
                ".dll",
                StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(launchContext.EntryCommandPath);
        }

        foreach (var argument in launchContext.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Keep this pair at the end. The locale value must be the final command-line
        // setting so a language chosen in the UI wins over the original CLI and
        // FINDEVERYTHING_ environment overrides. Capture removes only this marked
        // pair on the next startup, preventing it from accumulating on every restart.
        startInfo.ArgumentList.Add(ApplicationLaunchContext.ManagedLocaleMarkerArgument);
        startInfo.ArgumentList.Add(
            ApplicationLaunchContext.LocaleArgumentPrefix
            + LocalizationOptions.Normalize(cultureName));

        return startInfo;
    }
}

public sealed class ApplicationRestartCoordinator(
    IApplicationLifetimeController lifetimeController,
    IApplicationProcessLauncher processLauncher,
    ILogger<ApplicationRestartCoordinator> logger) : IApplicationRestartCoordinator
{
    private int _restartRequested;
    private int _restartAttempted;

    public bool IsRestartRequested => Volatile.Read(ref _restartRequested) != 0;

    public void RequestRestart()
    {
        if (Interlocked.Exchange(ref _restartRequested, 1) != 0)
        {
            return;
        }

        try
        {
            lifetimeController.Shutdown();
        }
        catch
        {
            Volatile.Write(ref _restartRequested, 0);
            throw;
        }
    }

    public void RestartAfterShutdownIfRequested()
    {
        if (!IsRestartRequested || Interlocked.Exchange(ref _restartAttempted, 1) != 0)
        {
            return;
        }

        try
        {
            processLauncher.LaunchReplacement();
        }
        catch (Exception exception)
        {
            try
            {
                logger.LogError(
                    exception,
                    "Could not restart the application after changing language.");
            }
            catch (Exception loggingException)
            {
                System.Diagnostics.Trace.TraceError(
                    "Replacement launch failed: {0}{1}Logging failure: {2}",
                    exception,
                    Environment.NewLine,
                    loggingException);
            }

            try
            {
                System.Windows.MessageBox.Show(
                    exception.Message,
                    System.Windows.Application.Current?.TryFindResource(
                        "Loc.Restart.Error.Title") as string
                        ?? "FindEverything 다시 시작 실패",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            catch (Exception messageBoxException)
            {
                System.Diagnostics.Trace.TraceError(
                    "Could not show the restart error: {0}",
                    messageBoxException);
            }
        }
    }
}
