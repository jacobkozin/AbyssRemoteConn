using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using AbyssRemote.Host;

namespace AbyssRemote.Tests;

/// <summary>Not a check of anything: a way to look at the browser client without the real PC.
/// On request it starts the test server, fills its pretend desktop, Steam library and transfer
/// folder, and keeps serving until it is told to stop. Whatever a browser does there reaches
/// the fakes only, so every button can be pressed, Lock, Sleep and Delete included.
///
/// <code>
/// set ABYSS_REMOTE_DEMO=1
/// set ABYSS_REMOTE_DEMO_DIR=C:\some\folder     (optional; default %TEMP%\abyss-remote-demo)
/// dotnet test tests\AbyssRemote.Tests --filter DemoServer
/// </code>
///
/// The folder then holds address.txt (the address, then a pairing code) and recorded.json (what
/// reached the fakes). The certificate is a throwaway, so the browser warns about it once.
/// Creating a file in the folder steers the demo: "stop" ends it, "new-code" writes a fresh
/// pairing code, and while "locked" or "no-steam" exists the pretend PC is locked or has no Steam.</summary>
[TestClass]
public sealed class DemoServer
{
    private const string Switch = "ABYSS_REMOTE_DEMO", FolderSetting = "ABYSS_REMOTE_DEMO_DIR", AwaySetting = "ABYSS_REMOTE_DEMO_AWAY";

    private static readonly object Drawing = new();
    private static readonly ConcurrentDictionary<(int Monitor, int Width), (long Second, byte[] Jpeg)> Frames = new();

    private static readonly (Color From, Color To)[] Palette =
    [
        (Color.FromArgb(14, 52, 96), Color.FromArgb(56, 189, 248)),
        (Color.FromArgb(40, 28, 92), Color.FromArgb(59, 130, 246)),
        (Color.FromArgb(10, 62, 58), Color.FromArgb(52, 211, 153)),
        (Color.FromArgb(88, 32, 54), Color.FromArgb(248, 113, 113)),
        (Color.FromArgb(70, 52, 14), Color.FromArgb(240, 185, 95)),
    ];

    [TestMethod]
    public async Task ServesTheBrowserClientUntilToldToStop()
    {
        if (Environment.GetEnvironmentVariable(Switch) != "1")
            Assert.Inconclusive($"Runs only on request. Set {Switch}=1 (and {FolderSetting} if you like) and run this test " +
                "alone to try the browser client against pretend monitors, games and files.");

        string folder = Directory.CreateDirectory(Environment.GetEnvironmentVariable(FolderSetting)
            ?? Path.Combine(Path.GetTempPath(), "abyss-remote-demo")).FullName;
        string InFolder(string name) => Path.Combine(folder, name);
        bool Asked(string name) => File.Exists(InFolder(name));
        foreach (string leftover in new[] { "stop", "new-code", "locked", "no-steam" }) File.Delete(InFolder(leftover));

        // ABYSS_REMOTE_DEMO_AWAY=remote.example.test turns away access on for that address; put a
        // proxy in front that sends X-Forwarded-For (see tests/README.md).
        string? awayHost = Environment.GetEnvironmentVariable(AwaySetting);
        await using var server = await TestServer.StartAsync(string.IsNullOrWhiteSpace(awayHost) ? null : awayHost.Trim());
        AddMonitors(server.Desktop, locked: () => Asked("locked"));
        AddPointer(server.Desktop);
        AddGames(server.Steam);
        AddFiles(server.Transfer.Directory);
        server.Transfer.MaxUploadBytes = 5 * 1024 * 1024; // small, so "too large" can be tried with a small file
        server.Clipboard.Text = "Copied on the pretend PC";

        void WriteAddress() => File.WriteAllLines(InFolder("address.txt"), [server.Origin + "/", server.Pairing.Rotate()]);
        WriteAddress();

        string written = "";
        while (!Asked("stop"))
        {
            if (Asked("new-code"))
            {
                File.Delete(InFolder("new-code"));
                WriteAddress();
            }
            server.Steam.IsInstalled = !Asked("no-steam");
            string recorded = Recorded(server);
            if (recorded != written) File.WriteAllText(InFolder("recorded.json"), written = recorded);
            await Task.Delay(250);
        }
        File.Delete(InFolder("stop"));
    }

    /// <summary>Three monitors of different shapes. The main one is deliberately not number 0,
    /// and the upright one has no model name, like a monitor Windows knows nothing about.</summary>
    private static void AddMonitors(FakeDesktop desktop, Func<bool> locked)
    {
        desktop.Monitors.Clear();
        desktop.Monitors.Add(new DisplayInfo(0, @"\\.\DISPLAY1", 3840, 360, 2560, 1440, false, "Northlight 27 QHD", 144, Scale: 1.25));
        desktop.Monitors.Add(new DisplayInfo(1, @"\\.\DISPLAY2", 0, 0, 3840, 2160, true, "Aurora 32 4K", 240, Scale: 1.5));
        desktop.Monitors.Add(new DisplayInfo(2, @"\\.\DISPLAY3", -1080, 0, 1080, 1920, false, "Monitor 3", 60));
        // Like the real screen, the pretend one only has a new picture when something changed
        // (its clock, once a second); in between a stream is told "unchanged".
        var shown = new ConcurrentDictionary<(int Monitor, int Width), byte[]>();
        desktop.OnCaptureMonitor = (monitor, maxWidth, forceFullFrame) =>
        {
            if (locked()) throw new DesktopUnavailableException("The PC is locked or showing a secure prompt (UAC). Waiting for the desktop…");
            byte[] picture = MonitorPicture(desktop.Monitors[monitor], maxWidth);
            var key = (monitor, Math.Min(maxWidth, desktop.Monitors[monitor].Width));
            if (!forceFullFrame && shown.TryGetValue(key, out var last) && ReferenceEquals(last, picture)) return null;
            shown[key] = picture;
            return picture;
        };
    }

    /// <summary>Gives the pretend PC a mouse pointer that goes where the browser's "pointer"
    /// messages send it. With it the page draws its own pointer, and zooming in on the pointer
    /// and a zoomed-in view that follows it can be tried.</summary>
    private static void AddPointer(FakeDesktop desktop)
    {
        byte[] picture = PointerPicture();
        desktop.OnInput = command =>
        {
            if (!command.TryGetProperty("kind", out var kind) || kind.GetString() != "pointer") return;
            if (desktop.Monitors.ElementAtOrDefault(command.GetProperty("display").GetInt32()) is not { } monitor) return;
            DemoPointer.MoveTo(monitor.X + (int)(command.GetProperty("x").GetDouble() * monitor.Width),
                               monitor.Y + (int)(command.GetProperty("y").GetDouble() * monitor.Height));
        };
        desktop.OnCreateCursor = () => new DemoPointer(picture);
    }

    /// <summary>One pointer for the pretend PC. Each browser connection gets a reader of its
    /// own, which sends the picture once and the position every time it is asked.</summary>
    private sealed class DemoPointer(byte[] picture) : ICursorSource
    {
        private static int _x = 1920, _y = 1080; // starts in the middle of the main monitor
        private bool _sent;

        public static void MoveTo(int x, int y) { Volatile.Write(ref _x, x); Volatile.Write(ref _y, y); }

        public CursorSnapshot Read()
        {
            var image = _sent ? null : new CursorImage(picture, 1, 1, PointerSize, PointerSize);
            _sent = true;
            return new CursorSnapshot(Volatile.Read(ref _x), Volatile.Read(ref _y), true, image);
        }

        public void Dispose() { }
    }

    private const int PointerSize = 32;

    /// <summary>A white arrow with a dark outline on a clear background, as a PNG.</summary>
    private static byte[] PointerPicture()
    {
        lock (Drawing)
        {
            using var bitmap = new Bitmap(PointerSize, PointerSize, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                PointF[] arrow = [new(1, 1), new(1, 24), new(7, 18), new(12, 29), new(16, 27), new(11, 17), new(19, 17)];
                using var outline = new Pen(Color.FromArgb(20, 24, 32), 2f);
                graphics.FillPolygon(Brushes.White, arrow);
                graphics.DrawPolygon(outline, arrow);
            }
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            return png.ToArray();
        }
    }

    /// <summary>A dozen games with covers and two without, some never played, one with a very
    /// long name and one whose name would be markup if a page pasted it in as HTML.</summary>
    private static void AddGames(FakeSteam steam)
    {
        string[] names =
        [
            "Lantern Bay", "Deep Current", "Kelp Forest Racing", "Tags & <b>Brackets</b>", "Salt and Signal",
            "The Extraordinarily Long-Winded Chronicles of the Cartographers' Guild: Definitive Edition",
            "Harbor Lights", "Nine Fathoms", "Pressure Hull", "Tidewater Tactics", "Moonpool", "Brine",
            "Station Thirteen", "Driftwood Kart",
        ];
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (int i = 0; i < names.Length; i++)
        {
            uint id = (uint)(1000 + i * 10);
            steam.AddGame(id, names[i], lastPlayed: i < 9 ? now - i * 86_400L : 0, sizeOnDisk: (i + 1) * 3_500_000_000L);
            if (i is not (3 or 11)) steam.AddCover(id, data: CoverPicture(names[i], i));
        }
    }

    private static void AddFiles(string folder)
    {
        Directory.CreateDirectory(folder);
        Put("Holiday photo.jpg", CoverPicture("Holiday", 2), TimeSpan.FromMinutes(5));
        Put("Meeting notes.txt", Encoding.UTF8.GetBytes("Bring the charger.\n"), TimeSpan.FromHours(26));
        Put("A very long file name that keeps going to show how the list wraps on a narrow phone screen.pdf", new byte[2_400_000], TimeSpan.FromDays(3));
        Put("Budget 2026.xlsx", new byte[48_000], TimeSpan.FromDays(40));

        void Put(string name, byte[] data, TimeSpan age)
        {
            string path = Path.Combine(folder, name);
            File.WriteAllBytes(path, data);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        }
    }

    /// <summary>What a stream or preview of a monitor shows: its name, its size and a clock, on
    /// a colour of its own. Drawn at most once a second per size; a stream asks far more often.</summary>
    private static byte[] MonitorPicture(DisplayInfo monitor, int maxWidth)
    {
        int width = Math.Min(maxWidth, monitor.Width);
        long second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Frames.TryGetValue((monitor.Id, width), out var kept) && kept.Second == second) return kept.Jpeg;

        int height = Math.Max(1, (int)((long)width * monitor.Height / monitor.Width));
        float unit = width / 40f;
        byte[] jpeg = Draw(width, height, monitor.Id, graphics =>
        {
            using var window = new SolidBrush(Color.FromArgb(60, 255, 255, 255));
            using var taskbar = new SolidBrush(Color.FromArgb(170, 10, 14, 23));
            using var large = new Font("Segoe UI", unit * 1.6f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var small = new Font("Segoe UI", unit * 0.95f, FontStyle.Regular, GraphicsUnit.Pixel);
            graphics.FillRectangle(window, unit * 3, unit * 3, width - unit * 6, height * 0.45f);
            graphics.FillRectangle(taskbar, 0, height - unit * 1.8f, width, unit * 1.8f);
            graphics.DrawString(monitor.FriendlyName, large, Brushes.White, unit * 3.8f, unit * 3.8f);
            graphics.DrawString($"{monitor.Width} x {monitor.Height}   {DateTime.Now:HH:mm:ss}", small, Brushes.White, unit * 3.9f, unit * 6.2f);
        });
        Frames[(monitor.Id, width)] = (second, jpeg);
        return jpeg;
    }

    /// <summary>A portrait cover in the shape Steam uses (2:3) with the game's name on it.</summary>
    private static byte[] CoverPicture(string name, int colour) => Draw(300, 450, colour, graphics =>
    {
        using var title = new Font("Segoe UI", 30, FontStyle.Bold, GraphicsUnit.Pixel);
        using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord };
        graphics.DrawString(name, title, Brushes.White, new RectangleF(20, 40, 260, 370), centred);
    });

    private static byte[] Draw(int width, int height, int colour, Action<Graphics> content)
    {
        lock (Drawing) // GDI+ objects must not be used from two threads at once
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var (from, to) = Palette[colour % Palette.Length];
                using var background = new LinearGradientBrush(new Rectangle(0, 0, width, height), from, to, 35f);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.FillRectangle(background, 0, 0, width, height);
                content(graphics);
            }
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Jpeg);
            return stream.ToArray();
        }
    }

    /// <summary>Everything the fakes wrote down, for whoever is trying the client.</summary>
    private static string Recorded(TestServer server) => JsonSerializer.Serialize(new
    {
        actions = server.Actions.Ran,
        steamStarted = server.Steam.Started.Select(start => string.Join(" ", start.ArgumentList)),
        inputMessages = server.Desktop.Commands.Count,
        lastInput = server.Desktop.Commands.TakeLast(5),
        clipboard = server.Clipboard.Text,
        files = Directory.GetFiles(server.Transfer.Directory).Select(Path.GetFileName).Order(),
        notices = server.Notices.TakeLast(12),
    }, new JsonSerializerOptions { WriteIndented = true });
}
