using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AbyssRemote.Host;

public static class DesktopClipboard
{
    // WinForms Clipboard requires STA. Limit each operation; only text is supported in R1.
    public static string Read() => OnSta(() =>
    {
        if (!Clipboard.ContainsText(TextDataFormat.UnicodeText)) return "";
        string text = Clipboard.GetText(TextDataFormat.UnicodeText);
        return text[..Math.Min(4096, text.Length)];
    });

    public static void Write(string text)
    {
        if (text.Length > 4096) throw new ArgumentOutOfRangeException(nameof(text));
        OnSta(() => { if (text.Length > 0) Clipboard.SetText(text, TextDataFormat.UnicodeText); else Clipboard.Clear(); return true; });
    }

    private static T OnSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try { result = action(); return; }
                    catch (ExternalException) when (attempt < 2) { Thread.Sleep(80); }
                }
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true, Name = "Abyss Remote Clipboard STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(3))) throw new TimeoutException("Clipboard access timed out");
        if (failure is not null) throw new InvalidOperationException("Clipboard unavailable", failure);
        return result!;
    }
}
