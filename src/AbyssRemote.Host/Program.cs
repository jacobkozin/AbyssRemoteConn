using System.Runtime.InteropServices;
using AbyssRemote.Host;
using Microsoft.AspNetCore.Connections;

LaunchOptions options;
try { options = LaunchOptions.Parse(args); }
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
// The self-test uses a free port picked by Windows, so it never collides with a running host.
if (options.SmokeTest) options = options with { Port = 0, Lan = false };

// Per-monitor DPI awareness: capture, cursor and SetCursorPos then all use physical pixels.
try { NativeDpi.Enable(); } catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }

if (options.ProbeVideo) return AbyssRemote.Host.GameMode.VideoProbe.Run(new DesktopEngine());

string webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (!File.Exists(Path.Combine(webRoot, "index.html")))
{
    Console.Error.WriteLine("The browser UI (wwwroot) is missing next to the executable. Run BUILD-AND-TEST.bat to rebuild.");
    return 1;
}

string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AbyssRemote");
StateStore state;
try { state = new StateStore(dataDir); }
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
int expired = state.PruneExpired();
var (cert, rootCert, certNotice) = HostCertificates.GetOrCreate(state, dataDir, options.RenewCert, options.Lan);

var hosts = new List<string> { "localhost", "127.0.0.1" };
var lanAddresses = HostCertificates.CurrentAddresses().Where(NetworkGuard.IsLocalNetwork).ToList();
if (options.Lan)
{
    hosts.Add(Environment.MachineName);
    hosts.AddRange(HostCertificates.CurrentAddresses().Select(a => a.ToString()));
}

var pairing = new PairingManager();
var sessions = new SessionRegistry();
pairing.CodeLocked += () => Notice("Pairing code disabled after too many wrong guesses. Press P for a new one.");

var app = AbyssServer.Build(new ServerContext
{
    Options = options,
    Certificate = cert,
    State = state,
    Pairing = pairing,
    Protection = new RequestProtection(hosts),
    Sessions = sessions,
    Desktop = new DesktopEngine(),
    Clipboard = new WindowsClipboard(),
    WebRoot = webRoot,
    Notice = Notice,
    ConsoleLogging = !options.SmokeTest,
});

if (options.SmokeTest) return await SmokeTest.RunAsync(app, cert);

try { await app.StartAsync(); }
catch (Exception ex) when (ex is AddressInUseException || ex.InnerException is AddressInUseException)
{
    Console.Error.WriteLine($"Port {options.Port} is already used by another program.");
    Console.Error.WriteLine("Close that program, or start Abyss Remote on another port, for example:  START-LOCAL-ONLY.bat --port 8555");
    return 1;
}

Console.WriteLine();
Console.WriteLine("===============================================");
Console.WriteLine($"  ABYSS REMOTE {AppInfo.Version}");
Console.WriteLine("===============================================");
Console.WriteLine($"Mode        : {(options.Lan ? "LAN - devices on your private network" : "This PC only (loopback)")}");
Console.WriteLine($"Open        : https://127.0.0.1:{options.Port}");
if (options.Lan)
    foreach (var address in lanAddresses) Console.WriteLine($"              https://{address}:{options.Port}");
Console.WriteLine($"Pair code   : {pairing.Rotate()}   (valid 10 minutes, works once)");
Console.WriteLine($"Server cert : SHA-256 {cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)}");
Console.WriteLine($"Root CA     : SHA-256 {rootCert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)}");
Console.WriteLine($"Root CA file: {Path.Combine(dataDir, "AbyssRemote-RootCA.cer")}");
Console.WriteLine("Keys        : P = new pairing code   R = revoke ALL devices   Q = quit");
Console.WriteLine("Never port-forward this host to the internet.");
Console.WriteLine();
if (certNotice is not null) Notice(certNotice);
if (expired > 0) Notice($"Removed {expired} device(s) whose 30-day pairing expired.");

_ = Task.Run(() =>
{
    while (true)
    {
        ConsoleKey key;
        try { key = Console.ReadKey(intercept: true).Key; }
        catch (InvalidOperationException) { return; } // no interactive console (input redirected)
        if (key == ConsoleKey.P) Notice($"New pairing code: {pairing.Rotate()}   (valid 10 minutes, works once)");
        if (key == ConsoleKey.R)
        {
            int count = state.RevokeAll();
            sessions.CloseAll();
            Notice($"Revoked {count} paired device(s) and closed their connections. Press P to pair again.");
        }
        if (key == ConsoleKey.Q)
        {
            _ = app.StopAsync();
            return;
        }
    }
});
await app.WaitForShutdownAsync();
return 0;

static void Notice(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

internal static class NativeDpi
{
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    internal static void Enable() => SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
}
