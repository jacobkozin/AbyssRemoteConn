using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AbyssRemote.Tests;

/// <summary>The Steam endpoints, against the real server and a pretend Steam folder. No test
/// here starts Steam, a game or Big Picture: what would be started is only written down.</summary>
[TestClass]
public sealed class SteamEndpointTests
{
    private TestServer _server = null!;

    private FakeSteam Steam => _server.Steam;

    [TestInitialize]
    public async Task Start() => _server = await TestServer.StartAsync();

    [TestCleanup]
    public async Task Stop()
    {
        Steam.Dispose();
        await _server.DisposeAsync();
    }

    private static Task<HttpResponseMessage> PostAsync(TestClient client, string path) => client.SendAsync(HttpMethod.Post, path);

    private static async Task<string?> ErrorAsync(HttpResponseMessage reply) =>
        (await reply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    /// <summary>What the host console was told about Steam.</summary>
    private string[] SteamNotices() => _server.Notices.Where(notice => notice.Contains("Steam")).ToArray();

    [TestMethod]
    public async Task TheGameListHasTheAgreedShape()
    {
        Steam.AddGame(620, "Portal 2", lastPlayed: 1_790_000_000, sizeOnDisk: 12_345_678_901);
        Steam.AddCover(620);
        Steam.AddGame(1001, "Never played");
        Steam.AddGame(1002, "Still downloading", stateFlags: "1026");
        Steam.AddGame(228980, "Steamworks Common Redistributables");
        using var client = await _server.PairAsync();

        var reply = await client.Http.GetFromJsonAsync<JsonElement>("/api/v1/steam");

        Assert.IsTrue(reply.GetProperty("installed").GetBoolean());
        var games = reply.GetProperty("games").EnumerateArray().ToList();
        Assert.AreEqual(2, games.Count, "only fully installed games, and no tools");

        CollectionAssert.AreEqual(new[] { "id", "name", "hasArt", "lastPlayedUtc", "sizeBytes" },
            games[0].EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(620u, games[0].GetProperty("id").GetUInt32());
        Assert.AreEqual("Portal 2", games[0].GetProperty("name").GetString());
        Assert.IsTrue(games[0].GetProperty("hasArt").GetBoolean());
        Assert.AreEqual("2026-09-21T14:13:20+00:00", games[0].GetProperty("lastPlayedUtc").GetString());
        Assert.AreEqual(12_345_678_901, games[0].GetProperty("sizeBytes").GetInt64());

        Assert.AreEqual(1001u, games[1].GetProperty("id").GetUInt32());
        Assert.IsFalse(games[1].GetProperty("hasArt").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, games[1].GetProperty("lastPlayedUtc").ValueKind);
    }

    [TestMethod]
    public async Task APcWithoutSteamSaysSoAndStartsNothing()
    {
        Steam.AddGame(620, "Portal 2");
        Steam.AddCover(620);
        Steam.IsInstalled = false;
        using var client = await _server.PairAsync();

        Assert.AreEqual("""{"installed":false,"games":[]}""", await client.Http.GetStringAsync("/api/v1/steam"));
        using (var art = await client.Http.GetAsync("/api/v1/steam/games/620/art"))
            Assert.AreEqual(HttpStatusCode.NotFound, art.StatusCode);
        using (var launch = await PostAsync(client, "/api/v1/steam/games/620/launch"))
            Assert.AreEqual(HttpStatusCode.NotFound, launch.StatusCode);
        foreach (string path in new[] { "/api/v1/steam/bigpicture", "/api/v1/steam/bigpicture/close" })
        {
            using var reply = await PostAsync(client, path);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, reply.StatusCode, path);
            Assert.AreEqual("Steam is not installed on this PC.", await ErrorAsync(reply));
        }
        Assert.AreEqual(0, Steam.Started.Count);
        Assert.AreEqual(0, SteamNotices().Length);
    }

    [TestMethod]
    public async Task SteamWithoutGamesIsAnEmptyList()
    {
        using var client = await _server.PairAsync();
        Assert.AreEqual("""{"installed":true,"games":[]}""", await client.Http.GetStringAsync("/api/v1/steam"));
    }

    [TestMethod]
    public async Task StrangersCannotSeeGamesOrCovers()
    {
        Steam.AddGame(620, "Portal 2");
        Steam.AddCover(620);
        using var stranger = _server.CreateClient();

        foreach (string path in new[] { "/api/v1/steam", "/api/v1/steam/games/620/art", "/api/v1/steam/games/999/art" })
        {
            using var reply = await stranger.Http.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.Unauthorized, reply.StatusCode, path);
            Assert.AreEqual(0, (await reply.Content.ReadAsByteArrayAsync()).Length, path);
            Assert.IsTrue(reply.Headers.CacheControl?.NoStore, path);
        }
    }

    [TestMethod]
    public async Task StartingAnythingNeedsTheSessionTheOriginAndTheCsrfToken()
    {
        Steam.AddGame(620, "Portal 2");
        using var client = await _server.PairAsync();

        foreach (string path in new[] { "/api/v1/steam/games/620/launch", "/api/v1/steam/bigpicture", "/api/v1/steam/bigpicture/close" })
            await ForgedRequests.AssertAllRefusedAsync(_server, client, HttpMethod.Post, path);

        Assert.AreEqual(0, Steam.Started.Count, "a refused request must never reach Steam");
        Assert.AreEqual(0, SteamNotices().Length);
    }

    [TestMethod]
    public async Task ACoverIsSentWithItsRealTypeAndMayBeKeptForADay()
    {
        Steam.AddGame(620, "Portal 2");
        Steam.AddCover(620, "0123456789abcdef0123456789abcdef01234567/library_capsule.jpg", FakeSteam.Png);
        Steam.AddGame(1001, "Harbor Lights");
        Steam.AddCover(1001);
        using var client = await _server.PairAsync();

        using var png = await client.Http.GetAsync("/api/v1/steam/games/620/art");
        Assert.AreEqual(HttpStatusCode.OK, png.StatusCode);
        Assert.AreEqual("image/png", png.Content.Headers.ContentType?.ToString());
        CollectionAssert.AreEqual(FakeSteam.Png, await png.Content.ReadAsByteArrayAsync());
        Assert.IsTrue(png.Headers.CacheControl!.Private);
        Assert.AreEqual(TimeSpan.FromDays(1), png.Headers.CacheControl.MaxAge);
        Assert.IsFalse(png.Headers.CacheControl.NoStore);
        Assert.AreEqual("nosniff", png.Headers.GetValues("X-Content-Type-Options").Single());

        using var jpeg = await client.Http.GetAsync("/api/v1/steam/games/1001/art");
        Assert.AreEqual("image/jpeg", jpeg.Content.Headers.ContentType?.ToString());
        CollectionAssert.AreEqual(FakeSteam.Jpeg, await jpeg.Content.ReadAsByteArrayAsync());
    }

    [TestMethod]
    public async Task AMissingCoverIsNotFound()
    {
        Steam.AddGame(620, "Portal 2"); // installed, but Steam has no picture for it
        Steam.AddCover(1001);           // a picture in the cache, but the game is not installed
        Steam.AddGame(228980, "Steamworks Common Redistributables");
        Steam.AddCover(228980);
        using var client = await _server.PairAsync();

        foreach (uint id in new uint[] { 620, 1001, 228980, 999 })
        {
            using var reply = await client.Http.GetAsync($"/api/v1/steam/games/{id}/art");
            Assert.AreEqual(HttpStatusCode.NotFound, reply.StatusCode, $"game {id}");
            Assert.IsTrue(reply.Headers.CacheControl?.NoStore, "only a real cover may be kept by the browser");
        }
    }

    [TestMethod]
    public async Task ACoverRequestCanOnlyNameAGameByNumber()
    {
        Steam.AddGame(620, "Portal 2");
        Steam.AddCover(620);
        // Pictures with a cover's name in the folders a path trick would climb to.
        File.WriteAllBytes(Path.Combine(Steam.CoverCache, "library_600x900.jpg"), FakeSteam.Jpeg);
        File.WriteAllBytes(Path.Combine(Steam.Folder, "appcache", "library_600x900.jpg"), FakeSteam.Jpeg);
        File.WriteAllBytes(Path.Combine(Steam.Folder, "library_600x900.jpg"), FakeSteam.Jpeg);
        using var client = await _server.PairAsync();

        string[] tricks =
        [
            "..", "%2e%2e", "..%2f..", "%2e%2e%2f%2e%2e%2f", "620%2f..", "620%2f..%2f..", "620%5c..", "..%5c..%5c620", ".", "%2e",
            "620/..", "620%2flibrary_600x900.jpg", "library_600x900.jpg", "620.jpg", "620%00", "%20620", "620%20", "+620", "-620",
            "0x26c", "620abc", "%EF%BC%96%EF%BC%92%EF%BC%90", "4294967916", "99999999999999999999", "C:%5CWindows", "%5C%5Clocalhost%5Cc$",
        ];
        foreach (string trick in tricks)
        {
            using var reply = await client.Http.GetAsync($"/api/v1/steam/games/{trick}/art");
            Assert.IsTrue(reply.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"{trick} gave {reply.StatusCode}");
            Assert.AreNotEqual("image/jpeg", reply.Content.Headers.ContentType?.MediaType, $"{trick} was answered with a picture");
        }
    }

    [TestMethod]
    public async Task StartingAGameRunsSteamWithThatGamesNumberAndTellsTheHost()
    {
        Steam.AddGame(620, "Portal 2");
        using var client = await _server.PairAsync("Living room iPad");

        using var reply = await PostAsync(client, "/api/v1/steam/games/620/launch");

        Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);
        Assert.AreEqual("""{"launched":true,"name":"Portal 2"}""", await reply.Content.ReadAsStringAsync());
        var start = Steam.Started.Single();
        Assert.AreEqual(Steam.Program, start.FileName);
        CollectionAssert.AreEqual(new[] { "steam://rungameid/620" }, start.ArgumentList.ToArray());
        Assert.AreEqual("", start.Arguments);
        Assert.IsFalse(start.UseShellExecute);
        CollectionAssert.AreEqual(new[] { "\"Living room iPad\" started \"Portal 2\" through Steam." }, SteamNotices());
    }

    [TestMethod]
    public async Task OnlyAnInstalledGameCanBeStarted()
    {
        Steam.AddGame(620, "Portal 2");
        Steam.AddGame(1002, "Still downloading", stateFlags: "1026");
        Steam.AddGame(228980, "Steamworks Common Redistributables");
        using var client = await _server.PairAsync();

        string[] refused =
        [
            "999", "1002", "228980", "0", "-620", "+620", "620abc", "0x26c", "620%20", "%20620", "620%3Bcalc.exe", "620%20-shutdown",
            "steam:%2F%2Frungameid%2F620", "620%2F..%2F..%2Fopen%2Fbigpicture", "4294967916", "99999999999999999999", "%EF%BC%96%EF%BC%92%EF%BC%90",
        ];
        foreach (string id in refused)
        {
            using var reply = await PostAsync(client, $"/api/v1/steam/games/{id}/launch");
            Assert.AreEqual(HttpStatusCode.NotFound, reply.StatusCode, id);
            Assert.AreEqual("That game is not installed on this PC.", await ErrorAsync(reply), id);
        }
        Assert.AreEqual(0, Steam.Started.Count);
        Assert.AreEqual(0, SteamNotices().Length);
    }

    [TestMethod]
    public async Task BigPictureIsOpenedAndClosed()
    {
        using var client = await _server.PairAsync("Living room iPad");

        using (var open = await PostAsync(client, "/api/v1/steam/bigpicture"))
            Assert.AreEqual("""{"opened":true}""", await open.Content.ReadAsStringAsync());
        using (var close = await PostAsync(client, "/api/v1/steam/bigpicture/close"))
            Assert.AreEqual("""{"closed":true}""", await close.Content.ReadAsStringAsync());

        CollectionAssert.AreEqual(new[] { "steam://open/bigpicture", "steam://close/bigpicture" },
            Steam.Started.Select(start => start.ArgumentList.Single()).ToArray());
        Assert.IsTrue(Steam.Started.All(start => start.FileName == Steam.Program && !start.UseShellExecute));
        CollectionAssert.AreEqual(
            new[] { "\"Living room iPad\" opened Steam Big Picture.", "\"Living room iPad\" closed Steam Big Picture." }, SteamNotices());
    }

    [TestMethod]
    public async Task ASteamThatWillNotStartIsAnErrorTheUserCanRead()
    {
        Steam.AddGame(620, "Portal 2");
        Steam.StartProblem = new Win32Exception(5); // "Access is denied"
        using var client = await _server.PairAsync();

        foreach (string path in new[] { "/api/v1/steam/games/620/launch", "/api/v1/steam/bigpicture", "/api/v1/steam/bigpicture/close" })
        {
            using var reply = await PostAsync(client, path);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, reply.StatusCode, path);
            Assert.AreEqual("Steam could not be started on this PC.", await ErrorAsync(reply), path);
        }
        Assert.AreEqual(0, SteamNotices().Length, "nothing was started, so there is nothing to announce");
    }

    [TestMethod]
    public async Task DamagedSteamFilesNeverBreakTheList()
    {
        Steam.AddGame(620, "Portal 2");
        Steam.WriteManifest(1001, "\"AppState\"\n{\n\t\"appid\"\t\t\"1001");
        Steam.WriteManifest(1002, "{{{{");
        File.WriteAllText(Path.Combine(Steam.Folder, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"");
        Directory.CreateDirectory(Path.Combine(Steam.CoverCache, "620", "library_600x900.jpg")); // a folder where the cover belongs
        using var client = await _server.PairAsync();

        var reply = await client.Http.GetFromJsonAsync<JsonElement>("/api/v1/steam");

        var game = reply.GetProperty("games").EnumerateArray().Single();
        Assert.AreEqual(620u, game.GetProperty("id").GetUInt32());
        Assert.IsFalse(game.GetProperty("hasArt").GetBoolean());
    }
}
