using System.Runtime.InteropServices;
using AbyssRemote.Host;

namespace AbyssRemote.Tests;

/// <summary>The phone once showed no mouse pointer at all: the host sent a blank cursor
/// picture, and went on sending it because it only looked at a cursor again when the handle
/// changed. A cursor hider swaps a system cursor's picture without changing its handle.
/// These tests use cursors made here, so they do not depend on what the PC's own cursors
/// look like at the moment.</summary>
[TestClass]
public sealed class CursorPictureTests
{
    [DllImport("user32.dll")] private static extern IntPtr CreateCursor(IntPtr instance, int hotX, int hotY, int width, int height, byte[] andMask, byte[] xorMask);
    [DllImport("user32.dll")] private static extern bool DestroyCursor(IntPtr cursor);

    private const int Size = 32;

    /// <summary>A 32x32 cursor from a rule per pixel: 'B' black, 'W' white, 'X' inverts the
    /// screen, anything else leaves the screen alone.</summary>
    private static IntPtr MakeCursor(Func<int, int, char> pixel, int hotX = 3, int hotY = 5)
    {
        var and = new byte[Size * Size / 8];
        var xor = new byte[Size * Size / 8];
        for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++)
        {
            char kind = pixel(x, y);
            int index = y * (Size / 8) + x / 8;
            byte bit = (byte)(0x80 >> (x % 8));
            if (kind is not ('B' or 'W')) and[index] |= bit;   // AND 1 keeps the screen pixel
            if (kind is 'W' or 'X') xor[index] |= bit;           // XOR 1 turns it white, or inverts it
        }
        IntPtr cursor = CreateCursor(IntPtr.Zero, hotX, hotY, Size, Size, and, xor);
        Assert.AreNotEqual(IntPtr.Zero, cursor);
        return cursor;
    }

    private static char Sample(int x, int y) =>
        y < 8 ? (x < 8 ? 'B' : x < 16 ? 'W' : '.') :
        y is >= 12 and < 20 && x is >= 4 and < 12 ? 'X' : '.';

    [TestMethod]
    public void ACursorBecomesAPictureWithItsColoursAndClearBackground()
    {
        IntPtr cursor = MakeCursor(Sample);
        try
        {
            var picture = WindowsCursorSource.Picture(cursor);

            Assert.IsNotNull(picture);
            Assert.AreEqual((Size, Size, 3, 5), (picture.Width, picture.Height, picture.HotX, picture.HotY));
            uint At(int x, int y) => (uint)picture.Argb[y * Size + x];
            Assert.AreEqual(0xFF000000u, At(2, 2), "black stays solid black");
            Assert.AreEqual(0xFFFFFFFFu, At(10, 2), "white stays solid white");
            Assert.AreEqual(0u, At(28, 28) >> 24, "untouched pixels are clear");
            Assert.AreEqual(0xFFFFFFFFu, At(6, 14), "pixels that invert the screen become white");
            Assert.AreEqual(0xFF000000u, At(3, 14), "with a dark outline, so they show on white too");

            var image = WindowsCursorSource.Image(cursor);
            Assert.IsNotNull(image);
            using var decoded = new System.Drawing.Bitmap(new MemoryStream(image.Png));
            Assert.AreEqual(255, decoded.GetPixel(10, 2).A);
            Assert.AreEqual(0, decoded.GetPixel(28, 28).A);
        }
        finally { DestroyCursor(cursor); }
    }

    [TestMethod]
    public void ACursorWithNothingToSeeGivesNoPicture()
    {
        IntPtr blank = MakeCursor((_, _) => '.');
        try
        {
            Assert.IsNull(WindowsCursorSource.Picture(blank));
            Assert.IsNull(WindowsCursorSource.Image(blank));
        }
        finally { DestroyCursor(blank); }
    }

    [TestMethod]
    public void TheSameHandleIsLookedAtAgain_SoAHiddenPointerComesBack()
    {
        var arrow = new CursorPicture([unchecked((int)0xFFFFFFFF), 0], 2, 1, 0, 0);
        CursorPicture? shown = null; // what the one handle looks like right now
        long now = 0;
        int looks = 0;
        using var source = new WindowsCursorSource(_ => { looks++; return shown; }, () => now);
        var handle = new IntPtr(65539);

        // The pointer is blank while the mouse rests: reported as not visible, no picture sent.
        var first = source.Describe(handle, 10, 20);
        Assert.IsFalse(first.Visible);
        Assert.IsNull(first.Image);
        Assert.AreEqual((10, 20), (first.X, first.Y));

        // The mouse moves and the hider restores the picture under the same handle.
        shown = arrow;
        now += 40;
        Assert.IsFalse(source.Describe(handle, 11, 20).Visible, "not looked at again yet");
        Assert.AreEqual(1, looks, "a look costs drawing, so it is not repeated for every request");

        now += 70;
        var back = source.Describe(handle, 12, 20);
        Assert.IsTrue(back.Visible, "the pointer must come back without a change of handle");
        Assert.IsNotNull(back.Image, "and its picture must be sent");

        // Unchanged picture: looked at again, but not sent again.
        now += 150;
        var same = source.Describe(handle, 13, 20);
        Assert.IsTrue(same.Visible);
        Assert.IsNull(same.Image);

        // Hidden and shown once more: visible again, and the browser already has that picture.
        shown = null; now += 150;
        Assert.IsFalse(source.Describe(handle, 13, 20).Visible);
        shown = arrow; now += 150;
        var again = source.Describe(handle, 13, 20);
        Assert.IsTrue(again.Visible);
        Assert.IsNull(again.Image);
    }

    [TestMethod]
    public void ANewHandleIsLookedAtAtOnce_AndADifferentPictureIsSent()
    {
        var arrow = new CursorPicture([unchecked((int)0xFFFFFFFF), 0], 2, 1, 0, 0);
        var beam = new CursorPicture([0, unchecked((int)0xFF000000)], 2, 1, 1, 0);
        long now = 0;
        using var source = new WindowsCursorSource(handle => handle == new IntPtr(1) ? arrow : beam, () => now);

        Assert.IsNotNull(source.Describe(new IntPtr(1), 0, 0).Image);
        now += 5; // far sooner than the regular second look
        var changed = source.Describe(new IntPtr(2), 0, 0);
        Assert.IsNotNull(changed.Image);
        Assert.AreEqual(1, changed.Image.HotX);
        now += 5;
        Assert.IsNotNull(source.Describe(new IntPtr(1), 0, 0).Image, "back to the arrow: the browser shows the beam, so send it");
    }
}
