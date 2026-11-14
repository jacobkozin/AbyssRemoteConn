using System.Reflection;

namespace AbyssRemote.Host;

public static class AppInfo
{
    public const string Name = "Abyss Remote";

    /// <summary>The name of the program file, as the build publishes it.</summary>
    public const string ExecutableName = "AbyssRemote.exe";

    /// <summary>The &lt;Version&gt; from the project file (without any "+commit" suffix).</summary>
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    /// <summary>%LOCALAPPDATA%\AbyssRemote: paired devices, the exported certificates and the
    /// window's own settings.</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AbyssRemote");

    /// <summary>The program file to start again later (the "Start with Windows" entry). During
    /// development the code runs inside dotnet.exe, so the program file next to the code is
    /// preferred over the running process.</summary>
    public static string ExecutablePath { get; } = FindExecutable();

    private static string FindExecutable()
    {
        string besideCode = Path.Combine(AppContext.BaseDirectory, ExecutableName);
        return File.Exists(besideCode) ? besideCode : Environment.ProcessPath ?? besideCode;
    }
}
