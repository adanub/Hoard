using Avalonia;
using System;
using System.Runtime.InteropServices;

namespace Hoard.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // A WinExe has no console of its own. When launched from a terminal (e.g. `dotnet run`),
        // attach to that terminal so Serilog's console sink shows logs live. No-op when launched
        // with no parent console (double-click).
        if (OperatingSystem.IsWindows())
            AttachConsole(ATTACH_PARENT_PROCESS);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
