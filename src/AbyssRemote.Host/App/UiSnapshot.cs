using System.Drawing.Imaging;

namespace AbyssRemote.Host.App;

/// <summary><c>AbyssRemote.exe --ui-snapshot &lt;folder&gt;</c>: a developer tool. It builds the
/// window with made-up sample data, draws it into PNG files at 100% and 150% scale and exits.
/// Nothing appears on screen, no server is started and no real pairing data is read, so the
/// pictures are safe to share and the layout can be checked without a second monitor.
/// It also writes checks.txt with the real (read-only) "Phone setup" results of this PC.</summary>
internal static class UiSnapshot
{
    private static readonly float[] Scales = [1f, 1.5f];

    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        // Windows Forms controls need a single-threaded apartment, like the real window.
        var thread = new Thread(() => Draw(folder)) { Name = "Abyss Remote snapshot" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        var setup = PhoneSetup.Check(LaunchOptions.DefaultPort);
        File.WriteAllLines(Path.Combine(folder, "checks.txt"),
        [
            $"Firewall rule for port {LaunchOptions.DefaultPort}: {setup.Firewall}",
            $"Network: {setup.Network}",
            $"Controller driver installed: {setup.ControllerDriverInstalled}",
            $"Program file for \"Start with Windows\": {Path.GetFileName(AppInfo.ExecutablePath)}",
        ]);
        Console.WriteLine($"Snapshots written to {Path.GetFullPath(folder)}");
        return 0;
    }

    private static void Draw(string folder)
    {
        Application.EnableVisualStyles();
        var actions = new NoActions();
        foreach (float scale in Scales)
        {
            string suffix = $"@{scale * 100:0}";
            foreach (var (name, state, size) in Samples())
            {
                using var view = new MainView(actions, scale) { Size = new Size((int)(size.Width * scale), (int)(size.Height * scale)) };
                view.Show(state);
                Save(view, Path.Combine(folder, $"window-{name}{suffix}.png"));
            }
            using var question = new ConfirmView("Revoke all devices?",
                "All 3 paired devices are disconnected at once. To use one again, pair it with a new code.", "Revoke all", danger: true, scale);
            Save(question, Path.Combine(folder, $"confirm{suffix}.png"));
        }

        using var tray = new TrayIcon(() => { }, () => { }, () => { }, () => { }, () => { }, () => { }, visible: false);
        tray.Show(Running(lan: true), deviceCount: 3);
        tray.Menu.PerformLayout();
        tray.Menu.Size = tray.Menu.GetPreferredSize(Size.Empty);
        Save(tray.Menu, Path.Combine(folder, "tray-menu.png"));
    }

    private static void Save(Control control, string path)
    {
        control.CreateControl();
        using var bitmap = new Bitmap(control.Width, control.Height, PixelFormat.Format32bppRgb);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
        bitmap.Save(path, ImageFormat.Png);
    }

    /// <summary>The states worth looking at: both modes, a failure, and a narrow window. The
    /// layout tests walk through the same list.</summary>
    internal static IEnumerable<(string Name, WindowState State, Size LogicalSize)> Samples()
    {
        var now = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var code = new PairingCode(PairingCodeState.Active, "12345678", now.AddSeconds(-19), now.AddSeconds(-19) + PairingManager.CodeLifetime);
        PairedDevice[] devices =
        [
            new("a", "Living room iPad", now.AddDays(-3), now.AddMinutes(-1), now.AddDays(27)),
            new("b", "Phone", now.AddDays(-12), now.AddHours(-5), now.AddDays(18)),
            new("c", "Laptop in the study with a rather long name", now.AddHours(-30), now.AddMinutes(-40), now.AddDays(29)),
        ];
        NoticeLine[] notices =
        [
            new(now.AddMinutes(-42), "Removed 1 device(s) whose 30-day pairing expired."),
            new(now.AddMinutes(-40), "Paired a new device: \"Laptop in the study with a rather long name\" from 192.168.1.37."),
            new(now.AddMinutes(-12), "Refused a connection from 203.0.113.9: only devices on your local network may connect."),
            new(now.AddMinutes(-3), "Revoked \"Old tablet\" and closed its connections."),
        ];
        var ready = new PhoneSetup(FirewallVerdict.Allowed, NetworkKind.Private, "Home Wi-Fi", ControllerDriverInstalled: true);
        var notReady = new PhoneSetup(FirewallVerdict.NoRule, NetworkKind.Public, "Network 2", ControllerDriverInstalled: false);
        var connected = new HashSet<string> { "a" };
        var nobody = new HashSet<string>();
        Size normal = new(1100, 940), narrow = new(600, 900);

        yield return ("home-network", new WindowState(Running(lan: true), code, devices, connected, notices, ready, true, true, now), normal);
        yield return ("this-pc", new WindowState(Running(lan: false), code, [], nobody, [], notReady, true, false, now), normal);
        yield return ("code-used", new WindowState(Running(lan: true), code with { State = PairingCodeState.Used, Digits = "" }, devices[..1], nobody, notices[..2], notReady, false, false, now), normal);
        var failed = new HostStatus(HostPhase.Failed, new HostRunOptions(false, 8444),
            Problem: new StartProblem(StartProblemKind.PortInUse, "Port 8444 is already used by another program."));
        yield return ("failed", new WindowState(failed, PairingCode.None, devices[..2], nobody, notices[..1], notReady, true, false, now), normal);
        yield return ("starting", new WindowState(new HostStatus(HostPhase.Starting, new HostRunOptions(false, 8444)), PairingCode.None, [], nobody, [], PhoneSetup.NotChecked, true, false, now), normal);
        yield return ("narrow", new WindowState(Running(lan: true), code, devices, connected, notices, notReady, true, false, now), narrow);
        yield return ("smallest", new WindowState(Running(lan: true), code, devices, connected, notices, ready, true, false, now), new Size(600, 480));
    }

    private static HostStatus Running(bool lan)
    {
        string[] lanUrls = lan ? ["https://192.168.1.20:8444", "https://172.24.96.1:8444"] : [];
        var running = new RunningHost(8444, lan, "https://127.0.0.1:8444", lanUrls,
            ServerFingerprint: new string('0', 64),
            RootFingerprint: "3F9A1C0D72B45E8811AF09C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F607",
            RootCertificate: [], RootCertificateFile: @"C:\Users\you\AppData\Local\AbyssRemote\AbyssRemote-RootCA.cer");
        return new HostStatus(HostPhase.Running, new HostRunOptions(lan, 8444), running);
    }

    /// <summary>The pictures are not clickable.</summary>
    private sealed class NoActions : IWindowActions
    {
        public void NewPairingCode() { }
        public void CopyAddress() { }
        public void RevokeDevice(PairedDevice device) { }
        public void RevokeAllDevices() { }
        public void TryAgain() { }
        public void SaveCertificate() { }
        public void AddFirewallRule() { }
        public void OpenNetworkSettings() { }
        public void CheckSetupAgain() { }
        public void SetHomeNetwork(bool on) { }
        public void SetPort(int port) { }
        public void SetStartWithWindows(bool on) { }
        public void SetCloseToTray(bool on) { }
    }
}
