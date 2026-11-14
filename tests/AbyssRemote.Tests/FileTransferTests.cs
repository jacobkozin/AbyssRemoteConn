using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AbyssRemote.Host.Features;

namespace AbyssRemote.Tests;

/// <summary>Sending, listing, fetching and deleting files through the real server. The folder
/// is a temporary one; the attacks on it are in <see cref="FileTransferAttackTests"/>.</summary>
[TestClass]
public sealed class FileTransferTests
{
    private TestServer _server = null!;
    private string Folder => _server.Transfer.Directory;

    [TestInitialize]
    public async Task Start() => _server = await TestServer.StartAsync();

    [TestCleanup]
    public async Task Stop() => await _server.DisposeAsync();

    [TestMethod]
    public async Task AFileGoesToThePcAndComesBackUnchanged()
    {
        using var client = await _server.PairAsync("Kitchen tablet");
        byte[] photo = RandomNumberGenerator.GetBytes(300 * 1024); // well over the 64 KB limit of every other request
        Assert.IsFalse(Directory.Exists(Folder), "the folder appears with the first file, not before");

        using (var sent = await client.UploadAsync("Holiday photo.jpg", photo))
        {
            Assert.AreEqual(HttpStatusCode.Created, sent.StatusCode);
            var saved = await sent.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual("Holiday photo.jpg", saved.GetProperty("name").GetString());
            Assert.AreEqual(photo.Length, saved.GetProperty("size").GetInt64());
            Assert.AreEqual("/api/v1/files/Holiday%20photo.jpg", sent.Headers.Location?.OriginalString);
        }
        CollectionAssert.AreEqual(photo, File.ReadAllBytes(Path.Combine(Folder, "Holiday photo.jpg")));

        using (var fetched = await client.Http.GetAsync("/api/v1/files/Holiday%20photo.jpg"))
        {
            Assert.AreEqual(HttpStatusCode.OK, fetched.StatusCode);
            CollectionAssert.AreEqual(photo, await fetched.Content.ReadAsByteArrayAsync());
            // Saved, never shown: a browser must not open an uploaded file as a page of this site.
            Assert.AreEqual("attachment", fetched.Content.Headers.ContentDisposition?.DispositionType);
            Assert.AreEqual("Holiday photo.jpg", fetched.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
            Assert.AreEqual("application/octet-stream", fetched.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("nosniff", fetched.Headers.GetValues("X-Content-Type-Options").Single());
        }

        using (var removed = await client.SendAsync(HttpMethod.Delete, "/api/v1/files/Holiday%20photo.jpg"))
        {
            Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode);
            Assert.IsTrue((await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deleted").GetBoolean());
        }
        Assert.IsFalse(File.Exists(Path.Combine(Folder, "Holiday photo.jpg")));
        using (var gone = await client.Http.GetAsync("/api/v1/files/Holiday%20photo.jpg"))
            Assert.AreEqual(HttpStatusCode.NotFound, gone.StatusCode);

        // One line on the PC for each of the three, naming the device.
        foreach (string verb in new[] { "Received", "Sending", "deleted" })
            Assert.AreEqual(1, _server.Notices.Count(n => n.Contains(verb) && n.Contains("Holiday photo.jpg") && n.Contains("Kitchen tablet")), verb);
    }

    [TestMethod]
    public async Task TheListShowsFolderLimitAndFilesNewestFirst()
    {
        using var client = await _server.PairAsync();
        var empty = await client.Http.GetFromJsonAsync<JsonElement>("/api/v1/files");
        Assert.AreEqual(Folder, empty.GetProperty("folder").GetString());
        Assert.AreEqual(TransferFolder.DefaultMaxUploadBytes, empty.GetProperty("maxUploadBytes").GetInt64());
        Assert.AreEqual(0, empty.GetProperty("files").GetArrayLength());
        Assert.IsFalse(Directory.Exists(Folder), "looking at the list must not create the folder");

        Directory.CreateDirectory(Folder);
        var noon = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        foreach (var (name, bytes, minutes) in new[] { ("old.txt", 3, 0), ("newest.txt", 5, 30), ("middle.txt", 4, 10) })
        {
            File.WriteAllBytes(Path.Combine(Folder, name), new byte[bytes]);
            File.SetLastWriteTimeUtc(Path.Combine(Folder, name), noon.AddMinutes(minutes));
        }

        var files = (await client.Http.GetFromJsonAsync<JsonElement>("/api/v1/files")).GetProperty("files").EnumerateArray().ToList();
        CollectionAssert.AreEqual(new[] { "newest.txt", "middle.txt", "old.txt" }, files.Select(f => f.GetProperty("name").GetString()).ToArray());
        Assert.AreEqual(5, files[0].GetProperty("size").GetInt64());
        Assert.AreEqual(new DateTimeOffset(noon.AddMinutes(30)), files[0].GetProperty("modifiedUtc").GetDateTimeOffset());
    }

    [TestMethod]
    public async Task ATakenNameGetsANumberAndNothingIsReplaced()
    {
        using var client = await _server.PairAsync();
        var uploads = new (string Name, string Text, string SavedAs)[]
        {
            ("notes.txt", "first", "notes.txt"),
            ("notes.txt", "second", "notes (2).txt"),
            ("NOTES.TXT", "third", "NOTES (3).TXT"),      // Windows names ignore case
            ("notes (2).txt", "fourth", "notes (2) (2).txt"),
            ("archive.tar.gz", "fifth", "archive.tar.gz"),
            ("archive.tar.gz", "sixth", "archive.tar (2).gz"),
            ("README", "seventh", "README"),
            ("README", "eighth", "README (2)"),
            (".gitignore", "ninth", ".gitignore"),
            (".gitignore", "tenth", ".gitignore (2)"),
        };
        foreach (var (name, text, savedAs) in uploads)
        {
            using var reply = await client.UploadAsync(name, Encoding.UTF8.GetBytes(text));
            Assert.AreEqual(HttpStatusCode.Created, reply.StatusCode, name);
            Assert.AreEqual(savedAs, (await reply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
        }
        foreach (var (_, text, savedAs) in uploads)
            Assert.AreEqual(text, File.ReadAllText(Path.Combine(Folder, savedAs)), savedAs);
        Assert.AreEqual(uploads.Length, Directory.GetFileSystemEntries(Folder).Length);
    }

    [TestMethod]
    public async Task ASubFolderWithTheSameNameIsNotReplacedEither()
    {
        using var client = await _server.PairAsync();
        Directory.CreateDirectory(Path.Combine(Folder, "report.pdf"));

        using var reply = await client.UploadAsync("report.pdf", [1, 2, 3]);
        Assert.AreEqual("report (2).pdf", (await reply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
        Assert.IsTrue(Directory.Exists(Path.Combine(Folder, "report.pdf")));
    }

    [TestMethod]
    public async Task UploadsCarryTheDownloadMarkAndAreOrdinaryFiles()
    {
        using var client = await _server.PairAsync();
        using (var reply = await client.UploadAsync("setup.exe", [0x4D, 0x5A]))
            Assert.AreEqual(HttpStatusCode.Created, reply.StatusCode);

        string path = Path.Combine(Folder, "setup.exe");
        var attributes = File.GetAttributes(path);
        Assert.IsFalse(attributes.HasFlag(FileAttributes.Hidden), "only the partial file is hidden");
        Assert.IsTrue(attributes.HasFlag(FileAttributes.Archive));
        Assert.AreEqual(1, Directory.GetFileSystemEntries(Folder).Length, "no partial file is left");

        if (new DriveInfo(Path.GetPathRoot(path)!).DriveFormat != "NTFS")
            Assert.Inconclusive("The temporary folder is not on NTFS, so files cannot carry a Zone.Identifier stream.");
        string mark = File.ReadAllText(path + ":Zone.Identifier");
        StringAssert.Contains(mark, "[ZoneTransfer]");
        StringAssert.Contains(mark, "ZoneId=3");
    }

    [TestMethod]
    public async Task AnEmptyFileIsStillAFile()
    {
        using var client = await _server.PairAsync();
        using var reply = await client.UploadAsync("empty.txt", []);

        Assert.AreEqual(HttpStatusCode.Created, reply.StatusCode);
        Assert.AreEqual(0, new FileInfo(Path.Combine(Folder, "empty.txt")).Length);
    }

    [TestMethod]
    public async Task DownloadsCanBeResumedWithRanges()
    {
        using var client = await _server.PairAsync();
        using (var sent = await client.UploadAsync("alphabet.txt", Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwxyz")))
            Assert.AreEqual(HttpStatusCode.Created, sent.StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/files/alphabet.txt");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, 14);
        using (var part = await client.Http.SendAsync(request))
        {
            Assert.AreEqual(HttpStatusCode.PartialContent, part.StatusCode);
            Assert.AreEqual("klmno", await part.Content.ReadAsStringAsync());
            Assert.AreEqual("bytes 10-14/26", part.Content.Headers.ContentRange?.ToString());
        }

        request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/files/alphabet.txt");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(100, null);
        using (var beyond = await client.Http.SendAsync(request))
            Assert.AreEqual(HttpStatusCode.RequestedRangeNotSatisfiable, beyond.StatusCode);

        using (var whole = await client.Http.GetAsync("/api/v1/files/alphabet.txt"))
            Assert.AreEqual("bytes", whole.Headers.AcceptRanges.Single());
        Assert.AreEqual(1, _server.Notices.Count(n => n.Contains("Sending")), "a continued download is not announced again");
    }

    [TestMethod]
    public async Task OnlyPlainFilesDirectlyInTheFolderAreVisible()
    {
        using var client = await _server.PairAsync();
        Directory.CreateDirectory(Path.Combine(Folder, "sub-folder"));
        File.WriteAllText(Path.Combine(Folder, "sub-folder", "inner.txt"), "inner");
        File.WriteAllText(Path.Combine(Folder, "plain.txt"), "plain");
        foreach (var (name, attribute) in new[] { ("hidden.txt", FileAttributes.Hidden), ("system.txt", FileAttributes.System), ("desktop.ini", FileAttributes.Hidden | FileAttributes.System) })
        {
            File.WriteAllText(Path.Combine(Folder, name), "not for devices");
            File.SetAttributes(Path.Combine(Folder, name), attribute);
        }
        string partial = Path.Combine(Folder, TransferFolder.PartialPrefix + Guid.NewGuid().ToString("N") + ".part");
        File.WriteAllText(partial, "half an upload"); // even without the hidden attribute its name keeps it private

        var names = (await client.Http.GetFromJsonAsync<JsonElement>("/api/v1/files")).GetProperty("files")
            .EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToArray();
        CollectionAssert.AreEqual(new[] { "plain.txt" }, names);

        foreach (string name in new[] { "sub-folder", "inner.txt", "hidden.txt", "system.txt", "desktop.ini", Path.GetFileName(partial) })
        {
            using var fetched = await client.Http.GetAsync("/api/v1/files/" + Uri.EscapeDataString(name));
            Assert.AreEqual(HttpStatusCode.NotFound, fetched.StatusCode, name);
            using var removed = await client.SendAsync(HttpMethod.Delete, "/api/v1/files/" + Uri.EscapeDataString(name));
            Assert.AreEqual(HttpStatusCode.NotFound, removed.StatusCode, name);
        }
        Assert.AreEqual(6, Directory.GetFileSystemEntries(Folder).Length, "nothing was deleted");
        Assert.IsTrue(File.Exists(Path.Combine(Folder, "sub-folder", "inner.txt")));
    }

    [TestMethod]
    public async Task LinksAreNeverFollowed()
    {
        using var client = await _server.PairAsync();
        string secret = Path.Combine(Path.GetDirectoryName(Folder)!, "secret.txt");
        File.WriteAllText(secret, "TOP-SECRET");
        Directory.CreateDirectory(Folder);
        string link = Path.Combine(Folder, "link.txt");
        try { File.CreateSymbolicLink(link, secret); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Assert.Inconclusive("This account may not create symbolic links (needs Developer Mode or an elevated prompt)."); }

        Assert.AreEqual(0, (await client.Http.GetFromJsonAsync<JsonElement>("/api/v1/files")).GetProperty("files").GetArrayLength());
        using (var fetched = await client.Http.GetAsync("/api/v1/files/link.txt"))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, fetched.StatusCode);
            Assert.IsFalse((await fetched.Content.ReadAsStringAsync()).Contains("TOP-SECRET"));
        }
        using (var removed = await client.SendAsync(HttpMethod.Delete, "/api/v1/files/link.txt"))
            Assert.AreEqual(HttpStatusCode.NotFound, removed.StatusCode);
        Assert.IsTrue(File.Exists(secret) && File.Exists(link));
    }

    [TestMethod]
    public async Task AFileThatIsOpenOnThePcGivesAClearAnswer()
    {
        using var client = await _server.PairAsync();
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, "open in an editor.docx");
        using (new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            using var fetched = await client.Http.GetAsync("/api/v1/files/" + Uri.EscapeDataString(Path.GetFileName(path)));
            Assert.AreEqual(HttpStatusCode.Conflict, fetched.StatusCode);
            StringAssert.Contains((await fetched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), "in use");

            using var removed = await client.SendAsync(HttpMethod.Delete, "/api/v1/files/" + Uri.EscapeDataString(Path.GetFileName(path)));
            Assert.AreEqual(HttpStatusCode.Conflict, removed.StatusCode);
            Assert.IsTrue((await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!.Length > 0);
        }
        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(_server.Notices.Any(n => n.Contains("Sending") || n.Contains("deleted")), "nothing was sent or deleted");
    }

    [TestMethod]
    public async Task AFolderThatCannotBeCreatedIsAnErrorNotACrash()
    {
        using var client = await _server.PairAsync();
        File.WriteAllText(Folder, "a file is in the way of the folder");

        using var reply = await client.UploadAsync("photo.jpg", [1, 2, 3]);
        Assert.AreEqual(HttpStatusCode.InternalServerError, reply.StatusCode);
        Assert.IsTrue((await reply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!.Length > 0);
        Assert.AreEqual(0, (await client.Http.GetFromJsonAsync<JsonElement>("/api/v1/files")).GetProperty("files").GetArrayLength());
    }

    [TestMethod]
    public void TheRealFolderIsInDownloadsAndIsNotCreatedByAsking()
    {
        string path = TransferFolder.InDownloads().Directory;
        bool existedBefore = Directory.Exists(path);

        Assert.IsTrue(Path.IsPathFullyQualified(path));
        Assert.AreEqual("Abyss Remote", Path.GetFileName(path));
        Assert.AreEqual(existedBefore, Directory.Exists(path));
    }

    [TestMethod]
    public void FreeSpaceIsKnownEvenBeforeTheFolderExists()
    {
        string notYet = Path.Combine(Path.GetTempPath(), "abyss-missing-" + Guid.NewGuid().ToString("N"), "deeper", "Abyss Remote");
        long free = new TransferFolder(notYet).FreeBytes();

        Assert.IsTrue(free > 0 && free < long.MaxValue, $"{free} bytes");
        Assert.IsFalse(Directory.Exists(notYet));
    }
}

/// <summary>What the tests in this folder need for uploads.</summary>
internal static class TransferTestExtensions
{
    /// <summary>Sends a file the way the browser does: raw bytes, the name in the query string.</summary>
    public static Task<HttpResponseMessage> UploadAsync(this TestClient client, string name, byte[] content) =>
        client.SendAsync(HttpMethod.Post, "/api/v1/files?name=" + Uri.EscapeDataString(name), new ByteArrayContent(content));
}
