using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using AbyssRemote.Host;
using AbyssRemote.Host.GameMode;

namespace AbyssRemote.Tests;

/// <summary>Game-mode protocol tests against the real server with a fake encoder.</summary>
[TestClass]
public sealed class GameModeProtocolTests
{
    private TestServer _server = null!;

    [TestInitialize]
    public async Task Start() => _server = await TestServer.StartAsync();

    [TestCleanup]
    public async Task Stop() => await _server.DisposeAsync();

    /// <summary>Next message that is not a periodic stats report.</summary>
    private static async Task<(WebSocketMessageType Type, byte[] Data)> NextAsync(WebSocket socket)
    {
        while (true)
        {
            var message = await socket.ReceiveMessageAsync();
            if (message.Type == WebSocketMessageType.Text && JsonDocument.Parse(message.Data).RootElement.GetProperty("type").GetString() == "stats")
                continue;
            return message;
        }
    }

    private static (bool Keyframe, uint Id) Header(byte[] frame)
    {
        Assert.AreEqual(1, frame[0], "message type 1 = video frame");
        return ((frame[1] & 1) != 0, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)));
    }

    [TestMethod]
    public async Task VideoIsFlowControlledByAcks()
    {
        using var client = await _server.PairAsync();
        using var socket = await client.ConnectAsync("/api/v1/video?display=0&fps=60");

        var (type, data) = await NextAsync(socket);
        Assert.AreEqual(WebSocketMessageType.Text, type);
        var config = JsonDocument.Parse(data).RootElement;
        Assert.AreEqual("config", config.GetProperty("type").GetString());
        Assert.AreEqual("avc1.64002a", config.GetProperty("codec").GetString());
        CollectionAssert.AreEqual(new byte[] { 1, 0x64, 0, 0x2A }, Convert.FromBase64String(config.GetProperty("description").GetString()!));

        (_, data) = await NextAsync(socket);
        Assert.AreEqual((true, 1u), Header(data));
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 1, 0x65, 0xAA }, data[VideoStream.HeaderSize..]);
        (_, data) = await NextAsync(socket);
        Assert.AreEqual((false, 2u), Header(data));

        // Two frames in flight, then nothing until the browser acknowledges one.
        await Task.Delay(300);
        Assert.AreEqual(VideoStream.FramesInFlight, _server.Desktop.LastVideo!.FramesProduced);

        await socket.SendTextAsync("ack 99"); // never sent: must not earn a credit
        await socket.SendTextAsync("key");
        await socket.SendTextAsync("ack 1");
        (_, data) = await NextAsync(socket);
        Assert.AreEqual((true, 3u), Header(data), "the frame after a keyframe request is a keyframe");
        Assert.AreEqual(3, _server.Desktop.LastVideo.FramesProduced);
        Assert.AreEqual(1, _server.Desktop.LastVideo.KeyframeRequests);
    }

    [TestMethod]
    public async Task VideoSocketHasTheSameSecurityGate()
    {
        using var client = await _server.PairAsync();
        await Assert.ThrowsAsync<WebSocketException>(() => client.ConnectAsync("/api/v1/video", origin: "https://evil.example"));
        await Assert.ThrowsAsync<WebSocketException>(() => client.ConnectAsync("/api/v1/video", csrf: new string('0', 64)));
        using var anonymous = _server.CreateClient();
        await Assert.ThrowsAsync<WebSocketException>(() => anonymous.ConnectAsync("/api/v1/video"));
        Assert.IsNull(_server.Desktop.LastVideo, "no encoder may start before the checks pass");
    }

    [TestMethod]
    public async Task UnsupportedPcsGetAClearCloseCode()
    {
        _server.Desktop.OnCreateVideo = _ => throw new VideoUnsupportedException("No hardware H.264 encoder.");
        using var client = await _server.PairAsync();
        using var socket = await client.ConnectAsync("/api/v1/video");

        Assert.AreEqual(CloseCodes.Unsupported, await socket.WaitForCloseAsync());
        Assert.AreEqual("No hardware H.264 encoder.", socket.CloseStatusDescription);
    }

    [TestMethod]
    public async Task ClosingTheSocketStopsTheEncoder()
    {
        using var client = await _server.PairAsync();
        var socket = await client.ConnectAsync("/api/v1/video");
        await NextAsync(socket);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        socket.Dispose();
        await Wait.UntilAsync(() => _server.Desktop.LastVideo!.Disposed);
    }

    [TestMethod]
    public async Task MeAdvertisesGameMode()
    {
        using var client = await _server.PairAsync();
        var me = await client.Http.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.IsTrue(me.GetProperty("features").GetProperty("gameMode").GetBoolean());
    }
}

[TestClass]
public sealed class VideoHelperTests
{
    private static readonly byte[] Stream =
    [
        0, 0, 0, 1, 0x67, 0x64, 0x00, 0x2A, 0xAC, // SPS (4-byte start code): High profile, level 4.2
        0, 0, 1, 0x68, 0xEE, 0x3C,                 // PPS (3-byte start code)
        0, 0, 0, 1, 0x65, 0x88, 0x84,              // IDR slice
    ];

    [TestMethod]
    public void FindsNalUnitsAfterBothStartCodeLengths() =>
        CollectionAssert.AreEqual(new[] { 4, 12, 19 }, H264.NalOffsets(Stream));

    [TestMethod]
    public void BuildsTheWebCodecsCodecStringFromTheSps()
    {
        Assert.AreEqual("avc1.64002a", H264.CodecString(Stream));
        Assert.IsNull(H264.CodecString([0, 0, 1, 0x41, 0x9A]), "a P-frame has no SPS");
    }

    [TestMethod]
    public void DetectsParameterSets()
    {
        Assert.IsTrue(H264.ContainsNal(Stream, H264.NalSps));
        Assert.IsTrue(H264.ContainsNal(Stream, H264.NalIdr));
        Assert.IsFalse(H264.ContainsNal([0, 0, 1, 0x41, 0x9A], H264.NalSps));
    }

    [TestMethod]
    public void ConvertsAnnexBToLengthPrefixedAvcc() =>
        CollectionAssert.AreEqual(new byte[]
        {
            0, 0, 0, 5, 0x67, 0x64, 0x00, 0x2A, 0xAC,
            0, 0, 0, 3, 0x68, 0xEE, 0x3C,
            0, 0, 0, 3, 0x65, 0x88, 0x84,
        }, H264.ToAvcc(Stream));

    [TestMethod]
    public void BuildsTheAvcDecoderConfigurationRecord()
    {
        CollectionAssert.AreEqual(new byte[]
        {
            1, 0x64, 0x00, 0x2A, 0xFF,          // version, High profile, constraints, level 4.2, 4-byte lengths
            0xE1, 0, 5, 0x67, 0x64, 0x00, 0x2A, 0xAC, // one SPS
            1, 0, 3, 0x68, 0xEE, 0x3C,           // one PPS
            0xFD, 0xF8, 0xF8, 0,                 // High profile: 4:2:0, 8-bit luma and chroma, no extensions
        }, H264.DecoderConfig(Stream));
        Assert.IsNull(H264.DecoderConfig([0, 0, 1, 0x41, 0x9A]));
    }

    [TestMethod]
    [DataRow(3840, 2160, 1080, 1920, 1080)]
    [DataRow(2560, 1440, 1080, 1920, 1080)]
    [DataRow(1920, 1080, 1440, 1920, 1080)] // never upscales
    [DataRow(3440, 1440, 1080, 2580, 1080)] // ultrawide keeps its shape
    [DataRow(1366, 768, 720, 1280, 720)]
    [DataRow(1280, 1023, 2160, 1278, 1022)] // NV12 needs even sizes; the width shrinks with the height
    public void StreamSizeKeepsAspectAndIsEven(int width, int height, int maxHeight, int expectedWidth, int expectedHeight) =>
        Assert.AreEqual((expectedWidth, expectedHeight), VideoConverter.OutputSize(width, height, maxHeight));
}
