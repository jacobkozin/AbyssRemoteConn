using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using AbyssRemote.Host;
using AbyssRemote.Host.App;

namespace AbyssRemote.Tests;

/// <summary>Builds the real window contents off screen (nothing is shown) with the sample
/// states of the snapshot tool, and checks the layout at several monitor scales.</summary>
[TestClass]
public sealed class WindowLayoutTests
{
    /// <summary>Windows Forms controls need a single-threaded apartment, which test threads are not.</summary>
    private static void OnWindowThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // A mistake while drawing should fail the test, not open an error dialog.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: true);
                action();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new AssertFailedException(failure.Message, failure);
    }

    [TestMethod]
    [DataRow(1f)]
    [DataRow(1.25f)]
    [DataRow(1.5f)]
    [DataRow(2f)]
    public void NothingIsClippedAndNothingOverlaps(float scale) => OnWindowThread(() =>
    {
        foreach (var (name, state, size) in UiSnapshot.Samples())
        {
            using var view = new MainView(new Ignore(), scale) { Size = new Size((int)(size.Width * scale), (int)(size.Height * scale)) };
            view.Show(state);
            view.CreateControl();
            Check(view, $"{name} at {scale * 100}%");
        }
    });

    [TestMethod]
    public void AWideWindowHasTwoColumnsAndANarrowOneHasOne() => OnWindowThread(() =>
    {
        var (_, state, _) = UiSnapshot.Samples().First();
        using var wide = new MainView(new Ignore(), 1f) { Size = new Size(1100, 940) };
        using var narrow = new MainView(new Ignore(), 1f) { Size = new Size(600, 940) };
        wide.Show(state);
        narrow.Show(state);

        Assert.AreEqual(2, Cards(wide).Select(card => card.Left).Distinct().Count());
        Assert.AreEqual(1, Cards(narrow).Select(card => card.Left).Distinct().Count());
    });

    [TestMethod]
    public void TheSameLayoutComesOutTwiceAsLargeAtTwoHundredPercent() => OnWindowThread(() =>
    {
        var (_, state, size) = UiSnapshot.Samples().First();
        using var normal = new MainView(new Ignore(), 1f) { Size = size };
        using var doubled = new MainView(new Ignore(), 2f) { Size = new Size(size.Width * 2, size.Height * 2) };
        normal.Show(state);
        doubled.Show(state);

        var small = Cards(normal).ToArray();
        var large = Cards(doubled).ToArray();
        Assert.AreEqual(small.Length, large.Length);
        for (int i = 0; i < small.Length; i++)
        {
            // Widths are exact; heights may differ by a few pixels because text is measured
            // in whole pixels at each size.
            Assert.AreEqual(small[i].Width * 2, large[i].Width, 2, small[i].GetType().Name);
            Assert.AreEqual(small[i].Left * 2, large[i].Left, 2, small[i].GetType().Name);
            Assert.AreEqual(small[i].Height * 2, large[i].Height, small[i].Height * 0.06, small[i].GetType().Name);
        }
    });

    [TestMethod]
    public void TheSnapshotToolDrawsEverySampleAtBothScales()
    {
        string folder = Directory.CreateTempSubdirectory("abyss-snapshot-test-").FullName;
        try
        {
            Assert.AreEqual(0, UiSnapshot.Run(folder));

            int samples = UiSnapshot.Samples().Count();
            Assert.AreEqual(samples * 2, Directory.GetFiles(folder, "window-*.png").Length);
            using (var picture = new Bitmap(Path.Combine(folder, "window-home-network@150.png")))
                Assert.AreEqual(new Size(1650, 1650), picture.Size, "1100 x 1100 logical pixels at 150%");
            Assert.IsTrue(File.Exists(Path.Combine(folder, "confirm@100.png")));
            Assert.IsTrue(File.Exists(Path.Combine(folder, "tray-menu.png")));
            Assert.AreEqual(4, File.ReadAllLines(Path.Combine(folder, "checks.txt")).Length);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    /// <summary>A round control cannot be see-through, so it paints what it lies on behind its
    /// corners. Without that, a card would stand in a box of one flat colour on the page, and
    /// a button in a box on its card.</summary>
    [TestMethod]
    [DataRow(1f)]
    [DataRow(1.5f)]
    public void RoundCornersShowWhatIsBehindThem(float scale) => OnWindowThread(() =>
    {
        var (_, state, size) = UiSnapshot.Samples().First();
        using var view = new MainView(new Ignore(), scale) { Size = new Size((int)(size.Width * scale), (int)(size.Height * scale)) };
        view.Show(state);
        view.CreateControl();
        using var picture = new Bitmap(view.Width, view.Height);
        view.DrawToBitmap(picture, new Rectangle(Point.Empty, view.Size));

        var page = view.Controls.OfType<Panel>().Single();
        int buttons = 0;
        foreach (Control card in Cards(view))
        {
            // The card's own corner pixel, and the page two pixels to the left of the card.
            int x = page.Left + card.Left, y = page.Top + card.Top;
            AssertSameColour(picture.GetPixel(x - 2, y), picture.GetPixel(x, y), $"the page behind the corner of {card.GetType().Name}");
            foreach (var button in card.Controls.OfType<RaisedButton>().Where(button => button.Visible))
            {
                int left = x + button.Left, top = y + button.Top;
                AssertSameColour(picture.GetPixel(left - 2, top), picture.GetPixel(left, top), $"the card behind the corner of \"{button.Text}\"");
                buttons++;
            }
        }
        Assert.IsTrue(buttons >= 8, "the sample shows the buttons of every card");

        // And the page is not one flat colour: lighter at the top than at the bottom.
        Color high = picture.GetPixel(4, page.Top + 4), low = picture.GetPixel(4, view.Height - 4);
        Assert.IsTrue(high.G > low.G + 10, $"the page is lit from above ({high} over {low})");
    });

    private static void AssertSameColour(Color expected, Color actual, string what)
    {
        int apart = Math.Max(Math.Abs(expected.R - actual.R), Math.Max(Math.Abs(expected.G - actual.G), Math.Abs(expected.B - actual.B)));
        Assert.IsTrue(apart <= 3, $"{what}: {actual} instead of {expected}");
    }

    /// <summary>The activity list shows a scroll bar only while there is more than fits. The
    /// boxes are real here, so changing the scroll bar has to replace one, and the newest line
    /// must still be in view afterwards.</summary>
    [TestMethod]
    public void TheActivityListHasAScrollBarOnlyWhileItIsNeeded() => OnWindowThread(() =>
    {
        var (_, state, size) = UiSnapshot.Samples().First();
        using var view = new MainView(new Ignore(), 1f) { Size = size };
        view.Show(state);
        view.CreateControl();
        var list = Cards(view).OfType<ActivityCard>().Single().Controls.OfType<TextBox>().Single();
        Assert.AreEqual(ScrollBars.None, list.ScrollBars, "four lines fit");

        var many = Enumerable.Range(0, 80)
            .Select(i => new NoticeLine(state.Now.AddMinutes(i - 80), $"Paired a new device: \"Phone {i}\" from 192.168.1.{i}."))
            .ToArray();
        view.Show(state with { Notices = many });
        Assert.AreEqual(ScrollBars.Vertical, list.ScrollBars, "eighty lines do not fit");
        Assert.IsTrue(SendMessageW(list.Handle, FirstVisibleLine, 0, 0) > 0, "scrolled down to the newest line");
        Check(view, "a long activity list");

        view.Show(state);
        Assert.AreEqual(ScrollBars.None, list.ScrollBars, "four lines fit again");
        Check(view, "a short activity list again");
    });

    [TestMethod]
    public void TheCloseButtonHidesToTheTrayButACloseRequestFromOutsideCloses() => OnWindowThread(() =>
    {
        using var window = new MainWindow(new Ignore());
        int hidden = 0, closed = 0;
        window.HiddenToTray += () => hidden++;
        window.FormClosed += (_, _) => closed++;
        IntPtr handle = window.Handle; // the window exists but is never shown

        SendMessageW(handle, SystemCommand, CloseCommand, 0); // what the X button and Alt+F4 send
        Assert.AreEqual((1, 0), (hidden, closed));
        Assert.IsFalse(window.IsDisposed);

        // The "End task" button of Task Manager asks with a plain close message. It must work
        // even right after a close that was turned into "hide".
        SendMessageW(handle, CloseMessage, 0, 0);
        Assert.AreEqual((1, 1), (hidden, closed));
        Assert.IsTrue(window.IsDisposed);
    });

    [TestMethod]
    public void WithoutCloseToTrayTheCloseButtonCloses() => OnWindowThread(() =>
    {
        using var window = new MainWindow(new Ignore()) { CloseToTray = false };
        int hidden = 0, closed = 0;
        window.HiddenToTray += () => hidden++;
        window.FormClosed += (_, _) => closed++;

        SendMessageW(window.Handle, SystemCommand, CloseCommand, 0);

        Assert.AreEqual((0, 1), (hidden, closed));
    });

    [TestMethod]
    public void QuittingClosesTheWindowEvenWithCloseToTray() => OnWindowThread(() =>
    {
        using var window = new MainWindow(new Ignore());
        int closed = 0;
        window.FormClosed += (_, _) => closed++;
        IntPtr handle = window.Handle;

        window.Quitting = true;
        SendMessageW(handle, SystemCommand, CloseCommand, 0);

        Assert.AreEqual(1, closed);
    });

    /// <summary>The whole windowed app, started the way "Start with Windows" starts it (hidden,
    /// so nothing appears on screen), with a temporary folder, throwaway certificates, a fake
    /// registry and no tray icon. Then it is told to quit, as the tray menu would.</summary>
    [TestMethod]
    public async Task TheAppStartsHiddenServesAndQuitsCleanly()
    {
        string folder = Directory.CreateTempSubdirectory("abyss-window-app-test-").FullName;
        int port = FreePort();
        var runKey = new FakeRunKey();
        var created = new TaskCompletionSource<WindowApp>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: true);
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                var environment = TestRuntime.Environment(folder, new ThrowawayCertificates(), new FakeDesktop());
                var app = new WindowApp(LaunchOptions.Parse(["--port", port.ToString(), "--minimized"]),
                    new WindowApp.WindowAppSetup(environment, folder, runKey, ShowTrayIcon: false));
                app.Start(startHidden: true);
                created.SetResult(app);
                Application.Run(); // returns when the app quits
            }
            catch (Exception ex) { created.TrySetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            var app = await created.Task;
            await Wait.UntilAsync(() => app.Runtime.Status.Phase == HostPhase.Running, timeoutMs: 20_000);
            Assert.AreEqual(port, app.Runtime.Status.Running!.Port);
            Assert.AreEqual(PairingCodeState.None, app.Runtime.PairingCode.State, "a hidden start makes no pairing code");
            Assert.IsTrue(CanConnect(port));

            app.RequestQuit();

            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "the window thread ends after Quit");
            Assert.AreEqual(HostPhase.Stopped, app.Runtime.Status.Phase);
            Assert.IsFalse(CanConnect(port), "the server is gone");
            Assert.IsFalse(File.Exists(Path.Combine(folder, PreferencesFile.FileName)), "nothing is saved until a setting changes");
            Assert.AreEqual(0, runKey.Writes, "the Run key is only written when the box is ticked");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [TestMethod]
    public void AnOpenWindowStartsWithACodeUnlessACodeWouldWorkFromAnywhere()
    {
        var options = new HostRunOptions(Lan: true, Port: 8444);
        RunningHost Running(string? awayUrl) => new(8444, true, "https://127.0.0.1:8444", ["https://192.168.1.20:8444"], "AA", "BB", [], "root.cer", AwayUrl: awayUrl);
        var atHome = new HostStatus(HostPhase.Running, options, Running(null));
        var withAway = new HostStatus(HostPhase.Running, options, Running("https://remote.test.example"));

        Assert.IsTrue(WindowApp.MakesCodeAtStart(atHome, startHidden: false));
        Assert.IsFalse(WindowApp.MakesCodeAtStart(atHome, startHidden: true), "started with Windows: nobody is there to pair");
        Assert.IsFalse(WindowApp.MakesCodeAtStart(withAway, startHidden: false), "with away access on, a code is made on request only");
        Assert.IsFalse(WindowApp.MakesCodeAtStart(new HostStatus(HostPhase.Failed, options), startHidden: false), "nothing to pair with while the server is not running");
    }

    /// <summary>The address the window shows, copies and puts into the QR code.</summary>
    [TestMethod]
    public void TheAwayAddressIsGivenOutOnlyWhileItWorks()
    {
        var home = new RunningHost(8444, true, "https://127.0.0.1:8444", ["https://192.168.1.20:8444"], "AA", "BB", [], "root.cer");
        var away = home with { AwayUrl = "https://remote.test.example" };
        Assert.AreEqual("https://remote.test.example", away.AddressToGiveOut(AwayVerdict.Working));
        foreach (var verdict in new[] { AwayVerdict.Unknown, AwayVerdict.NotAnswering, AwayVerdict.SomethingElse })
            Assert.AreEqual("https://192.168.1.20:8444", away.AddressToGiveOut(verdict), verdict.ToString());
        Assert.AreEqual("https://192.168.1.20:8444", home.AddressToGiveOut(AwayVerdict.Working), "away access is off");
        var thisPcOnly = home with { Lan = false, LanUrls = [], AwayUrl = "https://remote.test.example" };
        Assert.AreEqual("https://remote.test.example", thisPcOnly.AddressToGiveOut(AwayVerdict.Working), "away access does not need home-network mode");
        Assert.AreEqual("https://127.0.0.1:8444", thisPcOnly.AddressToGiveOut(AwayVerdict.NotAnswering));
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool CanConnect(int port)
    {
        using var client = new TcpClient();
        try
        {
            client.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException) { return false; }
    }

    private const uint SystemCommand = 0x0112, CloseMessage = 0x0010; // WM_SYSCOMMAND, WM_CLOSE
    private const nint CloseCommand = 0xF060;                         // SC_CLOSE
    private const uint FirstVisibleLine = 0x00CE;                     // EM_GETFIRSTVISIBLELINE

    [DllImport("user32.dll")] private static extern nint SendMessageW(IntPtr window, uint message, nint wParam, nint lParam);

    [TestMethod]
    public void TimesAreSaidInWords()
    {
        var now = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.AreEqual("just now", RelativeTime.Ago(now.AddSeconds(-30), now));
        Assert.AreEqual("1 minute ago", RelativeTime.Ago(now.AddSeconds(-90), now));
        Assert.AreEqual("59 minutes ago", RelativeTime.Ago(now.AddMinutes(-59), now));
        Assert.AreEqual("1 hour ago", RelativeTime.Ago(now.AddMinutes(-61), now));
        Assert.AreEqual("23 hours ago", RelativeTime.Ago(now.AddHours(-23.5), now));
        Assert.AreEqual("yesterday", RelativeTime.Ago(now.AddHours(-30), now));
        Assert.AreEqual("12 days ago", RelativeTime.Ago(now.AddDays(-12), now));
    }

    private static IEnumerable<Control> Cards(Control view) =>
        view.Controls.OfType<Panel>().Single().Controls.Cast<Control>().Where(card => card.Visible).OrderBy(card => card.TabIndex);

    /// <summary>Every visible control lies inside its parent, and no two visible controls
    /// with the same parent overlap. The scrolling area may be taller than the window (that
    /// is what scrolling is for) but never wider.</summary>
    private static void Check(Control parent, string where)
    {
        var children = parent.Controls.Cast<Control>().Where(child => child.Visible).ToList();
        bool scrolls = parent is Panel { AutoScroll: true };
        foreach (var child in children)
        {
            string what = $"{where}: {child.GetType().Name} \"{child.Text}\" in {parent.GetType().Name}";
            Assert.IsTrue(child.Width > 0 && child.Height > 0, $"{what} has no size");
            Assert.IsTrue(child.Left >= 0 && child.Right <= parent.ClientSize.Width, $"{what} sticks out sideways ({child.Bounds} in {parent.ClientSize})");
            if (!scrolls) Assert.IsTrue(child.Top >= 0 && child.Bottom <= parent.ClientSize.Height, $"{what} sticks out ({child.Bounds} in {parent.ClientSize})");
            Check(child, where);
        }
        for (int i = 0; i < children.Count; i++)
            for (int j = i + 1; j < children.Count; j++)
                Assert.IsFalse(children[i].Bounds.IntersectsWith(children[j].Bounds),
                    $"{where}: \"{children[i].Text}\" and \"{children[j].Text}\" overlap in {parent.GetType().Name}");
    }

    private sealed class Ignore : IWindowActions
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
        public void SetAway(bool on) { }
        public void SetAwayHost(string host) { }
        public void SetStartWithWindows(bool on) { }
        public void SetCloseToTray(bool on) { }
    }
}
