using Microsoft.Win32;

namespace AbyssRemote.Host.App;

/// <summary>The list of programs Windows starts when this user signs in
/// (HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run). Behind an interface so
/// the tests use a fake and never write the real registry.</summary>
public interface IRunKey
{
    string? Read(string name);
    void Write(string name, string command);
    void Delete(string name);
}

public sealed class WindowsRunKey : IRunKey
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(name) as string;
    }

    public void Write(string name, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(name, command, RegistryValueKind.String);
    }

    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>"Start with Windows". Nothing is written until the user ticks the box, and
/// unticking removes the entry again.</summary>
public sealed class Autostart(IRunKey runKey, string executablePath)
{
    public const string ValueName = "Abyss Remote";

    /// <summary>Starts hidden in the tray, so signing in does not open a window.</summary>
    public string Command => $"\"{executablePath}\" --minimized";

    /// <summary>True only when the entry starts this copy of the program. An entry left over
    /// from a copy in another folder shows as off, and ticking the box repairs it.</summary>
    public bool IsEnabled => string.Equals(runKey.Read(ValueName), Command, StringComparison.OrdinalIgnoreCase);

    public void Enable() => runKey.Write(ValueName, Command);

    public void Disable() => runKey.Delete(ValueName);
}
