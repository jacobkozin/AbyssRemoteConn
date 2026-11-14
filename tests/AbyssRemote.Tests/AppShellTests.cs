using AbyssRemote.Host;
using AbyssRemote.Host.App;

namespace AbyssRemote.Tests;

[TestClass]
public sealed class PreferencesTests
{
    private string _directory = null!;

    [TestInitialize]
    public void CreateFolder() => _directory = Directory.CreateTempSubdirectory("abyss-preferences-test-").FullName;

    [TestCleanup]
    public void DeleteFolder() => Directory.Delete(_directory, recursive: true);

    private string FilePath => Path.Combine(_directory, PreferencesFile.FileName);

    [TestMethod]
    public void WithoutAFileTheDefaultsAreSafe()
    {
        var loaded = new PreferencesFile(_directory).Load(out string? notice);

        Assert.IsNull(notice);
        Assert.IsFalse(loaded.Lan, "this PC only until the user chooses otherwise");
        Assert.AreEqual(LaunchOptions.DefaultPort, loaded.Port);
        Assert.IsTrue(loaded.CloseToTray);
        Assert.IsFalse(loaded.LanExplained || loaded.TrayTipShown);
        Assert.IsFalse(File.Exists(FilePath), "loading never writes");
    }

    [TestMethod]
    public void SavedSettingsComeBack()
    {
        var file = new PreferencesFile(_directory);
        var saved = new Preferences { Lan = true, Port = 8555, CloseToTray = false, LanExplained = true, TrayTipShown = true };

        file.Save(saved);

        Assert.AreEqual(saved, new PreferencesFile(_directory).Load(out string? notice));
        Assert.IsNull(notice);
        Assert.IsFalse(File.Exists(Path.Combine(_directory, "settings.json")), "that file belongs to the paired devices");
        StringAssert.Contains(File.ReadAllText(FilePath), "\"closeToTray\": false");
    }

    [TestMethod]
    public void SavingReplacesTheFileInOneStepAndLeavesNoTemporaryFile()
    {
        var file = new PreferencesFile(_directory);
        file.Save(new Preferences { Port = 9001 });
        file.Save(new Preferences { Port = 9002 });

        CollectionAssert.AreEqual(new[] { PreferencesFile.FileName }, Directory.GetFiles(_directory).Select(Path.GetFileName).ToArray());
        Assert.AreEqual(9002, file.Load(out _).Port);
    }

    [TestMethod]
    public void SavingCreatesTheFolder()
    {
        string nested = Path.Combine(_directory, "not", "there", "yet");
        new PreferencesFile(nested).Save(new Preferences { Lan = true });
        Assert.IsTrue(new PreferencesFile(nested).Load(out _).Lan);
    }

    [TestMethod]
    [DataRow("{ \"lan\": tru")]
    [DataRow("not json at all")]
    [DataRow("[1, 2, 3]")]
    [DataRow("{ \"port\": \"eighty\" }")]
    public void ADamagedFileMeansDefaultsAndANotice(string content)
    {
        File.WriteAllText(FilePath, content);

        var loaded = new PreferencesFile(_directory).Load(out string? notice);

        Assert.AreEqual(new Preferences(), loaded);
        StringAssert.Contains(notice, "could not be read");
        Assert.AreEqual(content, File.ReadAllText(FilePath), "the damaged file is kept until a setting is changed");
    }

    [TestMethod]
    [DataRow(80)]
    [DataRow(70000)]
    [DataRow(-1)]
    public void APortOutsideTheAllowedRangeFallsBackToTheDefault(int port)
    {
        File.WriteAllText(FilePath, $"{{ \"lan\": true, \"port\": {port} }}");

        var loaded = new PreferencesFile(_directory).Load(out string? notice);

        Assert.AreEqual(LaunchOptions.DefaultPort, loaded.Port);
        Assert.IsTrue(loaded.Lan, "the other settings are kept");
        StringAssert.Contains(notice, port.ToString());
    }

    [TestMethod]
    public void AnEmptyObjectIsTheDefaults()
    {
        File.WriteAllText(FilePath, "{}");
        Assert.AreEqual(new Preferences(), new PreferencesFile(_directory).Load(out string? notice));
        Assert.IsNull(notice);
    }
}

/// <summary>Stands in for the registry's Run key.</summary>
public sealed class FakeRunKey : IRunKey
{
    public Dictionary<string, string> Values { get; } = new();
    public int Writes { get; private set; }

    public string? Read(string name) => Values.GetValueOrDefault(name);
    public void Write(string name, string command) { Values[name] = command; Writes++; }
    public void Delete(string name) { Values.Remove(name); Writes++; }
}

[TestClass]
public sealed class AutostartTests
{
    private const string Exe = @"C:\Apps\Abyss Remote\AbyssRemote.exe";

    [TestMethod]
    public void NothingIsWrittenUntilTheUserTurnsItOn()
    {
        var key = new FakeRunKey();
        var autostart = new Autostart(key, Exe);

        Assert.IsFalse(autostart.IsEnabled);
        Assert.AreEqual(0, key.Writes, "looking never writes");
    }

    [TestMethod]
    public void TurningItOnStartsTheProgramHiddenInTheTray()
    {
        var key = new FakeRunKey();
        var autostart = new Autostart(key, Exe);

        autostart.Enable();

        Assert.AreEqual("\"C:\\Apps\\Abyss Remote\\AbyssRemote.exe\" --minimized", key.Values["Abyss Remote"]);
        Assert.IsTrue(autostart.IsEnabled);
        Assert.IsTrue(LaunchOptions.Parse(["--minimized"]).Minimized, "the option the command uses must exist");
    }

    [TestMethod]
    public void TurningItOffRemovesTheEntry()
    {
        var key = new FakeRunKey();
        var autostart = new Autostart(key, Exe);
        autostart.Enable();

        autostart.Disable();

        Assert.AreEqual(0, key.Values.Count);
        Assert.IsFalse(autostart.IsEnabled);
        autostart.Disable(); // already off: nothing to do, no error
    }

    [TestMethod]
    public void AnEntryForAnotherCopyCountsAsOffAndIsRepairedByTurningItOn()
    {
        var key = new FakeRunKey();
        key.Values[Autostart.ValueName] = "\"D:\\Old\\AbyssRemote.exe\" --minimized";
        var autostart = new Autostart(key, Exe);

        Assert.IsFalse(autostart.IsEnabled);
        autostart.Enable();
        Assert.AreEqual(autostart.Command, key.Values[Autostart.ValueName]);
        Assert.AreEqual(1, key.Values.Count);
    }

    [TestMethod]
    public void OtherProgramsEntriesAreNeverTouched()
    {
        var key = new FakeRunKey();
        key.Values["Some Other App"] = "other.exe";
        var autostart = new Autostart(key, Exe);

        autostart.Enable();
        autostart.Disable();

        Assert.AreEqual("other.exe", key.Values["Some Other App"]);
        Assert.AreEqual(1, key.Values.Count);
    }
}

[TestClass]
public sealed class SingleInstanceTests
{
    // A fresh name per test, so a real Abyss Remote running on this PC is never involved.
    private static string UniqueName() => "AbyssRemote.Test." + Guid.NewGuid().ToString("N");

    [TestMethod]
    public void OnlyTheFirstCopyIsFirst()
    {
        string name = UniqueName();
        using var first = new SingleInstance(name);
        using var second = new SingleInstance(name);

        Assert.IsTrue(first.IsFirst);
        Assert.IsFalse(second.IsFirst);
    }

    [TestMethod]
    public async Task ASecondCopyAsksTheFirstToShowItsWindow()
    {
        string name = UniqueName();
        using var first = new SingleInstance(name);
        int shown = 0;
        first.Listen(() => Interlocked.Increment(ref shown));

        using (var second = new SingleInstance(name)) second.AskFirstToShow();
        await Wait.UntilAsync(() => Volatile.Read(ref shown) == 1);

        // And again: the first copy keeps listening.
        using (var third = new SingleInstance(name)) third.AskFirstToShow();
        await Wait.UntilAsync(() => Volatile.Read(ref shown) == 2);
    }

    [TestMethod]
    public void AfterTheFirstCopyExitsTheNextOneIsFirstAgain()
    {
        string name = UniqueName();
        new SingleInstance(name).Dispose();
        using var next = new SingleInstance(name);
        Assert.IsTrue(next.IsFirst);
    }

    [TestMethod]
    public void DifferentNamesDoNotBlockEachOther()
    {
        using var one = new SingleInstance(UniqueName());
        using var other = new SingleInstance(UniqueName());
        Assert.IsTrue(one.IsFirst && other.IsFirst);
    }
}

[TestClass]
public sealed class PhoneSetupTests
{
    private const string Program = @"C:\Apps\AbyssRemote\AbyssRemote.exe";
    private const int All = int.MaxValue, Private = 2, Public = 4, DomainAndPrivate = 3;

    private static FirewallRule Rule(bool allow = true, int protocol = FirewallCheck.Tcp, string? ports = null, string? program = null,
        int profiles = Private, bool enabled = true, string? service = null, string? package = null) =>
        new("test rule", enabled, allow, protocol, ports, program, service, package, profiles);

    [TestMethod]
    public void WithoutAMatchingRuleThereIsNoRule()
    {
        Assert.AreEqual(FirewallVerdict.NoRule, FirewallCheck.Evaluate([], 8444, Program));
        Assert.AreEqual(FirewallVerdict.NoRule, FirewallCheck.Evaluate([Rule(ports: "8443")], 8444, Program), "the rule for the pre-1.0 port");
        Assert.AreEqual(FirewallVerdict.NoRule, FirewallCheck.Evaluate([Rule(ports: "8444", protocol: 17)], 8444, Program), "UDP");
        Assert.AreEqual(FirewallVerdict.NoRule, FirewallCheck.Evaluate([Rule(ports: "8444", enabled: false)], 8444, Program));
        Assert.AreEqual(FirewallVerdict.NoRule, FirewallCheck.Evaluate([Rule(ports: "8444", profiles: Public)], 8444, Program), "Public only");
        Assert.AreEqual(FirewallVerdict.NoRule, FirewallCheck.Evaluate([Rule(program: @"C:\Other\other.exe")], 8444, Program));
    }

    [TestMethod]
    public void TheRuleTheHelperScriptAddsCounts()
    {
        // ENABLE-LAN-FIREWALL.bat: TCP, one port, Private profile, any program.
        Assert.AreEqual(FirewallVerdict.Allowed, FirewallCheck.Evaluate([Rule(ports: "8444")], 8444, Program));
        Assert.AreEqual(FirewallVerdict.Allowed, FirewallCheck.Evaluate([Rule(ports: "80,443,8000-9000", profiles: All)], 8444, Program));
        Assert.AreEqual(FirewallVerdict.Allowed, FirewallCheck.Evaluate([Rule(ports: "8444", profiles: DomainAndPrivate)], 8444, Program));
    }

    [TestMethod]
    public void ARuleForThisProgramCounts()
    {
        // What Windows adds when its own "allow access?" prompt is answered with Allow.
        Assert.AreEqual(FirewallVerdict.Allowed, FirewallCheck.Evaluate([Rule(ports: "*", program: Program.ToUpperInvariant())], 8444, Program));
        Assert.AreEqual(FirewallVerdict.Allowed, FirewallCheck.Evaluate([Rule(protocol: FirewallCheck.AnyProtocol, program: Program)], 8444, Program));
        Assert.AreEqual(FirewallVerdict.NoRule, FirewallCheck.Evaluate([Rule(ports: "9000", program: Program)], 8444, Program), "another port of this program");
    }

    [TestMethod]
    public void RulesForStoreAppsAndServicesOpenNothingForUs()
    {
        // Windows keeps many "all ports, no program" rules; on a real PC every one of them
        // belonged to a Store app or a service, so they must not read as "allowed".
        Assert.AreEqual(FirewallVerdict.NoRule, FirewallCheck.Evaluate([Rule(protocol: FirewallCheck.AnyProtocol, profiles: All)], 8444, Program));
        Assert.AreEqual(FirewallVerdict.NoRule, FirewallCheck.Evaluate([Rule(ports: "*")], 8444, Program));
        Assert.AreEqual(FirewallVerdict.NoRule, FirewallCheck.Evaluate([Rule(ports: "8444", package: "S-1-15-2-1")], 8444, Program));
        Assert.AreEqual(FirewallVerdict.NoRule, FirewallCheck.Evaluate([Rule(ports: "8444", service: "upnphost")], 8444, Program));
    }

    [TestMethod]
    public void ABlockingRuleForThisProgramWinsOverAnyAllowRule()
    {
        // What Windows adds when its prompt is answered with Cancel.
        var rules = new[] { Rule(ports: "8444"), Rule(allow: false, ports: "*", program: Program), Rule(program: Program) };
        Assert.AreEqual(FirewallVerdict.Blocked, FirewallCheck.Evaluate(rules, 8444, Program));

        // Blocking rules for other programs, or without a program, are not about us.
        var others = new[] { Rule(ports: "8444"), Rule(allow: false, ports: "*", program: @"C:\Games\game.exe"), Rule(allow: false, protocol: FirewallCheck.AnyProtocol, profiles: All) };
        Assert.AreEqual(FirewallVerdict.Allowed, FirewallCheck.Evaluate(others, 8444, Program));
    }

    [TestMethod]
    [DataRow("8444", true)]
    [DataRow(" 80 , 8444 ", true)]
    [DataRow("8000-9000", true)]
    [DataRow("8444-8444", true)]
    [DataRow("8445-9000", false)]
    [DataRow("84440", false)]
    [DataRow("844", false)]
    [DataRow("RPC", false)]
    [DataRow("IPHTTPS,RPC-EPMap", false)]
    [DataRow("", false)]
    public void PortListsAreReadLikeWindowsWritesThem(string ports, bool expected) =>
        Assert.AreEqual(expected, FirewallCheck.ListsPort(ports, 8444));

    [TestMethod]
    public void TheNetworkWithInternetAccessDecides()
    {
        var home = new ConnectedNetwork("Home Wi-Fi", Category: 1, HasInternet: true);
        var cafe = new ConnectedNetwork("Cafe", Category: 0, HasInternet: true);
        var virtualSwitch = new ConnectedNetwork("Unidentified network", Category: 0, HasInternet: false);
        var office = new ConnectedNetwork("corp.example", Category: 2, HasInternet: true);

        Assert.AreEqual((NetworkKind.NotConnected, (string?)null), NetworkCheck.Evaluate([]));
        Assert.AreEqual((NetworkKind.Private, "Home Wi-Fi"), NetworkCheck.Evaluate([home]));
        Assert.AreEqual((NetworkKind.Public, "Cafe"), NetworkCheck.Evaluate([cafe]));
        Assert.AreEqual((NetworkKind.Private, "Home Wi-Fi"), NetworkCheck.Evaluate([virtualSwitch, home]), "a virtual adapter's network does not count");
        Assert.AreEqual((NetworkKind.Public, "Cafe"), NetworkCheck.Evaluate([home, cafe]), "one Public network with internet is enough to warn");
        Assert.AreEqual((NetworkKind.Private, "corp.example"), NetworkCheck.Evaluate([office]), "a company domain is not Public");
    }

    [TestMethod]
    public void WithoutInternetAPrivateNetworkStillCountsAsHome()
    {
        var offlineHome = new ConnectedNetwork("Home", Category: 1, HasInternet: false);
        var virtualSwitch = new ConnectedNetwork("Unidentified network", Category: 0, HasInternet: false);

        Assert.AreEqual((NetworkKind.Private, "Home"), NetworkCheck.Evaluate([virtualSwitch, offlineHome]));
        Assert.AreEqual((NetworkKind.Public, "Unidentified network"), NetworkCheck.Evaluate([virtualSwitch]));
    }
}
