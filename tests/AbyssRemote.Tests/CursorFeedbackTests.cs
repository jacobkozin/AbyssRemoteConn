using System.Text.Json;
using AbyssRemote.Host;

namespace AbyssRemote.Tests;

[TestClass]
public sealed class CursorFeedbackTests
{
    private sealed class Cursor : ICursorSource
    {
        public int Reads;
        public bool Disposed;
        public CursorSnapshot Read() { Reads++; return new(-1200, 450, true, new CursorImage([1, 2, 3], 2, 3, 32, 32)); }
        public void Dispose() => Disposed = true;
    }

    [TestMethod]
    public async Task CursorFeedbackIsReadOnlyAndEchoesAppliedMovementWithoutVideo()
    {
        await using var server = await TestServer.StartAsync();
        var source = new Cursor(); server.Desktop.OnCreateCursor = () => source;
        using var client = await server.PairAsync();
        using var socket = await client.ConnectAsync("/api/v1/control");
        await socket.SendJsonAsync(new { kind = "pointer", display = 0, x = .2, y = .3, sequence = 7 });
        await socket.SendJsonAsync(new { kind = "cursor" });
        var reply = await socket.ReceiveMessageAsync();
        using var json = JsonDocument.Parse(reply.Data);
        var root = json.RootElement;
        Assert.AreEqual("cursorState", root.GetProperty("type").GetString());
        Assert.IsTrue(root.GetProperty("supported").GetBoolean());
        Assert.AreEqual(7, root.GetProperty("sequence").GetInt32());
        Assert.AreEqual(-1200, root.GetProperty("x").GetInt32());
        Assert.AreEqual("AQID", root.GetProperty("image").GetProperty("png").GetString());
        Assert.AreEqual(1, server.Desktop.Commands.Count, "reading the cursor must not inject input");
        Assert.IsNull(server.Desktop.LastFrameRequest, "cursor feedback must not need video capture");
        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        Assert.IsTrue(source.Disposed);
    }

    [TestMethod]
    public async Task UnsupportedCursorSourceKeepsLegacyVideoUsable()
    {
        await using var server = await TestServer.StartAsync();
        using var client = await server.PairAsync();
        using var socket = await client.ConnectAsync("/api/v1/control");
        await socket.SendJsonAsync(new { kind = "cursor" });
        using var reply = JsonDocument.Parse((await socket.ReceiveMessageAsync()).Data);
        Assert.IsFalse(reply.RootElement.GetProperty("supported").GetBoolean());
        Assert.AreEqual(0, server.Desktop.Commands.Count);
    }

    [TestMethod]
    public async Task SeparateCursorIsExplicitAndDoesNotChangeLegacyStreams()
    {
        await using var server = await TestServer.StartAsync();
        using var client = await server.PairAsync();
        foreach (var separate in new[] { true, false })
        {
            using var socket = await client.ConnectAsync("/api/v1/stream" + (separate ? "?cursor=separate" : ""));
            await socket.SendTextAsync("full"); await socket.ReceiveMessageAsync();
            Assert.AreEqual(!separate, server.Desktop.LastIncludeCursor);
            await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
    }
}
