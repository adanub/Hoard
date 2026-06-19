using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Hoard.Core;
using Hoard.Core.Projects;
using Hoard.Ingest.GalleryDl;
using Hoard.Desktop.ViewModels;
using Hoard.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.IO;

namespace Hoard.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var appPaths = AppPaths.Default();

        // Logs go to BOTH the terminal (live, for the user) and a rolling hoard.log (for tooling).
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(appPaths.LogsRoot, "hoard.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("Hoard starting. App data: {AppData}", appPaths.AppDataRoot);

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: true));
        services.AddSingleton<Hoard.Core.Storage.IFileRecycler, Services.WindowsFileRecycler>(); // delete → recycle bin
        services.AddHoardCore(appPaths);
        services.AddGalleryDlConnectors(ResolveGalleryDlPath()); // desktop-only ingestion
        services.AddTransient<MainWindowViewModel>();
        var provider = services.BuildServiceProvider();

        // The shell opens on the project launcher; the DB is created when a project is opened there.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Dev-only: HOARD_GALLERY=1 opens the design-system gallery instead of the app (see DESIGN.md).
            desktop.MainWindow = Environment.GetEnvironmentVariable("HOARD_GALLERY") == "1"
                ? new GalleryWindow()
                : new MainWindow
                {
                    DataContext = provider.GetRequiredService<MainWindowViewModel>(),
                };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Prefer the bundled gallery-dl next to the app; fall back to one on PATH.</summary>
    private static string ResolveGalleryDlPath()
    {
        var exe = OperatingSystem.IsWindows() ? "gallery-dl.exe" : "gallery-dl";
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "gallery-dl", exe);
        return File.Exists(bundled) ? bundled : "gallery-dl";
    }
}
