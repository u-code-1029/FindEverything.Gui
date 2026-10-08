using System.Windows;
using FindEverything.Application;
using FindEverything.Application.Options;
using FindEverything.Infrastructure.FindEverything;
using FindEverything.Profile.Runtime;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Hosting;
using FindEverything.Desktop.Localization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FindEverything.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _host = BuildHost(e.Args);
            var localization = _host.Services
                .GetRequiredService<IOptions<LocalizationOptions>>()
                .Value;
            _ = LocalizationBootstrapper.Apply(this, localization);
            await _host.StartAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            var messageFormat = TryFindResource("Loc.Startup.Error.Message") as string
                ?? "애플리케이션을 시작하지 못했습니다.\n\n{0}";
            MessageBox.Show(
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    messageFormat,
                    exception.Message),
                TryFindResource("Loc.Startup.Error.Title") as string
                    ?? "FindEverything 시작 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var restartCoordinator = _host?.Services
            .GetService<IApplicationRestartCoordinator>();
        var logger = _host?.Services.GetService<ILogger<App>>();
        if (_host is not null)
        {
            var timeout = TimeSpan.FromSeconds(10);
            try
            {
                timeout = _host.Services
                    .GetService<IOptions<HostLifecycleOptions>>()?
                    .Value.ShutdownTimeout ?? timeout;

                using var timeoutSource = new CancellationTokenSource(timeout);
                _host.StopAsync(timeoutSource.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // The shutdown deadline is deliberately bounded. Remaining resources
                // are released by disposing the host below.
            }
            catch (Exception exception)
            {
                // A failed operation must not prevent a requested language restart.
                TryLogExitError(
                    logger,
                    exception,
                    "Application shutdown completed with an error.");
            }
            finally
            {
                try
                {
                    _host.Dispose();
                }
                catch (Exception exception)
                {
                    TryLogExitError(
                        logger,
                        exception,
                        "Application host disposal failed.");
                }

                _host = null;
            }
        }

        try
        {
            base.OnExit(e);
        }
        catch (Exception exception)
        {
            TryLogExitError(logger, exception, "WPF application shutdown failed.");
        }
        finally
        {
            restartCoordinator?.RestartAfterShutdownIfRequested();
        }
    }

    private static void TryLogExitError(
        ILogger<App>? logger,
        Exception exception,
        string message)
    {
        try
        {
            logger?.LogError(exception, message);
        }
        catch (Exception loggingException)
        {
            System.Diagnostics.Trace.TraceError(
                "{0}{1}{2}{1}Logging failure: {3}",
                message,
                Environment.NewLine,
                exception,
                loggingException);
        }
    }

    private static IHost BuildHost(string[] args)
    {
        // Capture this before host/configuration setup so relative entry, workspace,
        // database and framework-dependent DLL arguments retain startup semantics.
        var launchWorkingDirectory = Environment.CurrentDirectory;
        var paths = AppPaths.Create();
        Directory.CreateDirectory(paths.LocalDataDirectory);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Configuration precedence is intentional: packaged defaults, mutable user
        // settings, environment variables and finally command-line overrides.
        builder.Configuration.Sources.Clear();
        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            // A dedicated watcher validates a complete candidate before publishing
            // it. The stock JSON provider can throw parse/validation exceptions on
            // its FileSystemWatcher callback thread, so it must not hot-reload here.
            .AddJsonFile(paths.UserSettingsFile, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("FINDEVERYTHING_")
            .AddCommandLine(args);

        builder.Services.AddSingleton(ApplicationLaunchContext.Capture(
            Environment.GetCommandLineArgs().FirstOrDefault()
                ?? Environment.ProcessPath
                ?? string.Empty,
            Array.AsReadOnly(args.ToArray()),
            launchWorkingDirectory));

        builder.Services
            .AddFindEverythingApplication(builder.Configuration)
            .AddFindEverythingInfrastructure()
            .AddProfileRuntime()
            .AddDesktopPresentation(builder.Configuration, paths);

        return builder.Build();
    }
}
