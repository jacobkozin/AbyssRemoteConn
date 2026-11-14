using System.Runtime.InteropServices;

namespace AbyssRemote.Host.App;

/// <summary>AbyssRemote.exe is a windowed program, so Windows starts it without a console and
/// anything it prints goes nowhere. The modes that do print (--smoke-test, --probe-video,
/// --console, and the message for a mistyped option) call this first.
///
/// Call it before the first use of <see cref="Console"/>: .NET looks up the output handles the
/// first time something is written.</summary>
internal static class ConsoleWindow
{
    /// <summary>Connects to the console of whoever started the program (cmd, PowerShell, a
    /// script). Returns true when there is somewhere to print: that console, or a file or pipe
    /// the output was redirected to.
    ///
    /// Output that was redirected keeps going where it was sent: Windows only points a
    /// program's output at the newly attached console when the starter did not pass any.</summary>
    public static bool AttachToParent()
    {
        bool hasConsole = AttachConsole(ParentProcess)
            || Marshal.GetLastWin32Error() == AlreadyHasConsole; // started through dotnet.exe, a console program
        return hasConsole || OutputIsRedirected();
    }

    /// <summary>Opens a console window of our own, for --console started from a shortcut.</summary>
    public static void Open()
    {
        if (AllocConsole()) Console.Title = AppInfo.Name;
    }

    private static bool OutputIsRedirected()
    {
        IntPtr handle = GetStdHandle(StandardOutput);
        return handle != IntPtr.Zero && handle != new IntPtr(-1) && GetFileType(handle) != UnknownFileType;
    }

    private const int ParentProcess = -1;      // ATTACH_PARENT_PROCESS
    private const int AlreadyHasConsole = 5;   // ERROR_ACCESS_DENIED from AttachConsole
    private const int StandardOutput = -11;    // STD_OUTPUT_HANDLE
    private const uint UnknownFileType = 0;    // FILE_TYPE_UNKNOWN

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);
}
