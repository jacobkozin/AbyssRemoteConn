using System.Reflection;

namespace AbyssRemote.Host;

public static class AppInfo
{
    public const string Name = "Abyss Remote";

    /// <summary>The &lt;Version&gt; from the project file (without any "+commit" suffix).</summary>
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];
}
