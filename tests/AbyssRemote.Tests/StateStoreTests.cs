using AbyssRemote.Host;

namespace AbyssRemote.Tests;

[TestClass]
public sealed class StateStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private string _dir = "";

    [TestInitialize]
    public void CreateFolder() => _dir = Directory.CreateTempSubdirectory("abyss-state-test-").FullName;

    [TestCleanup]
    public void DeleteFolder() => Directory.Delete(_dir, recursive: true);

    [TestMethod]
    public void PairedCookieAuthenticatesAndOnlyAHashIsStored()
    {
        var store = new StateStore(_dir, new ManualClock(Start));
        var (id, secret) = store.Pair("Jacob's iPhone");

        Assert.AreEqual(id, store.Authenticate($"{id}.{secret}")?.Id);
        string saved = File.ReadAllText(Path.Combine(_dir, "settings.json"));
        Assert.IsFalse(saved.Contains(secret, StringComparison.OrdinalIgnoreCase), "the session secret must never be written to disk");
    }

    [TestMethod]
    public void WrongOrMalformedCookiesAreRejected()
    {
        var store = new StateStore(_dir, new ManualClock(Start));
        var (id, secret) = store.Pair("phone");
        string otherSecret = new('A', 64);

        Assert.IsNull(store.Authenticate($"{id}.{otherSecret}"));
        Assert.IsNull(store.Authenticate(secret));
        Assert.IsNull(store.Authenticate($"{id}.{secret}.extra"));
        Assert.IsNull(store.Authenticate(""));
        Assert.IsNull(store.Authenticate(null));
    }

    [TestMethod]
    public void PairingExpiresAfterThirtyDaysAndIsPruned()
    {
        var clock = new ManualClock(Start);
        var store = new StateStore(_dir, clock);
        var (id, secret) = store.Pair("phone");

        clock.Advance(StateStore.DeviceLifetime - TimeSpan.FromMinutes(1));
        Assert.IsTrue(store.IsActive(id));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.IsFalse(store.IsActive(id));
        Assert.IsNull(store.Authenticate($"{id}.{secret}"));
        Assert.AreEqual(1, store.PruneExpired());
        Assert.AreEqual(0, store.Devices().Count);
    }

    [TestMethod]
    public void RevokedDevicesStopWorkingAndSurviveRestart()
    {
        var store = new StateStore(_dir, new ManualClock(Start));
        var (keep, keepSecret) = store.Pair("keep");
        var (drop, dropSecret) = store.Pair("drop");

        Assert.IsTrue(store.Revoke(drop));
        Assert.IsFalse(store.Revoke(drop));

        var reloaded = new StateStore(_dir, new ManualClock(Start));
        Assert.IsNull(reloaded.Authenticate($"{drop}.{dropSecret}"));
        Assert.IsNotNull(reloaded.Authenticate($"{keep}.{keepSecret}"));
        Assert.AreEqual(1, reloaded.RevokeAll());
        Assert.IsFalse(reloaded.IsActive(keep));
    }

    [TestMethod]
    public void DeviceNamesLoseControlCharactersAndAreCapped()
    {
        Assert.AreEqual("evil[2Jname", StateStore.CleanName("  evil\u001b[2J\r\nname\t "));
        Assert.AreEqual(60, StateStore.CleanName(new string('x', 200)).Length);
    }

    [TestMethod]
    public void DamagedSettingsFileStopsStartupWithoutBeingOverwritten()
    {
        string file = Path.Combine(_dir, "settings.json");
        File.WriteAllText(file, "{ not json");

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => new StateStore(_dir));
        StringAssert.Contains(error.Message, "settings.json");
        Assert.AreEqual("{ not json", File.ReadAllText(file));
    }
}
