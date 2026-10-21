using AbyssRemote.Host;

namespace AbyssRemote.Tests;

[TestClass]
public sealed class PairingManagerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void CorrectCodePairsExactlyOnce()
    {
        var pairing = new PairingManager(new ManualClock(Start));
        string code = pairing.Rotate();

        Assert.AreEqual(PairingResult.Paired, pairing.TryUse(code, "10.0.0.2"));
        Assert.AreEqual(PairingResult.WrongOrExpired, pairing.TryUse(code, "10.0.0.3"));
    }

    [TestMethod]
    public void NoCodeExistsUntilTheHostCreatesOne()
    {
        var pairing = new PairingManager(new ManualClock(Start));
        Assert.AreEqual(PairingResult.WrongOrExpired, pairing.TryUse("00000000", "10.0.0.2"));
    }

    [TestMethod]
    public void CodeExpiresAfterTenMinutes()
    {
        var clock = new ManualClock(Start);
        var pairing = new PairingManager(clock);
        string code = pairing.Rotate();

        clock.Advance(PairingManager.CodeLifetime);
        Assert.AreEqual(PairingResult.WrongOrExpired, pairing.TryUse(code, "10.0.0.2"));
    }

    [TestMethod]
    [DataRow("1234567")]
    [DataRow("123456789")]
    [DataRow("1234abcd")]
    [DataRow("")]
    public void MalformedCodesNeverMatch(string code)
    {
        var pairing = new PairingManager(new ManualClock(Start));
        pairing.Rotate();
        Assert.AreEqual(PairingResult.WrongOrExpired, pairing.TryUse(code, "10.0.0.2"));
    }

    [TestMethod]
    public void EachAddressGetsFiveTriesPerFiveMinutes()
    {
        var clock = new ManualClock(Start);
        var pairing = new PairingManager(clock);
        string code = pairing.Rotate();

        for (int i = 0; i < PairingManager.AttemptsPerAddress; i++)
            Assert.AreEqual(PairingResult.WrongOrExpired, pairing.TryUse("00000000", "10.0.0.2"));
        // Even the right code is refused while this address is limited...
        Assert.AreEqual(PairingResult.TooManyAttempts, pairing.TryUse(code, "10.0.0.2"));

        // ...but works once the window has passed.
        clock.Advance(PairingManager.AttemptWindow);
        Assert.AreEqual(PairingResult.Paired, pairing.TryUse(code, "10.0.0.2"));
    }

    [TestMethod]
    public void TwentyWrongGuessesFromManyAddressesDestroyTheCode()
    {
        var pairing = new PairingManager(new ManualClock(Start));
        int lockedEvents = 0;
        pairing.CodeLocked += () => lockedEvents++;
        string code = pairing.Rotate();
        string wrong = code == "00000000" ? "11111111" : "00000000";

        for (int i = 1; i < PairingManager.MaxFailuresPerCode; i++)
            Assert.AreEqual(PairingResult.WrongOrExpired, pairing.TryUse(wrong, $"10.0.{i}.1"));
        Assert.AreEqual(PairingResult.Locked, pairing.TryUse(wrong, "10.0.99.1"));

        // The real code is now useless, and the console was told exactly once.
        Assert.AreEqual(PairingResult.Locked, pairing.TryUse(code, "10.0.100.1"));
        Assert.AreEqual(1, lockedEvents);

        // A fresh code from the host console works again.
        Assert.AreEqual(PairingResult.Paired, pairing.TryUse(pairing.Rotate(), "10.0.100.1"));
    }

    [TestMethod]
    public void CodesAreEightDigits()
    {
        var pairing = new PairingManager();
        for (int i = 0; i < 50; i++)
        {
            string code = pairing.Rotate();
            Assert.AreEqual(8, code.Length);
            Assert.IsTrue(code.All(char.IsAsciiDigit));
        }
    }
}
