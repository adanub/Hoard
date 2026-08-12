// Hoard — a local archive for your Pinterest boards.
// Copyright (C) 2026 Hoard contributors
//
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of the
// Licence, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
// even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
// General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with this program. If not,
// see <https://www.gnu.org/licenses/>.

using Avalonia;
using System;
using System.Runtime.InteropServices;
using Velopack;

namespace Hoard.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // MUST be the very first thing that runs. The installer/updater re-launches this same exe with
        // hook arguments (--veloapp-install, --veloapp-updated, …); VelopackApp handles those and exits
        // the process. Anything before it would run during every install/update hook, and anything that
        // opens a window would flash one. Inert (returns immediately) for a normal launch, and for a
        // portable/zip/`dotnet run` build that Velopack never installed.
        //
        // Guarded, because being first also means being BEFORE Serilog and the unhandled-exception hook
        // (App.OnFrameworkInitializationCompleted): a throw here — corrupt install metadata, an unreadable
        // app directory — would otherwise kill the process with no window and nothing in hoard.log. Same
        // principle UpdateService holds to: the updater must never be the reason the app won't start. The
        // exception is stashed and logged once the logger exists.
        try
        {
            VelopackApp.Build().Run();
        }
        catch (Exception ex)
        {
            App.StartupUpdaterError = ex;
        }

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
