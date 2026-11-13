using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AbyssRemote.Host;
using AbyssRemote.Host.Features;

namespace AbyssRemote.Tests;

/// <summary>Monitor previews and screenshots against the real server with a pretend desktop.</summary>
[TestClass]
public sealed class DisplayPreviewTests
{
    private static readonly string[] Pictures = ["/api/v1/displays/0/preview", "/api/v1/displays/0/screenshot"];
    private TestServer _server = null!;

    [TestInitialize]
    public async Task Start() => _server = await TestServer.StartAsync();

    [TestCleanup]
    public async Task Stop() => await _server.DisposeAsync();

    [TestMethod]
    public async Task StrangersGetNoPictures()
    {
        using var stranger = _server.CreateClient();
        foreach (string route in Pictures)
        {
            using var reply = await stranger.Http.GetAsync(route);
            Assert.AreEqual(HttpStatusCode.Unauthorized, reply.StatusCode, route);
        }
        Assert.IsNull(_server.Desktop.LastFrameRequest, "nothing may be captured before the pairing check");
    }

    [TestMethod]
    public async Task PreviewIsASmallJpegOfTheChosenMonitor()
    {
        _server.Desktop.Monitors.Add(new DisplayInfo(1, @"\\.\FAKE2", 1920, 0, 2560, 1440, false));
        using var client = await _server.PairAsync();
        using var reply = await client.Http.GetAsync("/api/v1/displays/1/preview");

        Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);
        Assert.AreEqual("image/jpeg", reply.Content.Headers.ContentType?.MediaType);
        CollectionAssert.AreEqual(FakeDesktop.FakeJpeg, await reply.Content.ReadAsByteArrayAsync());
        Assert.IsNull(reply.Content.Headers.ContentDisposition, "a preview is shown, not saved");
        Assert.IsTrue(reply.Headers.CacheControl?.NoStore, "a preview must never come from a cache");
        Assert.AreEqual((1, DisplayPreviewEndpoints.DefaultPreviewWidth, DisplayPreviewEndpoints.PreviewQuality), _server.Desktop.LastFrameRequest);
    }

    [TestMethod]
    [DataRow("?width=320", 320)]
    [DataRow("?width=160", 160)]
    [DataRow("?width=10", 160)]      // smaller than any picker needs
    [DataRow("?width=-5", 160)]
    [DataRow("?width=960", 960)]
    [DataRow("?width=100000", 960)]  // a preview is never a full-size capture
    [DataRow("?width=wide", 480)]
    [DataRow("", 480)]
    public async Task PreviewWidthStaysWithinLimits(string query, int expectedWidth)
    {
        using var client = await _server.PairAsync();
        using var reply = await client.Http.GetAsync("/api/v1/displays/0/preview" + query);

        Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);
        Assert.AreEqual(expectedWidth, _server.Desktop.LastFrameRequest?.MaxWidth);
    }

    [TestMethod]
    public async Task ScreenshotIsAFullSizeJpegToSave()
    {
        using var client = await _server.PairAsync();
        using var reply = await client.Http.GetAsync("/api/v1/displays/0/screenshot");

        Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);
        Assert.AreEqual("image/jpeg", reply.Content.Headers.ContentType?.MediaType);
        CollectionAssert.AreEqual(FakeDesktop.FakeJpeg, await reply.Content.ReadAsByteArrayAsync());

        var disposition = reply.Content.Headers.ContentDisposition;
        Assert.AreEqual("attachment", disposition?.DispositionType);
        StringAssert.Matches(disposition!.FileName!.Trim('"'), new Regex(@"^Abyss Remote \d{4}-\d\d-\d\d \d\d\.\d\d\.\d\d\.jpg$"));

        var (display, maxWidth, quality) = _server.Desktop.LastFrameRequest!.Value;
        Assert.AreEqual(0, display);
        Assert.IsTrue(maxWidth >= 7680, "wide enough that no monitor is scaled down");
        Assert.AreEqual(DisplayPreviewEndpoints.ScreenshotQuality, quality);
    }

    [TestMethod]
    [DataRow("1")]   // this pretend PC has one monitor, number 0
    [DataRow("64")]
    [DataRow("-1")]
    [DataRow("99999999999")]
    [DataRow("abc")]
    [DataRow("0x0")]
    [DataRow("0.5")]
    public async Task UnknownMonitorsAreNotFound(string id)
    {
        using var client = await _server.PairAsync();
        foreach (string kind in new[] { "preview", "screenshot" })
        {
            using var reply = await client.Http.GetAsync($"/api/v1/displays/{id}/{kind}");
            Assert.AreEqual(HttpStatusCode.NotFound, reply.StatusCode, kind);
        }
        Assert.IsNull(_server.Desktop.LastFrameRequest);
    }

    [TestMethod]
    public async Task ALockedPcAnswers503WithTheReason()
    {
        using var client = await _server.PairAsync();
        _server.Desktop.OnCapture = _ => throw new DesktopUnavailableException("The PC is locked.");
        foreach (string route in Pictures)
        {
            using var reply = await client.Http.GetAsync(route);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, reply.StatusCode, route);
            Assert.AreEqual("The PC is locked.", (await reply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }

        // Unlocked again: the failed captures must not have left the next one waiting.
        _server.Desktop.OnCapture = _ => FakeDesktop.FakeJpeg;
        using var after = await client.Http.GetAsync(Pictures[0]);
        Assert.AreEqual(HttpStatusCode.OK, after.StatusCode);
    }

    [TestMethod]
    public async Task CapturesNeverOverlap()
    {
        int running = 0, overlaps = 0;
        _server.Desktop.OnCapture = _ =>
        {
            if (Interlocked.Increment(ref running) > 1) Interlocked.Increment(ref overlaps);
            Thread.Sleep(15); // long enough for another request to arrive
            Interlocked.Decrement(ref running);
            return FakeDesktop.FakeJpeg;
        };
        using var client = await _server.PairAsync();

        var replies = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => client.Http.GetAsync(Pictures[i % 2])));
        foreach (var reply in replies)
        {
            Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);
            reply.Dispose();
        }
        Assert.AreEqual(0, overlaps, "each capture needs two full copies of a monitor in memory");
    }

    [TestMethod]
    public async Task TheMonitorListCarriesNameAndRefreshRate()
    {
        _server.Desktop.Monitors[0] = _server.Desktop.Monitors[0] with { FriendlyName = "Test Monitor 27", RefreshHz = 144 };
        using var client = await _server.PairAsync();
        var monitor = (await client.Http.GetFromJsonAsync<JsonElement>("/api/v1/displays"))[0];

        Assert.AreEqual("Test Monitor 27", monitor.GetProperty("friendlyName").GetString());
        Assert.AreEqual(144, monitor.GetProperty("refreshHz").GetInt32());
        // What older pages read is still there, unchanged.
        Assert.AreEqual(0, monitor.GetProperty("id").GetInt32());
        Assert.AreEqual(@"\\.\FAKE1", monitor.GetProperty("name").GetString());
        Assert.AreEqual(1920, monitor.GetProperty("width").GetInt32());
        Assert.AreEqual(1080, monitor.GetProperty("height").GetInt32());
        Assert.IsTrue(monitor.GetProperty("primary").GetBoolean());
    }
}

/// <summary>The Windows side of the monitor list. These run against whatever screens the
/// machine has (possibly none on a build server) and only read.</summary>
[TestClass]
public sealed class MonitorDetailsTests
{
    [TestMethod]
    public void StructsMatchTheSizesInTheWindowsHeader()
    {
        // sizeof() of the wingdi.h structs. A wrong size would make Windows write past a buffer.
        Assert.AreEqual(72, Unsafe.SizeOf<MonitorCatalog.DisplayConfigPath>());
        Assert.AreEqual(84, Unsafe.SizeOf<MonitorCatalog.DisplayConfigSourceName>());
        Assert.AreEqual(420, Unsafe.SizeOf<MonitorCatalog.DisplayConfigTargetName>());
    }

    [TestMethod]
    public void EveryRealMonitorHasANameAndKeepsItsNumber()
    {
        var displays = new DesktopEngine().Displays();
        for (int i = 0; i < displays.Count; i++)
        {
            Assert.AreEqual(i, displays[i].Id);
            StringAssert.StartsWith(displays[i].Name, @"\\.\");
            Assert.IsFalse(string.IsNullOrWhiteSpace(displays[i].FriendlyName), "a monitor without a model name is called Monitor N");
            Assert.IsTrue(displays[i].Width > 0 && displays[i].Height > 0);
            Assert.IsTrue(displays[i].RefreshHz is >= 0 and <= 1000, $"{displays[i].RefreshHz} Hz");
        }
        Assert.IsTrue(displays.Count == 0 || displays.Count(d => d.Primary) == 1);
    }

    [TestMethod]
    public void DetailsAreOnlyKnownForRealDeviceNames()
    {
        foreach (var (device, details) in MonitorCatalog.Read())
        {
            StringAssert.StartsWith(device, @"\\.\DISPLAY");
            Assert.IsFalse(details.FriendlyName.Any(char.IsControl));
            Assert.IsTrue(details.FriendlyName.Length <= 60);
        }
    }
}
