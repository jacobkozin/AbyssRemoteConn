using System.Runtime.CompilerServices;
using System.Text.Json;
using AbyssRemote.Host.GameMode;

namespace AbyssRemote.Tests;

[TestClass]
public sealed class GamepadTests
{
    private static XusbReport Map(object state) => XusbReport.FromBrowser(JsonSerializer.SerializeToElement(state));

    [TestMethod]
    public void StandardButtonsMapToXboxButtons()
    {
        Assert.AreEqual(XusbReport.A, Map(new { buttons = 1 << 0 }).Buttons);
        Assert.AreEqual(XusbReport.Y, Map(new { buttons = 1 << 3 }).Buttons);
        Assert.AreEqual(XusbReport.Start, Map(new { buttons = 1 << 9 }).Buttons);
        Assert.AreEqual(XusbReport.DPadLeft, Map(new { buttons = 1 << 14 }).Buttons);
        Assert.AreEqual(XusbReport.Guide, Map(new { buttons = 1 << 16 }).Buttons);
        Assert.AreEqual(XusbReport.A | XusbReport.RightShoulder, Map(new { buttons = (1 << 0) | (1 << 5) }).Buttons);
        Assert.AreEqual(0, Map(new { buttons = 1 << 6 }).Buttons, "triggers are analog, not buttons");
    }

    [TestMethod]
    public void SticksAndTriggersAreScaledAndYIsFlipped()
    {
        var report = Map(new { lt = 1.0, rt = 0.5, lx = 1.0, ly = -1.0, rx = -1.0, ry = 0.25 });
        Assert.AreEqual(255, report.LeftTrigger);
        Assert.AreEqual(128, report.RightTrigger);
        Assert.AreEqual(32767, report.ThumbLX);
        Assert.AreEqual(32767, report.ThumbLY, "browser up (-1) is XInput up (+)");
        Assert.AreEqual(-32767, report.ThumbRX);
        Assert.AreEqual(-8192, report.ThumbRY);
    }

    [TestMethod]
    public void HostileValuesAreClampedOrIgnored()
    {
        var report = Map(new { buttons = -1, lt = 50, lx = -9e9, ly = "up", rx = double.MaxValue });
        Assert.AreEqual(0, report.Buttons);
        Assert.AreEqual(255, report.LeftTrigger);
        Assert.AreEqual(-32767, report.ThumbLX);
        Assert.AreEqual(0, report.ThumbLY);
        Assert.AreEqual(32767, report.ThumbRX);
    }

    [TestMethod]
    public void ReportMatchesTheDriversTwelveByteLayout() => Assert.AreEqual(12, Unsafe.SizeOf<XusbReport>());

    [TestMethod]
    public void IoctlCodesMatchTheDriverHeader()
    {
        // CTL_CODE(FILE_DEVICE_BUS_EXTENDER, IOCTL_VIGEM_BASE + n, METHOD_BUFFERED, FILE_WRITE_DATA)
        static uint CtlCode(uint function) => (0x2Au << 16) | (2u << 14) | (function << 2);
        Assert.AreEqual(ViGEmBus.IoctlPlugin, CtlCode(0x801));
        Assert.AreEqual(ViGEmBus.IoctlUnplug, CtlCode(0x802));
        Assert.AreEqual(ViGEmBus.IoctlCheckVersion, CtlCode(0x803));
        Assert.AreEqual(ViGEmBus.IoctlWaitDeviceReady, CtlCode(0x804));
        Assert.AreEqual(ViGEmBus.IoctlXusbSubmitReport, CtlCode(0x801 + 0x201));
    }
}
