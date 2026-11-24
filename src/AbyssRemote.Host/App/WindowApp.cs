using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;

namespace AbyssRemote.Host.App;

/// <summary>The windowed program: it connects the <see cref="HostRuntime"/> (the server), the
/// <see cref="MainWindow"/>, the <see cref="TrayIcon"/> and the saved settings. The runtime
/// reports changes from its own threads; everything here runs on the window's thread.</summary>
internal sealed class WindowApp : IWindowActions
{
    /// <summary>What the app takes from the PC. The program uses <see cref="ForThisPc"/>; a test
    /// passes a temporary folder, throwaway certificates, a fake registry and no tray icon.</summary>
    internal sealed record WindowAppSetup(HostEnvironment Environment, string SettingsDirectory, IRunKey RunKey, bool ShowTrayIcon)
    {
        public static WindowAppSetup ForThisPc() =>
            new(HostEnvironment.ForThisPc(consoleLogging: false), AppInfo.DataDirectory, new WindowsRunKey(), ShowTrayIcon: true);
    }

    private readonly SynchronizationContext _windowThread;
    private readonly HostRuntime _runtime;
    private readonly PreferencesFile _preferencesFile;
    private readonly Autostart _autostart;
    private readonly MainWindow _window;
    private readonly TrayIcon _tray;
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
    private Preferences _preferences;
    private HostRunOptions _options;
    private PhoneSetup _setup = PhoneSetup.NotChecked;
    private DateTime _setupChecked;
    private bool _startWithWindows, _statusSeen, _checkingSetup, _windowClosed, _quitting;

    /// <summary>Runs the window until the user quits. Returns the process exit code.</summary>
    public static int Run(LaunchOptions commandLine)
    {
        // Windows Forms needs a thread of its own kind (a single-threaded apartment). The
        // console modes keep the program's main thread exactly as it always was.
        var thread = new Thread(() => RunWindowThread(commandLine)) { Name = "Abyss Remote window" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return 0;
    }

    private static void RunWindowThread(LaunchOptions commandLine)
    {
        using var instance = new SingleInstance();
        if (!instance.IsFirst)
        {
            // Already running: show that window instead of failing on the port. A start with
            // Windows (--minimized) stays quiet.
            if (!commandLine.Minimized) instance.AskFirstToShow();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        var app = new WindowApp(commandLine, WindowAppSetup.ForThisPc());
        // A mistake in the window must not take the server down with it: note it and carry on.
        Application.ThreadException += (_, e) => app._runtime.AddNotice($"Something went wrong in the window: {e.Exception.Message}");
        instance.Listen(() => app.OnWindowThread(app._window.ShowInFront));
        app.Start(startHidden: commandLine.Minimized);
        Application.Run(); // returns after Quit
    }

    /// <summary>Must be created on the thread that then runs the message loop.</summary>
    internal WindowApp(LaunchOptions commandLine, WindowAppSetup setup)
    {
        _windowThread = SynchronizationContext.Current!;
        _preferencesFile = new PreferencesFile(setup.SettingsDirectory);
        _preferences = _preferencesFile.Load(out string? settingsNotice);
        _options = HostRunOptions.From(commandLine, _preferences);
        _runtime = new HostRuntime(setup.Environment);
        _autostart = new Autostart(setup.RunKey, AppInfo.ExecutablePath);
        _startWithWindows = ReadAutostart();

        _window = new MainWindow(this) { CloseToTray = _preferences.CloseToTray };
        _window.HiddenToTray += OnHiddenToTray;
        _window.Activated += (_, _) => CheckSetup(onlyIfStale: true); // coming back from Windows Settings or a UAC prompt
        _window.FormClosed += (_, _) =>
        {
            _windowClosed = true;
            Quit();
        };
        _tray = new TrayIcon(ToggleWindow, _window.ShowInFront, CopyAddress, NewCodeFromTray, RevokeAllDevices, Quit, setup.ShowTrayIcon);

        _runtime.StatusChanged += () => OnWindowThread(OnStatusChanged);
        _runtime.NoticeAdded += _ => OnWindowThread(Refresh);
        _runtime.PairingCodeChanged += () => OnWindowThread(Refresh);
        _runtime.DevicesChanged += () => OnWindowThread(Refresh);
        _runtime.ConnectionsChanged += () => OnWindowThread(Refresh);
        _runtime.DevicePaired += device => OnWindowThread(() =>
            _tray.Tip("New device paired", $"“{device.Name}” can now see and control this PC."));
        _clock.Tick += (_, _) => { if (_window.Visible) Refresh(); }; // the code's countdown, "5 minutes ago"
        if (settingsNotice is not null) _runtime.AddNotice(settingsNotice);
    }

    /// <summary>The server underneath, for whoever needs to watch it.</summary>
    internal HostRuntime Runtime => _runtime;

    /// <summary>Quits as the tray menu's "Quit" does; may be called from any thread.</summary>
    internal void RequestQuit() => OnWindowThread(Quit);

    internal async void Start(bool startHidden)
    {
        Refresh();
        _clock.Start();
        if (!startHidden) _window.ShowInFront();
        var status = await _runtime.StartAsync(_options);
        _options = _options with { RenewCertificate = false }; // --renew-cert applies to the first start only
        // An open window is there to pair a device, so it gets a code. A hidden start (with
        // Windows) makes none: no valid code exists until someone asks for one.
        if (status.Phase == HostPhase.Running && !startHidden) _runtime.NewPairingCode();
    }

    /// <summary>Runs <paramref name="action"/> on the window's thread, from any thread.</summary>
    private void OnWindowThread(Action action) => _windowThread.Post(_ => { if (!_quitting) action(); }, null);

    private void OnStatusChanged()
    {
        _statusSeen = true;
        Refresh();
        // The port may have changed, and with it the firewall answer.
        if (_runtime.Status.Phase is HostPhase.Running or HostPhase.Failed) CheckSetup(onlyIfStale: false);
    }

    /// <summary>Shows the current state in the window and the tray.</summary>
    private void Refresh()
    {
        if (_quitting) return;
        // Until the runtime reports for the first time, show "Starting" with the saved settings.
        var status = _statusSeen ? _runtime.Status : new HostStatus(HostPhase.Starting, _options);
        var devices = _runtime.Devices;
        _window.View.Show(new WindowState(status, _runtime.PairingCode, devices, _runtime.ConnectedDeviceIds, _runtime.Notices,
            _setup, _preferences.CloseToTray, _startWithWindows, DateTimeOffset.UtcNow, _preferences.AwayHost));
        _tray.Show(status, devices.Count);
    }

    /// <summary>Runs the read-only "Phone setup" checks in the background.</summary>
    private void CheckSetup(bool onlyIfStale)
    {
        if (_checkingSetup || (onlyIfStale && DateTime.UtcNow - _setupChecked < TimeSpan.FromSeconds(3))) return;
        _checkingSetup = true;
        int port = CurrentPort;
        Task.Run(() => PhoneSetup.Check(port)).ContinueWith(check => OnWindowThread(() =>
        {
            _checkingSetup = false;
            _setupChecked = DateTime.UtcNow;
            if (check.IsCompletedSuccessfully) _setup = check.Result;
            if (_reportFirewall is { } asked)
            {
                // The firewall helper just finished: say plainly whether its rule is there now.
                _reportFirewall = null;
                _runtime.AddNotice(check.IsCompletedSuccessfully && check.Result.Firewall == FirewallVerdict.Allowed
                    ? $"Windows Firewall now lets devices on your home network reach port {asked}."
                    : $"The firewall rule for port {asked} was not added. The window that opened said why.");
            }
            Refresh();
        }));
    }

    /// <summary>The port the firewall helper was started for, until its result has been reported.</summary>
    private int? _reportFirewall;

    /// <summary>Waits for the firewall helper's window to close, then checks the firewall again,
    /// so the checklist and the activity list show at once whether the rule is really there.</summary>
    private async Task CheckWhenFinishedAsync(Process helper, int port)
    {
        using (helper)
        {
            try { await helper.WaitForExitAsync(); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return; }
        }
        OnWindowThread(() =>
        {
            _reportFirewall = port;
            _checkingSetup = false; // a check that started before the rule existed must not hide this one
            CheckSetup(onlyIfStale: false);
        });
    }

    private int CurrentPort => _runtime.Status.Running?.Port ?? _options.Port;

    // ---- What the user can ask for (IWindowActions) ----

    public void NewPairingCode()
    {
        if (_runtime.Status.Running is not null) _runtime.NewPairingCode();
    }

    public void CopyAddress()
    {
        if (_runtime.Status.Running is not { } running) return;
        try { Clipboard.SetText(running.MainUrl); }
        catch (ExternalException) { _runtime.AddNotice("The clipboard is busy. Copy the address again in a moment."); }
    }

    public void RevokeDevice(PairedDevice device) => _runtime.RevokeDevice(device.Id);

    public void RevokeAllDevices()
    {
        int count = _runtime.Devices.Count;
        if (count == 0) return;
        string message = $"{(count == 1 ? "The paired device is" : $"All {count} paired devices are")} disconnected at once. To use one again, pair it with a new code.";
        if (ConfirmDialog.Ask(_window, "Revoke all devices?", message, "Revoke all", danger: true)) _runtime.RevokeAllDevices();
    }

    public async void TryAgain() => await _runtime.RestartAsync(_options);

    public void SaveCertificate()
    {
        if (_runtime.Status.Running is not { } running) return;
        using var dialog = new SaveFileDialog
        {
            Title = "Save the Abyss Remote root certificate", FileName = "AbyssRemote-RootCA.cer",
            Filter = "Certificate (*.cer)|*.cer", DefaultExt = "cer", OverwritePrompt = true,
        };
        if (dialog.ShowDialog(_window) != DialogResult.OK) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, running.RootCertificate); // the public certificate, never the private key
            _runtime.AddNotice($"Saved the root certificate to {dialog.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _runtime.AddNotice($"The certificate could not be saved: {ex.Message}");
        }
    }

    public void AddFirewallRule()
    {
        string script = Path.Combine(AppContext.BaseDirectory, "ENABLE-LAN-FIREWALL.bat");
        if (!File.Exists(script))
        {
            _runtime.AddNotice("ENABLE-LAN-FIREWALL.bat is missing next to the program. Run it from the project folder instead.");
            return;
        }
        int port = CurrentPort;
        try
        {
            // "runas" makes Windows show its administrator prompt (UAC). The rule is only
            // added if the person at the PC approves that prompt; this program cannot.
            var helper = Process.Start(new ProcessStartInfo(script, port.ToString())
            {
                UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory,
            });
            _runtime.AddNotice($"Asked Windows to add the firewall rule for port {port}. A window shows the result; press a key there to close it.");
            if (helper is not null) _ = CheckWhenFinishedAsync(helper, port);
        }
        catch (Win32Exception)
        {
            _runtime.AddNotice("The firewall rule was not added: the Windows permission prompt was closed.");
        }
    }

    public void OpenNetworkSettings()
    {
        // Only opens the Settings page; changing the network to Private is the user's click.
        try { Process.Start(new ProcessStartInfo("ms-settings:network") { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception) { _runtime.AddNotice("Windows Settings could not be opened. Look under Settings > Network & internet."); }
    }

    public void CheckSetupAgain() => CheckSetup(onlyIfStale: false);

    public async void SetHomeNetwork(bool on)
    {
        if (on && !_preferences.LanExplained)
        {
            const string explanation = "Phones, tablets and computers on your Wi-Fi or wired network will be able to open Abyss Remote; "
                + "each one still needs the pairing code, and nothing becomes reachable from the internet.";
            if (!ConfirmDialog.Ask(_window, "Allow devices on your home network?", explanation, "Turn on")) return;
        }
        Save(_preferences with { Lan = on, LanExplained = _preferences.LanExplained || on });
        _options = _options with { Lan = on };
        await _runtime.RestartAsync(_options);
    }

    public async void SetPort(int port)
    {
        Save(_preferences with { Port = port });
        _options = _options with { Port = port };
        await _runtime.RestartAsync(_options);
    }

    /// <summary>"Let added devices connect away from home". Needs the away address first, and
    /// says once, plainly, what turning it on means.</summary>
    public async void SetAway(bool on)
    {
        if (on && !AwayAccess.IsValidHost(_preferences.AwayHost))
        {
            _runtime.AddNotice("Type the away address first (for example remote.example.duckdns.org) and click Apply.");
            Refresh();
            return;
        }
        if (on && !_preferences.AwayExplained)
        {
            string explanation = $"Devices you have already added will be able to use Abyss Remote from anywhere at https://{_preferences.AwayHost}, "
                + "through the Gaming Hub on this PC. That address is on the internet: whoever has one of those devices can control this PC "
                + "from anywhere until you revoke it. Each device is set up once at home, with “Set up away access” on its Abyss Remote home screen, "
                + "and new devices can still only be added at home.";
            if (!ConfirmDialog.Ask(_window, "Let added devices connect away from home?", explanation, "Turn on")) return;
        }
        Save(_preferences with { Away = on, AwayExplained = _preferences.AwayExplained || on });
        _options = _options with { AwayHost = on ? _preferences.AwayHost.Trim().ToLowerInvariant() : null };
        await _runtime.RestartAsync(_options);
    }

    /// <summary>Saves the away address. While away access is on, the server restarts with it.</summary>
    public async void SetAwayHost(string host)
    {
        host = host.Trim().ToLowerInvariant();
        if (!AwayAccess.IsValidHost(host)) return;
        Save(_preferences with { AwayHost = host });
        if (!_preferences.Away)
        {
            Refresh();
            return;
        }
        _options = _options with { AwayHost = host };
        await _runtime.RestartAsync(_options);
    }

    public void SetStartWithWindows(bool on)
    {
        try
        {
            if (on) _autostart.Enable();
            else _autostart.Disable();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            _runtime.AddNotice($"“Start with Windows” could not be changed: {ex.Message}");
        }
        _startWithWindows = ReadAutostart();
        Refresh();
    }

    public void SetCloseToTray(bool on)
    {
        Save(_preferences with { CloseToTray = on });
        _window.CloseToTray = on;
        Refresh();
    }

    // ---- Tray and lifetime ----

    private void ToggleWindow()
    {
        if (_window.Visible && _window.WindowState != FormWindowState.Minimized) _window.Hide();
        else _window.ShowInFront();
    }

    private void NewCodeFromTray()
    {
        NewPairingCode();
        _window.ShowInFront(); // a code is only useful where it can be read
    }

    private void OnHiddenToTray()
    {
        if (_preferences.TrayTipShown) return;
        _tray.Tip("Abyss Remote is still running", "It stays in the tray so your devices can connect. Right-click the icon to quit.");
        Save(_preferences with { TrayTipShown = true });
    }

    private void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _clock.Stop();
        _tray.Dispose();
        if (!_windowClosed)
        {
            _window.Quitting = true;
            _window.Close();
        }
        // Give the server a moment to close its connections properly, then leave.
        Task.Run(() => _runtime.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5));
        Application.ExitThread();
    }

    private void Save(Preferences changed)
    {
        _preferences = changed;
        try { _preferencesFile.Save(changed); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _runtime.AddNotice($"The settings could not be saved: {ex.Message}");
        }
    }

    private bool ReadAutostart()
    {
        try { return _autostart.IsEnabled; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException) { return false; }
    }
}
