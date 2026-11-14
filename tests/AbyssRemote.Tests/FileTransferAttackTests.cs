using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AbyssRemote.Host.Features;

namespace AbyssRemote.Tests;

/// <summary>Attacks on the transfer folder, through the real server. Next to the folder, in the
/// server's state folder, sit a canary file and the server's own settings.json (the paired
/// devices). No request may read, replace or delete either, or create anything out there.</summary>
[TestClass]
public sealed class FileTransferAttackTests
{
    private const string Canary = "TOP-SECRET-CANARY";
    private TestServer _server = null!;
    private TestClient _client = null!;

    private string Folder => _server.Transfer.Directory;
    private string Outside => Path.GetDirectoryName(Folder)!;
    private string Secret => Path.Combine(Outside, "secret.txt");

    [TestInitialize]
    public async Task Start()
    {
        _server = await TestServer.StartAsync();
        _client = await _server.PairAsync();
        File.WriteAllText(Secret, Canary);
    }

    [TestCleanup]
    public async Task Stop()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    /// <summary>A request whose path and query go out exactly as written. HttpClient normally
    /// tidies URLs ("/a/../b" becomes "/b"), which would hide the attack from the server.</summary>
    private HttpRequestMessage Raw(HttpMethod method, string pathAndQuery, HttpContent? content = null, bool http2 = false)
    {
        var uri = new Uri($"https://{_server.BaseUri.Authority}{pathAndQuery}", new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Add("Origin", _server.Origin);
        request.Headers.Add("X-Abyss-CSRF", _client.Csrf);
        if (http2) (request.Version, request.VersionPolicy) = (HttpVersion.Version20, HttpVersionPolicy.RequestVersionExact);
        return request;
    }

    private HttpRequestMessage Upload(string name, HttpContent content, bool http2 = false) =>
        Raw(HttpMethod.Post, "/api/v1/files?name=" + Uri.EscapeDataString(name), content, http2);

    private string[] Partials() =>
        Directory.Exists(Folder) ? Directory.GetFiles(Folder, TransferFolder.PartialPrefix + "*") : [];

    private string[] NamesOutside() =>
        Directory.EnumerateFileSystemEntries(Outside, "*", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(Outside, path)).Order(StringComparer.Ordinal).ToArray();

    private void AssertNothingOutsideWasTouched(string[] namesBefore)
    {
        Assert.AreEqual(Canary, File.ReadAllText(Secret), "the canary next to the folder was changed");
        CollectionAssert.AreEqual(namesBefore, NamesOutside(), "something was created or deleted outside the folder");
        StringAssert.Contains(File.ReadAllText(Path.Combine(Outside, "settings.json")), _client.DeviceId!);
    }

    /// <summary>Makes control and invisible characters readable in a failure message.</summary>
    private static string Show(string text) =>
        string.Concat(text.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"\\u{(int)c:X4}"));

    [TestMethod]
    public async Task StrangersAndForgedRequestsGetNowhere()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "keep.txt"), "keep me");

        using var stranger = _server.CreateClient();
        foreach (string route in new[] { "/api/v1/files", "/api/v1/files/keep.txt" })
        {
            using var reply = await stranger.Http.GetAsync(route);
            Assert.AreEqual(HttpStatusCode.Unauthorized, reply.StatusCode, route);
            Assert.IsFalse((await reply.Content.ReadAsStringAsync()).Contains("keep"));
        }
        await ForgedRequests.AssertAllRefusedAsync(_server, _client, HttpMethod.Post, "/api/v1/files?name=forged.txt", () => new ByteArrayContent(new byte[2000]));
        await ForgedRequests.AssertAllRefusedAsync(_server, _client, HttpMethod.Delete, "/api/v1/files/keep.txt");

        CollectionAssert.AreEqual(new[] { "keep.txt" }, Directory.GetFileSystemEntries(Folder).Select(Path.GetFileName).ToArray());
        Assert.AreEqual("keep me", File.ReadAllText(Path.Combine(Folder, "keep.txt")));
        Assert.IsFalse(_server.Notices.Any(n => n.Contains("Received") || n.Contains("Sending") || n.Contains("deleted")));
    }

    [TestMethod]
    public async Task UploadNamesThatAreNotPlainFileNamesAreRefused()
    {
        string[] names =
        [
            // Leaving the folder
            "../evil.txt", "..\\evil.txt", "../../../../Windows/evil.txt", "..", ".", "sub/evil.txt", "sub\\evil.txt",
            "/evil.txt", "\\evil.txt", "\\\\server\\share\\evil.txt", "\\\\?\\C:\\evil.txt", "\\\\.\\C:\\evil.txt",
            Path.Combine(Outside, "evil.txt"), Path.Combine(Outside, "evil.txt").Replace('\\', '/'), Secret,
            // Drives and alternate data streams
            "C:\\Windows\\evil.txt", "C:evil.txt", "C:", "evil.txt:stream", "evil.txt::$DATA", "evil.txt:Zone.Identifier", ":evil",
            // Devices that exist in every folder
            "CON", "con", "NUL", "nul.txt", "PRN.pdf", "AUX", "aux.h", "COM1", "com9.txt", "LPT1", "lpt0.log", "CONIN$", "CONOUT$",
            "COM\u00B9", "lpt\u00B2.txt", "NUL .txt", "con.tar.gz",
            // Names Windows would silently change
            "evil.", "evil ", " evil", "evil.txt.", "evil.txt ", "...", " ", "evil.txt\u00A0", "\u3000evil.txt",
            // Control characters, wildcards and other characters Windows forbids
            "a\0b.txt", "evil.txt\0.jpg", "a\nb.txt", "a\rb.txt", "a\tb.txt", "a\u001Bb.txt", "a\u007Fb.txt", "a\u0085b.txt",
            "*.txt", "a?b.txt", "a\"b.txt", "a<b.txt", "a>b.txt", "a|b.txt",
            // Unicode tricks: reversed reading direction, line breaks, a damaged character
            "photo\u202Egpj.exe", "a\u202Ab.txt", "a\u2066b.txt", "a\u2069b.txt", "a\u2028b.txt", "a\u2029b.txt", "a\uFFFDb.txt",
            // Too long, and the names kept for partial uploads
            new string('a', TransferName.MaxLength + 1), new string('a', 300) + ".txt",
            TransferFolder.PartialPrefix + "0123456789abcdef.part", TransferFolder.PartialPrefix.ToUpperInvariant() + "x",
        ];
        string[] before = NamesOutside();

        foreach (string name in names)
        {
            using var reply = await _client.Http.SendAsync(Upload(name, new ByteArrayContent(Encoding.UTF8.GetBytes("evil"))));
            Assert.AreEqual(HttpStatusCode.BadRequest, reply.StatusCode, Show(name));
            Assert.IsTrue((await reply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!.Length > 0, Show(name));
        }

        // The same with the name missing, empty, given twice, or sent in ways a browser would not.
        string[] queries =
        [
            "", "?name=", "?name=a.txt&name=b.txt", "?Name=..%2Fevil.txt", "?name=..%2fevil.txt", "?name=%2e%2e%5cevil.txt",
            "?name=evil.txt%00", "?name=" + new string('a', 6000),
        ];
        foreach (string query in queries)
        {
            using var reply = await _client.Http.SendAsync(Raw(HttpMethod.Post, "/api/v1/files" + query, new ByteArrayContent(Encoding.UTF8.GetBytes("evil"))));
            Assert.AreEqual(HttpStatusCode.BadRequest, reply.StatusCode, query.Length > 60 ? query[..60] : query);
        }

        Assert.IsFalse(Directory.Exists(Folder), "a refused upload must not even create the folder");
        AssertNothingOutsideWasTouched(before);
        Assert.IsFalse(_server.Notices.Any(n => n.Contains("Received")));
    }

    [TestMethod]
    public async Task OddButHarmlessNamesStayInsideTheFolder()
    {
        // Look-alikes of "/" , "\" and "." are ordinary characters to Windows, and text such as
        // "%2e%2e%2f" is only dangerous to a server that decodes twice. Each must simply
        // become a file with exactly that name, directly inside the folder.
        string[] names =
        [
            "..\uFF0F..\uFF0Fsecret.txt", "..\u2215secret.txt", "..\uFF3Csecret.txt", "\uFF0E\uFF0E\uFF0Fsecret.txt", "C\uFF1Asecret.txt",
            "%2e%2e%2fsecret.txt", "..%5Csecret.txt", "%252e%252e%252fsecret.txt", "secret.txt%00.jpg", "%u002e%u002e%u2215secret.txt",
            "\uFF43\uFF4F\uFF4E.txt", "COM10.txt", "CONSOLE.txt", "nul-terminated.txt", "..leading dots.txt", "a..b.txt",
            "-rf.txt", "~$draft.docx", "@file", "$MFT", "a;b.txt", "100% done.txt", "a&b=c.txt", "a+b.txt", "a#b.txt", "it's.txt",
            "{braces} [brackets] (parens).txt", "na\u00EFve caf\u00E9.txt", "\u540D\u524D.txt", "\u05E9\u05DC\u05D5\u05DD.txt",
            "family \U0001F468\u200D\U0001F469\u200D\U0001F467.png", new string('a', TransferName.MaxLength - 4) + ".txt",
        ];
        string[] before = [.. NamesOutside(), Path.GetFileName(Folder)];
        Array.Sort(before, StringComparer.Ordinal);

        foreach (string name in names)
        {
            using var reply = await _client.Http.SendAsync(Upload(name, new ByteArrayContent(Encoding.UTF8.GetBytes("content of " + name))));
            Assert.AreEqual(HttpStatusCode.Created, reply.StatusCode, Show(name));
            Assert.AreEqual(name, (await reply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString(), Show(name));
        }

        // %c0%af is an "over-long" way to write "/" that old servers decoded. It is not valid
        // text, so it is never decoded here: the name keeps those six characters as they are.
        const string overlong = "..%c0%afsecret.txt";
        using (var reply = await _client.Http.SendAsync(Raw(HttpMethod.Post, "/api/v1/files?name=" + overlong, new ByteArrayContent(Encoding.UTF8.GetBytes("content of " + overlong)))))
        {
            Assert.AreEqual(HttpStatusCode.Created, reply.StatusCode);
            Assert.AreEqual(overlong, (await reply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
        }
        names = [.. names, overlong];

        CollectionAssert.AreEquivalent(names, Directory.GetFiles(Folder).Select(Path.GetFileName).ToArray());
        Assert.AreEqual(names.Length, Directory.GetFileSystemEntries(Folder, "*", SearchOption.AllDirectories).Length, "no sub-folders, nothing extra");
        AssertNothingOutsideWasTouched(before);

        // Each one comes back, and goes away, under exactly the name the list shows.
        var listed = (await _client.Http.GetFromJsonAsync<JsonElement>("/api/v1/files")).GetProperty("files")
            .EnumerateArray().Select(f => f.GetProperty("name").GetString()!).ToArray();
        CollectionAssert.AreEquivalent(names, listed);
        foreach (string name in listed)
        {
            using (var fetched = await _client.Http.SendAsync(Raw(HttpMethod.Get, "/api/v1/files/" + Uri.EscapeDataString(name))))
            {
                Assert.AreEqual(HttpStatusCode.OK, fetched.StatusCode, Show(name));
                Assert.AreEqual("content of " + name, await fetched.Content.ReadAsStringAsync(), Show(name));
            }
            using var removed = await _client.Http.SendAsync(Raw(HttpMethod.Delete, "/api/v1/files/" + Uri.EscapeDataString(name)));
            Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode, Show(name));
        }
        Assert.AreEqual(0, Directory.GetFileSystemEntries(Folder).Length);
        AssertNothingOutsideWasTouched(before);
    }

    [TestMethod]
    public async Task DownloadAndDeleteReachOnlyRealFilesInTheFolder()
    {
        Directory.CreateDirectory(Path.Combine(Folder, "sub"));
        File.WriteAllText(Path.Combine(Folder, "sub", "inner.txt"), Canary);
        File.WriteAllText(Path.Combine(Folder, "real.txt"), "the one real file");
        string partial = TransferFolder.PartialPrefix + "0123456789abcdef0123456789abcdef.part";
        File.WriteAllText(Path.Combine(Folder, partial), Canary);
        File.SetAttributes(Path.Combine(Folder, partial), FileAttributes.Hidden);

        string absolute = Uri.EscapeDataString(Secret); // C%3A%5C...%5Csecret.txt
        string[] attacks =
        [
            // Up and out, in every spelling of "../"
            "..%2Fsecret.txt", "..%2fsecret.txt", "..%5Csecret.txt", "..%5csecret.txt", "%2e%2e%2fsecret.txt", "%2E%2E%5Csecret.txt",
            "..%252Fsecret.txt", "..%255Csecret.txt", "%252e%252e%252fsecret.txt", "../secret.txt", "..\\secret.txt", "%2e%2e/secret.txt",
            ".%2e/secret.txt", "..;/secret.txt", "..%2F..%2F..%2F..%2F..%2F..%2FWindows%2Fwin.ini", "..%5C..%5C..%5C..%5C..%5CWindows%5Cwin.ini",
            "..%c0%afsecret.txt", "..%c1%9csecret.txt", "..%ef%bc%8fsecret.txt", "..%e2%88%95secret.txt", "..%u2215secret.txt", "..%uff0fsecret.txt",
            "real.txt%2F..%2F..%2Fsecret.txt", "real.txt/../../secret.txt", "sub%2F..%2F..%2Fsecret.txt",
            // Down into a sub-folder
            "sub", "sub%2Finner.txt", "sub%5Cinner.txt", "sub/inner.txt", "sub\\inner.txt", "inner.txt",
            // Absolute paths, drives, devices and extended paths
            absolute, absolute.Replace("%5C", "%2F"), absolute.Replace("%3A", ":"), "C:secret.txt", "C%3Asecret.txt",
            "%5C%5C%3F%5C" + absolute, "%5C%5C.%5C" + absolute, "file%3A%2F%2F%2F" + absolute.Replace("%5C", "%2F"),
            "C%3A%5CWindows%5Cwin.ini", "%5CWindows%5Cwin.ini", "%2FWindows%2Fwin.ini",
            "NUL", "CON", "AUX", "COM1", "LPT1", "nul.txt", "CONIN$", "CONOUT$",
            // Other streams of the real file, and names Windows would "tidy" into it
            "real.txt::$DATA", "real.txt%3A%3A$DATA", "real.txt:Zone.Identifier", "real.txt%3AZone.Identifier", "real.txt:",
            "real.txt.", "real.txt..", "real.txt%20", "real.txt%20.", "real.txt%2E", "%20real.txt", "REAL.TXT", "Real.txt", "real.TXT",
            // Wildcards and patterns
            "*", "%2A", "*.txt", "real.*", "real.tx%3F", "re%3Fl.txt", "real%2A", "%3F%3F%3F%3F.txt", "real.txt%2A",
            // Nothing, dots, control characters, a damaged character
            ".", "..", "%2e", "%2e%2e", "...", "%20", "%00", "real.txt%00", "real.txt%00.jpg", "real.txt%0a", "real.txt%0d%0a", "%EF%BF%BD",
            // The partial upload, and names too long for any file
            partial, Uri.EscapeDataString(partial), partial.ToUpperInvariant(), new string('a', 300), new string('a', 5000) + ".txt",
        ];
        string[] before = NamesOutside();

        foreach (string attack in attacks)
        {
            string label = attack.Length > 80 ? attack[..80] + "..." : attack;
            using (var fetched = await _client.Http.SendAsync(Raw(HttpMethod.Get, "/api/v1/files/" + attack)))
            {
                string body = await fetched.Content.ReadAsStringAsync();
                Assert.IsFalse(body.Contains(Canary) || body.Contains("TokenHash") || body.Contains("the one real file"), $"GET {label} leaked a file");
                // "/files/." is just "/files/": the ordinary list of names, which this device may see anyway.
                bool isTheList = fetched.StatusCode == HttpStatusCode.OK && body.StartsWith("{\"folder\":");
                Assert.IsTrue(isTheList || fetched.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"GET {label} answered {(int)fetched.StatusCode}");
            }
            using var removed = await _client.Http.SendAsync(Raw(HttpMethod.Delete, "/api/v1/files/" + attack));
            Assert.IsTrue(removed.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.MethodNotAllowed,
                $"DELETE {label} answered {(int)removed.StatusCode}");
        }

        AssertNothingOutsideWasTouched(before);
        Assert.AreEqual("the one real file", File.ReadAllText(Path.Combine(Folder, "real.txt")));
        Assert.AreEqual(Canary, File.ReadAllText(Path.Combine(Folder, "sub", "inner.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(Folder, partial)));
        Assert.IsFalse(_server.Notices.Any(n => n.Contains("Sending") || n.Contains("deleted")));

        // The honest request still works.
        using var real = await _client.Http.GetAsync("/api/v1/files/real.txt");
        Assert.AreEqual("the one real file", await real.Content.ReadAsStringAsync());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string longPath, char[] buffer, uint bufferLength);

    [TestMethod]
    public async Task AnOldStyleShortNameIsNotASecondWayIn()
    {
        Directory.CreateDirectory(Folder);
        string longPath = Path.Combine(Folder, "a rather long file name.txt");
        File.WriteAllText(longPath, "long name");
        var buffer = new char[260];
        uint length = GetShortPathNameW(longPath, buffer, (uint)buffer.Length);
        string alias = Path.GetFileName(new string(buffer, 0, (int)length));
        if (length == 0 || alias.Equals(Path.GetFileName(longPath), StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive("This drive does not give files 8.3 short names.");

        // Windows itself would open the file under its alias (for example ARATHE~1.TXT).
        Assert.AreEqual("long name", File.ReadAllText(Path.Combine(Folder, alias)));

        using (var fetched = await _client.Http.GetAsync("/api/v1/files/" + Uri.EscapeDataString(alias)))
            Assert.AreEqual(HttpStatusCode.NotFound, fetched.StatusCode);
        using (var removed = await _client.SendAsync(HttpMethod.Delete, "/api/v1/files/" + Uri.EscapeDataString(alias)))
            Assert.AreEqual(HttpStatusCode.NotFound, removed.StatusCode);
        using (var sent = await _client.UploadAsync(alias, Encoding.UTF8.GetBytes("uploaded under the alias")))
        {
            Assert.AreEqual(HttpStatusCode.Created, sent.StatusCode);
            Assert.AreNotEqual(alias, (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString(), "the alias is taken, so the upload gets a number");
        }
        Assert.AreEqual("long name", File.ReadAllText(longPath), "the file behind the alias must not be replaced");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OversizedUploadsAreRefused(bool http2)
    {
        _server.Transfer.MaxUploadBytes = 1000;
        var tooBig = new byte[4000];

        // The request says up front how big it is: refused before a single byte is read.
        using (var declared = await _client.Http.SendAsync(Upload("declared.bin", new ByteArrayContent(tooBig), http2)))
        {
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
            StringAssert.Contains((await declared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), "too large");
        }
        Assert.IsFalse(Directory.Exists(Folder), "nothing may be written for an upload that announced it was too big");

        // The request does not say: it is cut off as soon as it passes the limit.
        using (var undeclared = await _client.Http.SendAsync(Upload("undeclared.bin", new UnknownLengthContent(tooBig), http2)))
        {
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, undeclared.StatusCode);
            StringAssert.Contains((await undeclared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), "too large");
        }
        await Wait.UntilAsync(() => Directory.GetFileSystemEntries(Folder).Length == 0);

        // Exactly the limit is fine, declared or not.
        using (var fits = await _client.Http.SendAsync(Upload("fits.bin", new ByteArrayContent(new byte[1000]), http2)))
            Assert.AreEqual(HttpStatusCode.Created, fits.StatusCode);
        using (var fits = await _client.Http.SendAsync(Upload("fits too.bin", new UnknownLengthContent(new byte[1000]), http2)))
            Assert.AreEqual(HttpStatusCode.Created, fits.StatusCode);

        CollectionAssert.AreEquivalent(new[] { "fits.bin", "fits too.bin" }, Directory.GetFileSystemEntries(Folder).Select(Path.GetFileName).ToArray());
        Assert.AreEqual(1000, (await _client.Http.GetFromJsonAsync<JsonElement>("/api/v1/files")).GetProperty("maxUploadBytes").GetInt64());
    }

    [TestMethod]
    public async Task OtherRequestsKeepTheSmallBodyLimit()
    {
        // Raising the limit for uploads must not raise it anywhere else.
        using var reply = await _client.PostJsonAsync("/api/v1/clipboard", new { text = new string('x', 100_000) });
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, reply.StatusCode);
        Assert.AreEqual("", _server.Clipboard.Text);
    }

    [TestMethod]
    public async Task AnUploadIsRefusedWhenTheDriveIsTooFull()
    {
        _server.Transfer.FreeBytes = () => 100;

        using (var reply = await _client.UploadAsync("video.mp4", new byte[101]))
        {
            Assert.AreEqual(HttpStatusCode.InsufficientStorage, reply.StatusCode);
            StringAssert.Contains((await reply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), "free space");
        }
        Assert.IsFalse(Directory.Exists(Folder));

        using (var fits = await _client.UploadAsync("note.txt", new byte[100]))
            Assert.AreEqual(HttpStatusCode.Created, fits.StatusCode);
    }

    [TestMethod]
    [DataRow(null)]      // the body does not say how long it is
    [DataRow(1_000_000)] // the body promised a megabyte and stops after a few kilobytes
    public async Task ACancelledUploadLeavesNothingBehind(int? declaredLength)
    {
        using var cancel = new CancellationTokenSource();
        var never = new TaskCompletionSource();
        var sending = _client.Http.SendAsync(Upload("holiday video.mp4", new StalledUpload(new byte[5000], never.Task, declaredLength)), cancel.Token);

        await Wait.UntilAsync(() => Partials().Length == 1);
        string partial = Partials()[0];
        Assert.IsTrue(File.GetAttributes(partial).HasFlag(FileAttributes.Hidden), "an unfinished upload is hidden on the PC");

        // While it is arriving, no device can see, fetch or delete it.
        Assert.AreEqual(0, (await _client.Http.GetFromJsonAsync<JsonElement>("/api/v1/files")).GetProperty("files").GetArrayLength());
        using (var fetched = await _client.Http.GetAsync("/api/v1/files/" + Path.GetFileName(partial)))
            Assert.AreEqual(HttpStatusCode.NotFound, fetched.StatusCode);
        using (var removed = await _client.SendAsync(HttpMethod.Delete, "/api/v1/files/" + Path.GetFileName(partial)))
            Assert.AreEqual(HttpStatusCode.NotFound, removed.StatusCode);
        Assert.IsTrue(File.Exists(partial));

        cancel.Cancel(); // the phone gives up, or walks out of Wi-Fi range
        await Assert.ThrowsAsync<OperationCanceledException>(() => sending);

        await Wait.UntilAsync(() => Directory.GetFileSystemEntries(Folder).Length == 0);
        Assert.IsFalse(_server.Notices.Any(n => n.Contains("Received")));
    }

    [TestMethod]
    public async Task UnfinishedUploadsAreClearedButNeverARunningOne()
    {
        // Left behind by a host that was closed in the middle of an upload.
        Directory.CreateDirectory(Folder);
        string abandoned = Path.Combine(Folder, TransferFolder.PartialPrefix + "00000000000000000000000000000000.part");
        File.WriteAllBytes(abandoned, new byte[10]);
        File.SetAttributes(abandoned, FileAttributes.Hidden);

        var finish = new TaskCompletionSource();
        var slow = _client.Http.SendAsync(Upload("slow.bin", new StalledUpload(new byte[5000], finish.Task, declaredLength: null)));
        await Wait.UntilAsync(() => !File.Exists(abandoned) && Partials().Length == 1);
        string running = Partials()[0];

        // A second upload starts and finishes meanwhile; its clean-up must leave the first alone.
        using (var quick = await _client.UploadAsync("quick.txt", [1, 2, 3]))
            Assert.AreEqual(HttpStatusCode.Created, quick.StatusCode);
        Assert.IsTrue(File.Exists(running), "a running upload was deleted by another upload");

        finish.SetResult();
        using (var done = await slow)
            Assert.AreEqual(HttpStatusCode.Created, done.StatusCode);
        CollectionAssert.AreEquivalent(new[] { "quick.txt", "slow.bin" }, Directory.GetFileSystemEntries(Folder).Select(Path.GetFileName).ToArray());
        Assert.AreEqual(5000, new FileInfo(Path.Combine(Folder, "slow.bin")).Length);
    }

    /// <summary>A body of unknown length: HTTP/1.1 sends it chunked, HTTP/2 without a content-length.</summary>
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    /// <summary>Sends its first bytes, then waits: an upload that is under way.</summary>
    private sealed class StalledUpload(byte[] firstBytes, Task resume, int? declaredLength) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        // HttpClient waits for this to end before a cancelled request returns, so it must stop when asked.
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancel)
        {
            await stream.WriteAsync(firstBytes, cancel);
            await stream.FlushAsync(cancel);
            await resume.WaitAsync(cancel);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = declaredLength ?? 0;
            return declaredLength is not null;
        }
    }
}

/// <summary>The folder class on its own, without a server.</summary>
[TestClass]
public sealed class TransferFolderTests
{
    private string _dir = "";

    [TestInitialize]
    public void CreateFolder() => _dir = Directory.CreateTempSubdirectory("abyss-transfer-test-").FullName;

    [TestCleanup]
    public void DeleteFolder() => Directory.Delete(_dir, recursive: true);

    [TestMethod]
    public async Task WhateverGoesWrongThePartialFileIsRemoved()
    {
        var folder = new TransferFolder(Path.Combine(_dir, "transfer"));

        await Assert.ThrowsExactlyAsync<IOException>(() => folder.SaveAsync("video.mp4", new FailingStream(bytesFirst: 200_000), CancellationToken.None));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(folder.Directory).Length, "after a connection that broke");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => folder.SaveAsync("video.mp4", new MemoryStream(new byte[10]), cancelled.Token));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(folder.Directory).Length, "after a cancelled upload");

        folder.MaxUploadBytes = 10;
        await Assert.ThrowsExactlyAsync<UploadTooLargeException>(() => folder.SaveAsync("video.mp4", new MemoryStream(new byte[11]), CancellationToken.None));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(folder.Directory).Length, "after an upload that was too large");

        var (name, size) = await folder.SaveAsync("video.mp4", new MemoryStream(new byte[10]), CancellationToken.None);
        Assert.AreEqual(("video.mp4", 10L), (name, size));
        Assert.AreEqual(1, Directory.GetFileSystemEntries(folder.Directory).Length);
    }

    [TestMethod]
    public async Task TheFolderRefusesUnsafeNamesEvenWithoutTheWebServer()
    {
        var folder = new TransferFolder(Path.Combine(_dir, "transfer"));
        foreach (string name in new[] { "..\\evil.txt", "../evil.txt", Path.Combine(_dir, "evil.txt"), "evil.txt:stream", "NUL", "" })
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => folder.SaveAsync(name, new MemoryStream([1]), CancellationToken.None));

        Assert.IsFalse(Directory.Exists(folder.Directory));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(_dir).Length);
    }

    [TestMethod]
    public void FindingAFileNeverTurnsTheNameIntoAPath()
    {
        var folder = new TransferFolder(Path.Combine(_dir, "transfer"));
        Directory.CreateDirectory(folder.Directory);
        File.WriteAllText(Path.Combine(_dir, "outside.txt"), "outside");
        File.WriteAllText(Path.Combine(folder.Directory, "inside.txt"), "inside");

        Assert.AreEqual("inside.txt", folder.Find("inside.txt")?.Name);
        foreach (string name in new[] { "..\\outside.txt", "../outside.txt", Path.Combine(_dir, "outside.txt"), "inside.txt.", "inside.txt ", "INSIDE.TXT", "inside.txt::$DATA", "*.txt", "inside.tx?", "" })
            Assert.IsNull(folder.Find(name), name);
    }

    [TestMethod]
    public void LinksFoldersAndHiddenOrSystemFilesAreNotVisible()
    {
        // A symbolic link is a file with the ReparsePoint attribute. Not every account may
        // create one for a test, so the rule itself is checked here.
        foreach (var plain in new[] { FileAttributes.Normal, FileAttributes.Archive, FileAttributes.Archive | FileAttributes.ReadOnly })
            Assert.IsTrue(TransferFolder.IsVisible(plain, "photo.jpg"), plain.ToString());
        foreach (var other in new[] { FileAttributes.ReparsePoint, FileAttributes.Directory, FileAttributes.Hidden, FileAttributes.System, FileAttributes.Device })
            Assert.IsFalse(TransferFolder.IsVisible(FileAttributes.Archive | other, "photo.jpg"), other.ToString());
        Assert.IsFalse(TransferFolder.IsVisible(FileAttributes.Archive, TransferFolder.PartialPrefix + "x.part"));
        Assert.IsFalse(TransferFolder.IsVisible(FileAttributes.Archive, TransferFolder.PartialPrefix.ToUpperInvariant() + "X.PART"));
    }

    /// <summary>Delivers some bytes, then fails like a connection that was cut.</summary>
    private sealed class FailingStream(int bytesFirst) : MemoryStream(new byte[bytesFirst])
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
        {
            int read = await base.ReadAsync(buffer, cancel);
            return read > 0 ? read : throw new IOException("The connection was reset.");
        }
    }
}

[TestClass]
public sealed class TransferNameTests
{
    [TestMethod]
    [DataRow("photo.jpg")]
    [DataRow("IMG_0042.HEIC")]
    [DataRow("Screenshot 2026-11-13 at 14.05.22.png")]
    [DataRow("report (final) v2.pdf")]
    [DataRow("archive.tar.gz")]
    [DataRow("README")]
    [DataRow(".gitignore")]
    [DataRow("100% done.txt")]
    [DataRow("COM10.txt")]      // only COM0 to COM9 are devices
    [DataRow("console.log")]
    [DataRow("null.txt")]
    [DataRow("a..b")]
    public void OrdinaryNamesAreAccepted(string name) => Assert.IsTrue(TransferName.IsSafe(name));

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(".")]
    [DataRow("..")]
    [DataRow("../x")]
    [DataRow("..\\x")]
    [DataRow("a/b")]
    [DataRow("a\\b")]
    [DataRow("C:x")]
    [DataRow("x:y")]
    [DataRow("x.")]
    [DataRow("x ")]
    [DataRow(" x")]
    [DataRow("CON")]
    [DataRow("nul")]
    [DataRow("Nul.txt")]
    [DataRow("com1.jpg")]
    [DataRow("LPT9")]
    [DataRow("aux .txt")]
    [DataRow("x*")]
    [DataRow("x?")]
    [DataRow("x|y")]
    public void NamesThatAreMoreThanAFileNameAreRefused(string? name) => Assert.IsFalse(TransferName.IsSafe(name));

    [TestMethod]
    public void EveryControlCharacterIsRefusedAnywhereInTheName()
    {
        for (int c = 0; c <= 0x9F; c++)
        {
            if (!char.IsControl((char)c)) continue;
            foreach (string name in new[] { $"{(char)c}a.txt", $"a{(char)c}b.txt", $"a.txt{(char)c}" })
                Assert.IsFalse(TransferName.IsSafe(name), $"U+{c:X4}");
        }
    }

    [TestMethod]
    public void TheLengthLimitCountsTheWholeName()
    {
        Assert.IsTrue(TransferName.IsSafe(new string('a', TransferName.MaxLength)));
        Assert.IsFalse(TransferName.IsSafe(new string('a', TransferName.MaxLength + 1)));
        Assert.IsTrue(TransferName.IsSafe(new string('a', TransferName.MaxLength - 4) + ".txt"));
        Assert.IsFalse(TransferName.IsSafe(new string('a', TransferName.MaxLength - 3) + ".txt"));
    }
}
