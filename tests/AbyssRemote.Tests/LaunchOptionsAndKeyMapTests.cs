using AbyssRemote.Host;

namespace AbyssRemote.Tests;

[TestClass]
public sealed class LaunchOptionsTests
{
    [TestMethod]
    public void DefaultsAreLocalOnlyOnTheDefaultPort()
    {
        var options = LaunchOptions.Parse([]);
        Assert.IsFalse(options.Lan);
        Assert.AreEqual(LaunchOptions.DefaultPort, options.Port);
    }

    [TestMethod]
    public void LanAndPortAreExplicit()
    {
        var options = LaunchOptions.Parse(["--LAN", "--port", "9000"]);
        Assert.IsTrue(options.Lan);
        Assert.AreEqual(9000, options.Port);
    }

    [TestMethod]
    [DataRow("--port")]
    [DataRow("--port", "80")]
    [DataRow("--port", "70000")]
    [DataRow("--port", "abc")]
    [DataRow("--lam")] // typo must not silently start in some other mode
    [DataRow("--lan", "--local")] // contradicts itself
    [DataRow("--ui-snapshot")]
    [DataRow("--ui-snapshot", "--lan")]
    public void BadOptionsAreErrors(params string[] args) =>
        Assert.ThrowsExactly<ArgumentException>(() => LaunchOptions.Parse(args));

    [TestMethod]
    public void TheWindowIsTheDefaultAndConsoleModeIsExplicit()
    {
        var plain = LaunchOptions.Parse([]);
        Assert.IsFalse(plain.ConsoleMode || plain.Minimized || plain.LocalOnly || plain.PortGiven || plain.SmokeTest || plain.ProbeVideo);
        Assert.IsNull(plain.UiSnapshotFolder);

        Assert.IsTrue(LaunchOptions.Parse(["--console"]).ConsoleMode);
        Assert.IsTrue(LaunchOptions.Parse(["--MINIMIZED"]).Minimized, "what \"Start with Windows\" passes");
        Assert.IsTrue(LaunchOptions.Parse(["--local"]).LocalOnly);
        Assert.AreEqual(@"C:\Temp\shots", LaunchOptions.Parse(["--ui-snapshot", @"C:\Temp\shots"]).UiSnapshotFolder);
    }

    [TestMethod]
    public void AnExplicitPortIsToldApartFromTheDefault()
    {
        Assert.IsFalse(LaunchOptions.Parse(["--lan"]).PortGiven);
        var given = LaunchOptions.Parse(["--port", "8444"]);
        Assert.IsTrue(given.PortGiven, "--port 8444 overrides a saved port even though 8444 is the default");
        Assert.AreEqual(8444, given.Port);
    }

    [TestMethod]
    public void TheOlderOptionsStillParseAsBefore()
    {
        var options = LaunchOptions.Parse(["--lan", "--port", "8555", "--renew-cert", "--smoke-test", "--probe-video"]);
        Assert.AreEqual(new LaunchOptions(Lan: true, Port: 8555, RenewCert: true, SmokeTest: true, ProbeVideo: true) { PortGiven = true }, options);
    }
}

[TestClass]
public sealed class KeyMapTests
{
    [TestMethod]
    [DataRow("KeyA", 0x1E)]
    [DataRow("KeyW", 0x11)]
    [DataRow("Digit1", 0x02)]
    [DataRow("Space", 0x39)]
    [DataRow("ShiftLeft", 0x2A)]
    [DataRow("Numpad4", 0x4B)]
    [DataRow("F12", 0x58)]
    public void PhysicalKeysUseSetOneScanCodes(string code, int expected)
    {
        Assert.IsTrue(KeyMap.TryGetScanCode(code, out ushort scan, out bool extended));
        Assert.AreEqual(expected, (int)scan);
        Assert.IsFalse(extended);
    }

    [TestMethod]
    [DataRow("ArrowLeft", 0x4B)] // same base code as Numpad4, told apart by the E0 prefix
    [DataRow("ControlRight", 0x1D)]
    [DataRow("AltRight", 0x38)]
    [DataRow("NumpadEnter", 0x1C)]
    [DataRow("MetaLeft", 0x5B)]
    [DataRow("Delete", 0x53)]
    public void NavigationKeysAreExtended(string code, int expected)
    {
        Assert.IsTrue(KeyMap.TryGetScanCode(code, out ushort scan, out bool extended));
        Assert.AreEqual(expected, (int)scan);
        Assert.IsTrue(extended);
    }

    [TestMethod]
    public void SpecialKeysFallBackToVirtualKeys()
    {
        Assert.IsTrue(KeyMap.TryGetVirtualKey("Pause", out ushort vk, out _));
        Assert.AreEqual(0x13, (int)vk);
        Assert.IsTrue(KeyMap.TryGetVirtualKey("AudioVolumeUp", out vk, out _));
        Assert.AreEqual(0xAF, (int)vk);
    }

    [TestMethod]
    public void UnknownCodesAreIgnored()
    {
        Assert.IsFalse(KeyMap.IsKnown("Unidentified"));
        Assert.IsFalse(KeyMap.IsKnown("keya"));
        Assert.IsFalse(KeyMap.TryGetScanCode("", out _, out _));
    }
}
