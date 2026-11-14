using System.Runtime.InteropServices;
using AbyssRemote.Host;
using AbyssRemote.Host.App;

// Abyss Remote starts in one of four ways:
//   (no option)     the window with its tray icon           -> App/WindowApp.cs
//   --console       the classic console screen (P, R, Q)    -> App/ConsoleScreen.cs
//   --smoke-test    the HTTPS self-test, then exit          -> SmokeTest.cs
//   --probe-video   the Game mode check, then exit          -> GameMode/VideoProbe.cs
// The first three all sit on top of App/HostRuntime.cs, which owns the server.

LaunchOptions options;
try { options = LaunchOptions.Parse(args); }
catch (ArgumentException ex)
{
    // A mistyped option. Say so in the console that started us, or in a message box when the
    // program was started from a shortcut and there is no console.
    if (ConsoleWindow.AttachToParent()) Console.Error.WriteLine(ex.Message);
    else MessageBox.Show(ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    return 2;
}

// Per-monitor DPI awareness: capture, cursor and SetCursorPos then all use physical pixels,
// and the window stays sharp on monitors with different scaling.
try { NativeDpi.Enable(); } catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }

if (options.UiSnapshotFolder is not null)
{
    ConsoleWindow.AttachToParent();
    return UiSnapshot.Run(options.UiSnapshotFolder);
}
if (!options.SmokeTest && !options.ProbeVideo && !options.ConsoleMode) return WindowApp.Run(options);

// Everything below prints, so it needs a console: the one of whoever started us, or (for
// --console from a shortcut) a new one.
if (!ConsoleWindow.AttachToParent() && options.ConsoleMode) ConsoleWindow.Open();

if (options.ProbeVideo) return AbyssRemote.Host.GameMode.VideoProbe.Run(new DesktopEngine());

await using var runtime = new HostRuntime(HostEnvironment.ForThisPc(consoleLogging: !options.SmokeTest));
if (options.SmokeTest) return await SmokeTest.RunAsync(runtime, options.RenewCert);

var saved = new PreferencesFile(AppInfo.DataDirectory).Load(out string? settingsNotice);
return await ConsoleScreen.RunAsync(runtime, HostRunOptions.From(options, saved), settingsNotice);

internal static class NativeDpi
{
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    internal static void Enable() => SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
}
