using System.ComponentModel;
using AbyssRemote.Host.Features;

namespace AbyssRemote.Tests;

/// <summary>Finding games and covers in a pretend Steam folder, and what would be started for
/// them. Every file is made up for the test, and no test starts a program.</summary>
[TestClass]
public sealed class SteamLibraryTests
{
    private const string HashedFolder = "0123456789abcdef0123456789abcdef01234567";
    private FakeSteam _steam = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void CreateSteam() => _steam = new FakeSteam();

    [TestCleanup]
    public void DeleteSteam() => _steam.Dispose();

    private uint[] ListedIds() => _steam.Games().Select(game => game.Id).ToArray();

    [TestMethod]
    public void GamesAreFoundInEveryLibraryFolder()
    {
        string second = _steam.Beside("Second drive"), unplugged = _steam.Beside("Unplugged drive");
        _steam.ListLibraries(second, unplugged, second + Path.DirectorySeparatorChar, @"relative\folder");
        _steam.AddGame(1001, "Harbor Lights");
        _steam.AddGame(2002, "Night Train", library: second);
        _steam.AddGame(1001, "A stale copy of Harbor Lights", library: second);

        CollectionAssert.AreEqual(new[] { _steam.Folder, second, unplugged }, new SteamLibrary(_steam.Folder).Folders().ToArray());
        CollectionAssert.AreEquivalent(new[] { "Harbor Lights", "Night Train" }, _steam.Games().Select(game => game.Name).ToArray());
        Assert.AreEqual("Night Train", _steam.Launch(2002)?.Name, "a game on the second drive can be started too");
    }

    [TestMethod]
    public void ADamagedLibraryListStillLeavesTheSteamFolder()
    {
        _steam.ListLibraries(_steam.Beside("Second drive"));
        string list = Path.Combine(_steam.Folder, "steamapps", "libraryfolders.vdf");
        File.WriteAllText(list, File.ReadAllText(list)[..40]); // cut off in the middle
        _steam.AddGame(1001, "Harbor Lights");

        CollectionAssert.AreEqual(new[] { _steam.Folder }, new SteamLibrary(_steam.Folder).Folders().ToArray());
        CollectionAssert.AreEqual(new uint[] { 1001 }, ListedIds());
    }

    [TestMethod]
    [DataRow("4", true, DisplayName = "fully installed")]
    [DataRow("6", true, DisplayName = "installed, with an update waiting")]
    [DataRow("1026", false, DisplayName = "being downloaded")]
    [DataRow("2", false, DisplayName = "files incomplete")]
    [DataRow("1", false, DisplayName = "uninstalled")]
    [DataRow("0", false, DisplayName = "no state")]
    [DataRow("", false, DisplayName = "empty state")]
    [DataRow("four", false, DisplayName = "state that is not a number")]
    [DataRow("-4", false, DisplayName = "negative state")]
    public void OnlyFullyInstalledGamesAreListedAndStarted(string stateFlags, bool listed)
    {
        _steam.AddGame(1001, "Harbor Lights", stateFlags);

        Assert.AreEqual(listed, ListedIds().Contains(1001u));
        Assert.AreEqual(listed, _steam.Launch(1001) is not null);
        Assert.AreEqual(listed ? 1 : 0, _steam.Started.Count);
    }

    [TestMethod]
    [DataRow(228980u, "Steamworks Common Redistributables", false)]
    [DataRow(228980u, "Renamed some day", false)] // known by its number alone
    [DataRow(250820u, "SteamVR", false)]
    [DataRow(1001u, "SteamVR Performance Test", false)]
    [DataRow(1001u, "Steam Linux Runtime 3.0 (sniper)", false)]
    [DataRow(1001u, "Proton 9.0 (Beta)", false)]
    [DataRow(1001u, "Proton Experimental", false)]
    [DataRow(1001u, "Proton EasyAntiCheat Runtime", false)]
    [DataRow(1001u, "Harbor Lights Dedicated Server", false)]
    [DataRow(1001u, "Proton Storm", true)] // a game that only starts with the word
    [DataRow(1001u, "Steamboat Racers", true)]
    [DataRow(1001u, "Harbor Lights", true)]
    public void ThingsThatAreNotGamesAreLeftOut(uint id, string name, bool listed)
    {
        _steam.AddGame(id, name);

        Assert.AreEqual(listed, ListedIds().Contains(id));
        Assert.AreEqual(listed, _steam.Launch(id) is not null, "what is not listed cannot be started either");
    }

    [TestMethod]
    public void GamesAreOrderedByLastPlayedThenByName()
    {
        _steam.AddGame(1, "zebra, never played", lastPlayed: 0);
        _steam.AddGame(2, "Played long ago", lastPlayed: 1_700_000_000);
        _steam.AddGame(3, "Played yesterday", lastPlayed: 1_790_000_000);
        _steam.AddGame(4, "Apple, never played", lastPlayed: null); // some manifests have no LastPlayed line
        _steam.AddGame(5, "banana, never played", lastPlayed: 0);
        _steam.AddGame(6, "Also played yesterday", lastPlayed: 1_790_000_000);

        CollectionAssert.AreEqual(new uint[] { 6, 3, 2, 4, 5, 1 }, ListedIds());
    }

    [TestMethod]
    public void AGameCarriesItsNameSizeAndLastPlayedTime()
    {
        _steam.AddGame(620, "Portal 2", lastPlayed: 1_790_000_000, sizeOnDisk: 12_345_678_901);
        _steam.AddGame(1001, "Never played");
        _steam.AddCover(620);

        var games = _steam.Games();

        Assert.AreEqual(new SteamGame(620, "Portal 2", true, DateTimeOffset.FromUnixTimeSeconds(1_790_000_000), 12_345_678_901), games[0]);
        Assert.AreEqual(new SteamGame(1001, "Never played", false, null, 1_000_000), games[1]);
    }

    [TestMethod]
    public void DamagedAndOddManifestsAreSkipped()
    {
        string good = FakeSteam.Manifest(1001, "Harbor Lights");
        string steamapps = Path.GetDirectoryName(_steam.WriteManifest(1001, good))!;
        _steam.WriteManifest(1002, good[..(good.Length / 2)]); // cut off while it was written
        _steam.WriteManifest(1003, "\0\u0001 not a manifest at all }{");
        _steam.WriteManifest(1004, "");
        _steam.WriteManifest(1005, FakeSteam.Manifest(9999, "Says it is another game"));
        _steam.WriteManifest(1006, FakeSteam.Manifest(1006, ""));
        _steam.WriteManifest(1007, FakeSteam.Manifest(1007, "Far too large to be a manifest") + new string(' ', 1024 * 1024));
        _steam.WriteManifest(1008, "\"SomethingElse\"\n{\n\t\"appid\"\t\t\"1008\"\n\t\"name\"\t\t\"Wrong kind of file\"\n\t\"StateFlags\"\t\t\"4\"\n}\n");
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_abc.acf"), FakeSteam.Manifest(1009, "Not a number in the name"));
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_1010.acf.bak"), FakeSteam.Manifest(1010, "A backup copy"));
        Directory.CreateDirectory(Path.Combine(steamapps, "appmanifest_1011.acf")); // a folder, not a file
        string busy = _steam.WriteManifest(1012, FakeSteam.Manifest(1012, "Being written right now"));

        using (new FileStream(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            CollectionAssert.AreEqual(new uint[] { 1001 }, ListedIds());
        CollectionAssert.AreEquivalent(new uint[] { 1001, 1012 }, ListedIds(), "a file Steam has finished writing shows up again");

        foreach (uint id in new uint[] { 1002, 1003, 1004, 1005, 9999, 1006, 1007, 1008, 1009, 1010, 1011 })
            Assert.IsNull(_steam.Launch(id), $"game {id}");
        Assert.AreEqual(0, _steam.Started.Count);
    }

    [TestMethod]
    public void GameNamesLoseControlCharactersAndAreCut()
    {
        _steam.AddGame(1001, "  Evil\u001b[2J\r\nName\t ");
        _steam.AddGame(1002, new string('x', 99) + "\U0001F600 and more");

        var names = _steam.Games().ToDictionary(game => game.Id, game => game.Name);

        Assert.AreEqual("Evil[2JName", names[1001]);
        Assert.AreEqual(new string('x', 99), names[1002], "cut before the emoji, never through the middle of it");
    }

    [TestMethod]
    [DataRow("library_600x900.jpg")]
    [DataRow(HashedFolder + "/library_600x900.jpg")]
    [DataRow(HashedFolder + "/library_capsule.jpg")]
    [DataRow("LIBRARY_600X900.JPG")]
    public void TheCoverIsFoundWhereverSteamKeepsIt(string place)
    {
        _steam.AddGame(1001, "Harbor Lights");
        _steam.AddCover(1001, "header.jpg", [0xFF, 0xD8, 0xFF, 1]); // the wide banner is not the cover
        _steam.AddCover(1001, "fedcba9876543210fedcba9876543210fedcba98/library_hero.jpg", [0xFF, 0xD8, 0xFF, 2]);
        _steam.AddCover(1001, place);

        Assert.IsTrue(_steam.Games().Single().HasArt);
        var art = _steam.Art(1001)!;
        CollectionAssert.AreEqual(FakeSteam.Jpeg, art.Data);
        Assert.AreEqual("image/jpeg", art.ContentType);
    }

    [TestMethod]
    public void ACoverThatIsReallyAPngIsSentAsOne()
    {
        _steam.AddGame(1001, "Harbor Lights");
        _steam.AddCover(1001, data: FakeSteam.Png); // named .jpg, as Steam does

        var art = _steam.Art(1001)!;
        Assert.AreEqual("image/png", art.ContentType);
        CollectionAssert.AreEqual(FakeSteam.Png, art.Data);
    }

    [TestMethod]
    public void TheNewestOfSeveralCoversIsUsed()
    {
        _steam.AddGame(1001, "Harbor Lights");
        string old = _steam.AddCover(1001, "library_600x900.jpg", FakeSteam.Jpeg);
        string current = _steam.AddCover(1001, HashedFolder + "/library_capsule.jpg", FakeSteam.Png);
        File.SetLastWriteTimeUtc(old, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(current, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.AreEqual("image/png", _steam.Art(1001)!.ContentType);
    }

    [TestMethod]
    public void AGameWithoutAUsableCoverHasNoArt()
    {
        _steam.AddGame(1001, "No picture folder at all");
        _steam.AddGame(1002, "Only other pictures");
        _steam.AddCover(1002, "header.jpg");
        _steam.AddCover(1002, "logo.png", FakeSteam.Png);
        _steam.AddCover(1002, HashedFolder + ".jpg");
        _steam.AddCover(1002, "a/b/library_600x900.jpg"); // deeper than Steam ever puts it
        _steam.AddGame(1003, "A web page with a cover's name");
        _steam.AddCover(1003, data: "<html>not a picture</html>"u8.ToArray());
        _steam.AddGame(1004, "An empty file");
        _steam.AddCover(1004, data: []);
        _steam.AddGame(1005, "A picture that is far too large");
        using (var huge = new FileStream(_steam.AddCover(1005), FileMode.Open, FileAccess.Write))
            huge.SetLength(SteamLibrary.MaxCoverBytes + 1);

        var games = _steam.Games();

        Assert.AreEqual(5, games.Count);
        foreach (var game in games)
        {
            Assert.IsFalse(game.HasArt, game.Name);
            Assert.IsNull(_steam.Art(game.Id), game.Name);
        }
    }

    [TestMethod]
    public void OnlyListedGamesHaveACover()
    {
        _steam.AddCover(1001); // in the cache, but the game is not installed
        _steam.AddGame(228980, "Steamworks Common Redistributables");
        _steam.AddCover(228980);
        _steam.AddGame(1002, "Still downloading", stateFlags: "1026");
        _steam.AddCover(1002);

        foreach (uint id in new uint[] { 1001, 228980, 1002, 0, uint.MaxValue })
            Assert.IsNull(_steam.Art(id), $"game {id}");
    }

    [TestMethod]
    [DataRow("620", true)]
    [DataRow("4294967295", true)]
    [DataRow("4294967296", false)] // one more than a game number can be
    [DataRow("4294967916", false)] // must not wrap around to 620
    [DataRow("", false)]
    [DataRow(null, false)]
    [DataRow(" 620", false)]
    [DataRow("620 ", false)]
    [DataRow("+620", false)]
    [DataRow("-620", false)]
    [DataRow("0x26C", false)]
    [DataRow("6 20", false)]
    [DataRow("620.0", false)]
    [DataRow("1,000", false)]
    [DataRow("６２０", false)] // full-width digits
    [DataRow("620/../1", false)]
    [DataRow("620;calc", false)]
    public void AGameNumberIsDigitsOnly(string? text, bool valid) =>
        Assert.AreEqual(valid, SteamLibrary.TryParseId(text, out _));

    [TestMethod]
    public void StartingAGameRunsSteamItselfWithOneFixedArgument()
    {
        _steam.AddGame(620, "Portal 2");

        Assert.AreEqual("Portal 2", _steam.Launch(620)?.Name);

        var start = _steam.Started.Single();
        Assert.AreEqual(_steam.Program, start.FileName);
        CollectionAssert.AreEqual(new[] { "steam://rungameid/620" }, start.ArgumentList.ToArray());
        Assert.AreEqual("", start.Arguments, "the argument list is the only source of arguments");
        Assert.IsFalse(start.UseShellExecute, "steam.exe is started directly, never as a link opened through Windows");
        Assert.AreEqual(_steam.Folder, start.WorkingDirectory);
    }

    [TestMethod]
    public void AGameThatIsNotInstalledStartsNothing()
    {
        _steam.AddGame(620, "Portal 2");

        Assert.IsNull(_steam.Launch(621));
        Assert.IsNull(_steam.Launch(0));
        Assert.AreEqual(0, _steam.Started.Count);
    }

    [TestMethod]
    public void BigPictureIsOpenedAndClosedThroughSteam()
    {
        _steam.OpenBigPicture();
        _steam.CloseBigPicture();

        CollectionAssert.AreEqual(new[] { "steam://open/bigpicture", "steam://close/bigpicture" },
            _steam.Started.Select(start => start.ArgumentList.Single()).ToArray());
        Assert.IsTrue(_steam.Started.All(start => start.FileName == _steam.Program && !start.UseShellExecute));
    }

    [TestMethod]
    public void WithoutSteamThereIsNothingToListOrStart()
    {
        _steam.AddGame(620, "Portal 2");
        _steam.AddCover(620);
        _steam.IsInstalled = false;

        Assert.IsFalse(_steam.Installed);
        Assert.AreEqual(0, _steam.Games().Count);
        Assert.IsNull(_steam.Art(620));
        Assert.IsNull(_steam.Launch(620));
        Assert.AreEqual("Steam is not installed on this PC.", Assert.ThrowsExactly<SteamUnavailableException>(_steam.OpenBigPicture).Message);
        Assert.ThrowsExactly<SteamUnavailableException>(_steam.CloseBigPicture);
        Assert.AreEqual(0, _steam.Started.Count);
    }

    [TestMethod]
    public void ASteamFolderThatIsGoneIsAnEmptyLibrary()
    {
        // The registry can still name a folder that was deleted by hand.
        Assert.IsTrue(_steam.Installed);
        Assert.IsFalse(Directory.Exists(_steam.Folder));

        Assert.AreEqual(0, _steam.Games().Count);
        Assert.IsNull(_steam.Art(620));
        Assert.IsNull(_steam.Launch(620));
        Assert.AreEqual(0, _steam.Started.Count);
    }

    [TestMethod]
    public void ASteamThatWillNotStartIsReportedInPlainWords()
    {
        _steam.AddGame(620, "Portal 2");
        _steam.StartProblem = new Win32Exception(2); // "The system cannot find the file specified"

        var problem = Assert.ThrowsExactly<SteamUnavailableException>(() => _steam.Launch(620));

        Assert.AreEqual("Steam could not be started on this PC.", problem.Message);
    }

    /// <summary>Reads the real Steam library of the PC the tests run on, when it has one, to
    /// show that the reader copes with files Steam itself wrote. It only reads, and it reports
    /// numbers only: which games someone owns is nobody else's business.</summary>
    [TestMethod]
    public void TheSteamLibraryOfThisPcCanBeRead()
    {
        var steam = WindowsSteam.FindInRegistry();
        if (steam is null) Assert.Inconclusive("Steam is not installed on this PC.");
        Assert.IsTrue(File.Exists(steam.Program), "the registry names the program file that would be started");

        var library = new SteamLibrary(steam.Folder);
        var games = library.Games();
        int covers = games.Count(game => game.HasArt && library.Art(game.Id) is not null);
        TestContext.WriteLine($"Library folders: {library.Folders().Count}, games: {games.Count}, with a cover: {covers}");

        Assert.AreEqual(games.Count, games.Select(game => game.Id).Distinct().Count(), "every game is listed once");
        Assert.IsTrue(games.All(game => game.Name.Length is > 0 and <= SteamLibrary.MaxNameLength));
        Assert.AreEqual(games.Count(game => game.HasArt), covers, "every game listed with art has a cover that can be sent");
    }
}
