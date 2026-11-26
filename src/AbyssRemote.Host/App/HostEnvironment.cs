using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AbyssRemote.Host.App;

/// <summary>Everything the runtime takes from outside: where its files live, where the
/// certificates come from and which classes talk to Windows. The program uses
/// <see cref="ForThisPc"/>; tests pass a temporary folder, throwaway certificates and fakes,
/// so they can start the real server without touching the real PC.</summary>
public sealed class HostEnvironment
{
    /// <summary>Folder for the paired devices (settings.json) and the exported certificates.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Folder with the browser client (index.html and its scripts).</summary>
    public required string WebRoot { get; init; }

    public required ICertificateSource Certificates { get; init; }
    public required IDesktop Desktop { get; init; }
    public required IClipboard Clipboard { get; init; }

    // The feature modules, with the same defaults as ServerContext.
    public Features.TransferFolder Transfer { get; init; } = Features.TransferFolder.InDownloads();
    public Features.IHostActions Actions { get; init; } = new Features.WindowsHostActions();
    public Features.ISteam Steam { get; init; } = new Features.WindowsSteam();

    /// <summary>This PC's IPv4 addresses, the one a phone should use first.</summary>
    public Func<IReadOnlyList<IPAddress>> NetworkAddresses { get; init; } = LanAddresses.BestFirst;

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Asks whether the away address (a host name) is forwarded to this program right
    /// now. Tests answer for themselves instead of looking for a proxy on this PC.</summary>
    public Func<string, Task<AwayVerdict>> CheckAway { get; init; } = host => AwayProbe.CheckAsync(host);

    /// <summary>The cheap look in between: does the proxy on this PC still listen?</summary>
    public Func<Task<bool>> AwayListening { get; init; } = () => AwayProbe.ListeningAsync();

    /// <summary>Print the web server's warnings and errors to the console (console mode only).</summary>
    public bool ConsoleLogging { get; init; }

    public static HostEnvironment ForThisPc(bool consoleLogging) => new()
    {
        DataDirectory = AppInfo.DataDirectory,
        WebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        Certificates = new WindowsCertificateStore(),
        Desktop = new DesktopEngine(),
        Clipboard = new WindowsClipboard(),
        ConsoleLogging = consoleLogging,
    };
}

public static class LanAddresses
{
    /// <summary>The addresses the server certificate covers, reordered so that the adapter with
    /// a default gateway (the real home network) comes before virtual adapters such as a
    /// virtual machine switch. The first one goes into the QR code.</summary>
    public static IReadOnlyList<IPAddress> BestFirst()
    {
        var routed = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Select(n => n.GetIPProperties())
            .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .SelectMany(p => p.UnicastAddresses.Select(a => a.Address))
            .ToHashSet();
        return Order(HostCertificates.CurrentAddresses(), routed);
    }

    /// <summary>Addresses on an adapter with a gateway first; otherwise the order stays as it was.</summary>
    public static IReadOnlyList<IPAddress> Order(IEnumerable<IPAddress> addresses, IReadOnlySet<IPAddress> onRoutedAdapters) =>
        addresses.OrderByDescending(onRoutedAdapters.Contains).ToList();
}
