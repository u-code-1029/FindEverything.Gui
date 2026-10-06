using System.Windows;
using FindEverything.Application;
using FindEverything.Infrastructure.FindEverything;
using FindEverything.Profile.Runtime;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
            await _host.StartAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"애플리케이션을 시작하지 못했습니다.\n\n{exception.Message}",
                "FindEverything 시작 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
            finally
            {
                _host.Dispose();
                _host = null;
            }
        }

        base.OnExit(e);
    }

    private static IHost BuildHost(string[] args)
    {
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
            .AddJsonFile(paths.UserSettingsFile, optional: true, reloadOnChange: true)
            .AddEnvironmentVariables("FINDEVERYTHING_")
            .AddCommandLine(args);

        builder.Services
            .AddFindEverythingApplication(builder.Configuration)
            .AddFindEverythingInfrastructure()
            .AddProfileRuntime()
            .AddDesktopPresentation(builder.Configuration, paths);

        return builder.Build();
    }
}
