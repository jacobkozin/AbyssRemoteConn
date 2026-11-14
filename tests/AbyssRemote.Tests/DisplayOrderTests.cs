using AbyssRemote.Host;

namespace AbyssRemote.Tests;

/// <summary>Reads the real monitors of the PC running the tests (read-only).</summary>
[TestClass]
public sealed class DisplayOrderTests
{
    [TestMethod]
    public void Displays_NumberThePrimaryMonitorZero_ThenLeftToRight()
    {
        var displays = new DesktopEngine().Displays();

        Assert.IsTrue(displays.Count > 0, "A PC that runs the tests has at least one monitor.");
        Assert.IsTrue(displays[0].Primary, "Monitor 0 must be the primary one.");
        Assert.AreEqual(1, displays.Count(d => d.Primary));
        CollectionAssert.AreEqual(Enumerable.Range(0, displays.Count).ToArray(), displays.Select(d => d.Id).ToArray());

        var others = displays.Skip(1).ToArray();
        var sorted = others.OrderBy(d => d.X).ThenBy(d => d.Y).ToArray();
        CollectionAssert.AreEqual(sorted, others, "The other monitors follow from left to right, then top to bottom.");
    }

    [TestMethod]
    public void Displays_GiveTheSameAnswerEveryTime()
    {
        var desktop = new DesktopEngine();
        CollectionAssert.AreEqual(desktop.Displays().ToArray(), desktop.Displays().ToArray());
    }
}
