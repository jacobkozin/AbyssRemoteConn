using System.Runtime.InteropServices;

namespace AbyssRemote.Host.App;

// The "Phone setup" checklist in the window. Everything here only READS Windows settings; the
// two things that change something (adding a firewall rule, marking a network Private) are
// left to the user, behind a UAC prompt or in the Windows Settings app.

public enum FirewallVerdict { Allowed, NoRule, Blocked, FirewallOff, Unknown }

public enum NetworkKind { Private, Public, NotConnected, Unknown }

/// <summary>What the checklist shows. <paramref name="NetworkName"/> is the network the
/// verdict is about, when there is one.</summary>
public sealed record PhoneSetup(FirewallVerdict Firewall, NetworkKind Network, string? NetworkName, bool ControllerDriverInstalled)
{
    public static readonly PhoneSetup NotChecked = new(FirewallVerdict.Unknown, NetworkKind.Unknown, null, false);

    /// <summary>Runs all three checks. They take a moment (the firewall has about a thousand
    /// rules), so the window calls this on a background thread.</summary>
    public static PhoneSetup Check(int port)
    {
        FirewallVerdict firewall;
        try
        {
            firewall = WindowsFirewall.PrivateProfileEnabled()
                ? FirewallCheck.Evaluate(WindowsFirewall.InboundRules(), port, Environment.ProcessPath ?? "")
                : FirewallVerdict.FirewallOff;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            firewall = FirewallVerdict.Unknown;
        }

        (NetworkKind Kind, string? Name) network;
        try { network = NetworkCheck.Evaluate(WindowsNetworks.Connected()); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            network = (NetworkKind.Unknown, null);
        }

        return new PhoneSetup(firewall, network.Kind, network.Name, ControllerDriver.IsInstalled());
    }
}

/// <summary>One inbound Windows Firewall rule, reduced to what the check looks at.</summary>
/// <param name="Protocol">6 = TCP, 17 = UDP, 256 = any.</param>
/// <param name="LocalPorts">"8444", "8000-9000,9443", "*", or nothing for "all ports".</param>
/// <param name="Program">The program the rule is limited to, or nothing.</param>
/// <param name="Service">The Windows service the rule is limited to, or nothing.</param>
/// <param name="AppPackage">The Store app the rule is limited to, or nothing.</param>
/// <param name="Profiles">Bit 1 = Domain, bit 2 = Private, bit 4 = Public.</param>
public sealed record FirewallRule(
    string Name, bool Enabled, bool Allow, int Protocol, string? LocalPorts, string? Program, string? Service, string? AppPackage, int Profiles);

public static class FirewallCheck
{
    public const int Tcp = 6, AnyProtocol = 256, PrivateProfile = 2;

    /// <summary>Does Windows Firewall let a device on a Private network reach
    /// <paramref name="port"/> of this program?
    ///
    /// Only two kinds of rule count, because only they clearly mean this program: a rule that
    /// names the port (what ENABLE-LAN-FIREWALL.bat adds), and a rule for this program file
    /// (what Windows adds when its own "allow access?" prompt is answered). Windows also keeps
    /// many rules with no port and no program; those belong to Store apps and do not open
    /// anything for us. A blocking rule for this program wins over everything, as in Windows.</summary>
    public static FirewallVerdict Evaluate(IEnumerable<FirewallRule> inboundRules, int port, string program)
    {
        bool allowed = false;
        foreach (var rule in inboundRules)
        {
            if (!rule.Enabled || (rule.Profiles & PrivateProfile) == 0) continue;
            if (!string.IsNullOrEmpty(rule.Service) || !string.IsNullOrEmpty(rule.AppPackage)) continue;
            if (rule.Protocol != Tcp && rule.Protocol != AnyProtocol) continue;

            bool allPorts = string.IsNullOrEmpty(rule.LocalPorts) || rule.LocalPorts == "*";
            bool namesPort = !allPorts && ListsPort(rule.LocalPorts!, port);
            bool forThisProgram = SameFile(rule.Program, program) && (allPorts || namesPort);
            bool forThisPort = string.IsNullOrEmpty(rule.Program) && namesPort && rule.Protocol == Tcp;

            if (!rule.Allow && forThisProgram) return FirewallVerdict.Blocked;
            if (rule.Allow && (forThisProgram || forThisPort)) allowed = true;
        }
        return allowed ? FirewallVerdict.Allowed : FirewallVerdict.NoRule;
    }

    /// <summary>True when a port list such as "80,443,8000-9000" contains the port. Names that
    /// Windows uses in some rules ("RPC", "IPHTTPS") never match.</summary>
    public static bool ListsPort(string ports, int port)
    {
        foreach (string part in ports.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] range = part.Split('-', 2);
            if (!int.TryParse(range[0], out int first)) continue;
            int last = first;
            if (range.Length == 2 && !int.TryParse(range[1], out last)) continue;
            if (port >= first && port <= last) return true;
        }
        return false;
    }

    private static bool SameFile(string? ruleProgram, string program) =>
        !string.IsNullOrEmpty(ruleProgram) && program.Length > 0
        && string.Equals(Environment.ExpandEnvironmentVariables(ruleProgram), program, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A network this PC is connected to, as Windows classifies it.</summary>
/// <param name="Category">0 = Public, 1 = Private, 2 = a company domain.</param>
public sealed record ConnectedNetwork(string Name, int Category, bool HasInternet);

public static class NetworkCheck
{
    public const int PublicCategory = 0;

    /// <summary>Is the home network marked Private? "The home network" is taken to be the one
    /// with internet access; virtual adapters (a virtual machine switch, for example) show up
    /// as extra networks without it. If any network with internet access is Public the answer
    /// is Public, because Windows then blocks incoming connections on it.</summary>
    public static (NetworkKind Kind, string? Name) Evaluate(IReadOnlyList<ConnectedNetwork> connected)
    {
        if (connected.Count == 0) return (NetworkKind.NotConnected, null);
        var online = connected.Where(n => n.HasInternet).ToList();
        if (online.Count > 0)
        {
            var open = online.FirstOrDefault(n => n.Category == PublicCategory);
            return open is null ? (NetworkKind.Private, online[0].Name) : (NetworkKind.Public, open.Name);
        }
        // No internet anywhere: a network someone marked Private is the best sign of "home".
        var home = connected.FirstOrDefault(n => n.Category != PublicCategory);
        return home is null ? (NetworkKind.Public, connected[0].Name) : (NetworkKind.Private, home.Name);
    }
}

/// <summary>Reads Windows Firewall through its scripting object (the one PowerShell's
/// <c>New-Object -ComObject HNetCfg.FwPolicy2</c> uses). Reading needs no administrator
/// rights. <c>dynamic</c> makes C# call the COM object's properties by name.</summary>
internal static class WindowsFirewall
{
    private const int Inbound = 1, Allow = 1; // NET_FW_RULE_DIR_IN, NET_FW_ACTION_ALLOW

    public static bool PrivateProfileEnabled() => (bool)Policy().FirewallEnabled[FirewallCheck.PrivateProfile];

    public static IReadOnlyList<FirewallRule> InboundRules()
    {
        var rules = new List<FirewallRule>();
        foreach (dynamic rule in Policy().Rules)
        {
            if ((int)rule.Direction != Inbound) continue;
            rules.Add(new FirewallRule(
                Name: (string?)rule.Name ?? "",
                Enabled: (bool)rule.Enabled,
                Allow: (int)rule.Action == Allow,
                Protocol: (int)rule.Protocol,
                LocalPorts: (string?)rule.LocalPorts,
                Program: (string?)rule.ApplicationName,
                Service: (string?)rule.serviceName,
                AppPackage: (string?)rule.LocalAppPackageId,
                Profiles: (int)rule.Profiles));
        }
        return rules;
    }

    private static dynamic Policy()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new COMException("Windows Firewall is not available.");
        return Activator.CreateInstance(type)!;
    }
}

/// <summary>Reads the connected networks from the Windows Network List Manager, which is where
/// the "Private network / Public network" choice in Settings is stored.</summary>
internal static class WindowsNetworks
{
    private static readonly Guid NetworkListManager = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
    private const int ConnectedOnly = 1; // NLM_ENUM_NETWORK_CONNECTED

    public static IReadOnlyList<ConnectedNetwork> Connected()
    {
        var type = Type.GetTypeFromCLSID(NetworkListManager) ?? throw new COMException("The Network List Manager is not available.");
        dynamic manager = Activator.CreateInstance(type)!;
        var networks = new List<ConnectedNetwork>();
        foreach (dynamic network in manager.GetNetworks(ConnectedOnly))
            networks.Add(new ConnectedNetwork((string)network.GetName(), (int)network.GetCategory(), (bool)network.IsConnectedToInternet));
        return networks;
    }
}

/// <summary>The ViGEmBus driver turns a controller connected to the phone into a virtual Xbox
/// controller on this PC. Abyss Remote never installs it; the checklist only reports it.</summary>
internal static class ControllerDriver
{
    public static bool IsInstalled() => GameMode.ViGEmBus.Shared.Bus is not null;
}
