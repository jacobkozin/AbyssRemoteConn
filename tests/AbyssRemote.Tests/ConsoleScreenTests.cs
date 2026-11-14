using AbyssRemote.Host;
using AbyssRemote.Host.App;

namespace AbyssRemote.Tests;

/// <summary>The classic console screen (--console), run on a test runtime with the console
/// output captured. The test process has no keyboard, so the keys P, R and Q are not pressed
/// here; what they call (new code, revoke all, stop) is covered by the runtime tests.</summary>
[TestClass]
public sealed class ConsoleScreenTests
{
    private TextWriter _realOut = null!, _realError = null!;
    private StringWriter _out = null!, _error = null!;

    [TestInitialize]
    public void CaptureTheConsole()
    {
        (_realOut, _realError) = (Console.Out, Console.Error);
        (_out, _error) = (new StringWriter(), new StringWriter());
        Console.SetOut(TextWriter.Synchronized(_out));
        Console.SetError(TextWriter.Synchronized(_error));
    }

    [TestCleanup]
    public void GiveTheConsoleBack()
    {
        Console.SetOut(_realOut);
        Console.SetError(_realError);
    }

    [TestMethod]
    public async Task TheBannerComesFirstThenTheNoticesAndStoppingEndsIt()
    {
        await using var host = new TestRuntime();
        new StateStore(host.DataDirectory).Pair("Old laptop"); // so there is something to revoke later

        var screen = ConsoleScreen.RunAsync(host.Runtime, TestRuntime.AnyFreePort, settingsNotice: "A note about the settings file.");
        await Wait.UntilAsync(() => _out.ToString().Contains("Never port-forward"), timeoutMs: 15_000);
        host.Runtime.RevokeAllDevices();
        await Wait.UntilAsync(() => _out.ToString().Contains("Revoked 1 paired device(s)"));
        await host.Runtime.StopAsync(); // what the Q key does

        Assert.AreEqual(0, await screen);
        string text = _out.ToString();
        StringAssert.Contains(text, $"ABYSS REMOTE {AppInfo.Version}");
        StringAssert.Contains(text, "Mode        : This PC only (loopback)");
        StringAssert.Contains(text, "Open        : https://127.0.0.1:");
        StringAssert.Contains(text, "Keys        : P = new pairing code   R = revoke ALL devices   Q = quit");
        string codeLine = text.Split('\n').Single(line => line.StartsWith("Pair code   :"));
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(codeLine, @"^Pair code   : \d{8}   \(valid 10 minutes, works once\)\s*$"));
        Assert.IsTrue(text.IndexOf("Never port-forward", StringComparison.Ordinal) < text.IndexOf("A note about the settings file.", StringComparison.Ordinal),
            "notices follow the banner");
        Assert.AreEqual("", _error.ToString());
    }

    [TestMethod]
    public async Task APortThatIsTakenEndsWithExitCodeOneAndAHint()
    {
        await using var first = new TestRuntime();
        await using var second = new TestRuntime();
        await first.Runtime.StartAsync(TestRuntime.AnyFreePort);
        int port = first.Runtime.Status.Running!.Port;

        int exitCode = await ConsoleScreen.RunAsync(second.Runtime, new HostRunOptions(Lan: false, port), settingsNotice: null);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(_error.ToString(), $"Port {port} is already used by another program.");
        StringAssert.Contains(_error.ToString(), "START-LOCAL-ONLY.bat --port 8555");
        Assert.IsFalse(_out.ToString().Contains("ABYSS REMOTE"), "no banner for a server that did not start");
    }
}
