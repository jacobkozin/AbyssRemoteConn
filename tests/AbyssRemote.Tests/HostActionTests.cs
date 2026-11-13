using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AbyssRemote.Host.Features;

namespace AbyssRemote.Tests;

/// <summary>Lock and sleep, against the real server with a pretend PC. The real Windows calls
/// are never made here: a test run must not lock the screen or put the PC to sleep.</summary>
[TestClass]
public sealed class HostActionTests
{
    private TestServer _server = null!;

    [TestInitialize]
    public async Task Start() => _server = await TestServer.StartAsync();

    [TestCleanup]
    public async Task Stop()
    {
        _server.Actions.Awake.Set(); // never leave a pretend sleep waiting
        await _server.DisposeAsync();
    }

    private static Task<HttpResponseMessage> RunAsync(TestClient client, string id) =>
        client.PostJsonAsync($"/api/v1/actions/{id}", new { });

    /// <summary>Long enough for an action that was (wrongly) started in the background to reach the pretend PC.</summary>
    private static Task SettleAsync() => Task.Delay(250);

    [TestMethod]
    public async Task ThePcOffersLockAndSleepAndNothingElse()
    {
        using var client = await _server.PairAsync();
        var list = await client.Http.GetFromJsonAsync<JsonElement>("/api/v1/actions");

        var actions = list.GetProperty("actions").EnumerateArray().ToList();
        CollectionAssert.AreEqual(new[] { "lock", "sleep" }, actions.Select(a => a.GetProperty("id").GetString()).ToArray());
        Assert.IsTrue(actions.All(a => a.GetProperty("label").GetString()!.Length > 0));
    }

    [TestMethod]
    public async Task EveryListedActionCanBeRun()
    {
        using var client = await _server.PairAsync();
        var list = await client.Http.GetFromJsonAsync<JsonElement>("/api/v1/actions");

        foreach (var action in list.GetProperty("actions").EnumerateArray())
        {
            using var reply = await RunAsync(client, action.GetProperty("id").GetString()!);
            Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);
            Assert.IsTrue((await reply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("done").GetBoolean());
        }
        await Wait.UntilAsync(() => _server.Actions.Ran.Count == 2);
        CollectionAssert.AreEquivalent(new[] { "lock", "sleep" }, _server.Actions.Ran.ToArray());
    }

    [TestMethod]
    public async Task StrangersCannotSeeOrRunActions()
    {
        using var stranger = _server.CreateClient();
        using (var list = await stranger.Http.GetAsync("/api/v1/actions"))
            Assert.AreEqual(HttpStatusCode.Unauthorized, list.StatusCode);

        using var client = await _server.PairAsync();
        foreach (string id in new[] { "lock", "sleep", "no-such-action" })
            await ForgedRequests.AssertAllRefusedAsync(_server, client, HttpMethod.Post, $"/api/v1/actions/{id}", () => JsonContent.Create(new { }));

        await SettleAsync();
        Assert.AreEqual(0, _server.Actions.Ran.Count, "a refused request must never reach the PC");
        Assert.IsFalse(_server.Notices.Any(n => n.Contains("locked") || n.Contains("sleep")));
    }

    [TestMethod]
    public async Task LockHappensAtOnceAndNamesTheDevice()
    {
        using var client = await _server.PairAsync("Kitchen tablet");
        using var reply = await RunAsync(client, "lock");

        Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);
        CollectionAssert.AreEqual(new[] { "lock" }, _server.Actions.Ran.ToArray());
        Assert.IsTrue(_server.Notices.Any(n => n.Contains("Kitchen tablet") && n.Contains("locked")));
    }

    [TestMethod]
    public async Task SleepAnswersWithoutWaitingForThePcToWakeUp()
    {
        _server.Actions.Awake.Reset(); // the pretend PC stays asleep until the test wakes it
        using var client = await _server.PairAsync("Kitchen tablet");

        using (var reply = await RunAsync(client, "sleep"))
            Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode, "the answer must not wait for the sleep to end");
        await Wait.UntilAsync(() => _server.Actions.Ran.Count == 1);
        Assert.IsTrue(_server.Notices.Any(n => n.Contains("Kitchen tablet") && n.Contains("sleep")));

        // A double tap while the PC is going to sleep or asleep must not queue a second sleep.
        using (var again = await RunAsync(client, "sleep"))
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode);
        await SettleAsync();
        CollectionAssert.AreEqual(new[] { "sleep" }, _server.Actions.Ran.ToArray());
        Assert.AreEqual(1, _server.Notices.Count(n => n.Contains("sleep")));

        // Awake again: it can be asked to sleep once more (the server notices the wake-up a moment later).
        _server.Actions.Awake.Set();
        for (int attempt = 0; attempt < 100 && _server.Actions.Ran.Count < 2; attempt++)
        {
            using var later = await RunAsync(client, "sleep");
            Assert.AreEqual(HttpStatusCode.OK, later.StatusCode);
            await Task.Delay(20);
        }
        Assert.IsTrue(_server.Actions.Ran.Count >= 2, "sleep must work again after the PC woke up");
    }

    [TestMethod]
    public async Task AnythingOffTheListIsNotFound()
    {
        using var client = await _server.PairAsync();
        string[] ids =
        [
            "shutdown", "restart", "hibernate", "run", "cmd.exe",
            "LOCK", "lock%20", "lock;calc.exe", "lock%26calc.exe", // ids are exact
            "..%5C..%5CWindows%5CSystem32%5Ccalc.exe",
        ];
        foreach (string id in ids)
        {
            using var reply = await RunAsync(client, id);
            Assert.AreEqual(HttpStatusCode.NotFound, reply.StatusCode, id);
        }

        await SettleAsync();
        Assert.AreEqual(0, _server.Actions.Ran.Count);
    }

    [TestMethod]
    public async Task ARefusalByWindowsIsReported()
    {
        _server.Actions.Refusal = "Windows did not do that.";
        using var client = await _server.PairAsync();

        using (var locked = await RunAsync(client, "lock"))
        {
            Assert.AreEqual(HttpStatusCode.InternalServerError, locked.StatusCode);
            Assert.AreEqual("Windows did not do that.", (await locked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }
        Assert.IsFalse(_server.Notices.Any(n => n.Contains("locked")), "nothing was locked, so nothing is announced");

        // Sleep has already answered when Windows refuses, so only the PC can show it.
        using (var sleep = await RunAsync(client, "sleep"))
            Assert.AreEqual(HttpStatusCode.OK, sleep.StatusCode);
        await Wait.UntilAsync(() => _server.Notices.Contains("Windows did not do that."));
    }

    [TestMethod]
    public void NoActionTakesAnythingFromTheBrowser()
    {
        // The guarantee that this can never run a command: no action has a parameter.
        var methods = typeof(IHostActions).GetMethods();
        CollectionAssert.AreEquivalent(new[] { "Lock", "Sleep" }, methods.Select(m => m.Name).ToArray());
        Assert.IsTrue(methods.All(m => m.GetParameters().Length == 0 && m.ReturnType == typeof(void)));
    }
}
