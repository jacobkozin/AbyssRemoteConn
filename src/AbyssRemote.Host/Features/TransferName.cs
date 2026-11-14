using System.Globalization;
using System.Text;

namespace AbyssRemote.Host.Features;

/// <summary>Decides whether a file name sent by a browser may be used for an upload. The name
/// is untrusted, so it must be nothing more than the name of one ordinary file. A name that
/// fails is refused, never repaired: there is no cleaned-up form for an attacker to aim at.</summary>
public static class TransferName
{
    /// <summary>NTFS allows 255 characters, but the whole path (folder plus name) should stay
    /// under the 260 characters that Explorer and many programs still expect. 150 leaves room
    /// for the folder and for " (2)" when a name is already taken.</summary>
    public const int MaxLength = 150;

    public static bool IsSafe(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength) return false;

        // Windows drops trailing dots and spaces when it opens a file ("notes.txt." opens
        // "notes.txt"), and "." and ".." are folders. Space at either end only hides a name.
        if (char.IsWhiteSpace(name[0]) || char.IsWhiteSpace(name[^1]) || name[^1] == '.') return false;

        foreach (Rune character in name.EnumerateRunes())
            if (!IsAllowed(character)) return false;

        // Partial uploads use this prefix; nobody else may.
        return !IsDeviceName(name) && !name.StartsWith(TransferFolder.PartialPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowed(Rune character)
    {
        // Path separators, ':' (drive letters and "file:stream"), wildcards and the rest of
        // what Windows forbids in a file name.
        if (character.IsAscii && "\\/:*?\"<>|".Contains((char)character.Value)) return false;

        // Control characters could rewrite the host console; Unicode line breaks split a log line.
        if (Rune.IsControl(character)) return false;
        if (Rune.GetUnicodeCategory(character) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return false;

        // Characters that switch the reading direction can show "exe" as if it were "pdf".
        if (character.Value is (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069)) return false;

        // U+FFFD is what a broken character turns into: the name was damaged on its way here.
        return character != Rune.ReplacementChar;
    }

    /// <summary>CON, NUL, COM1 and friends are devices in every folder, with or without an
    /// extension ("nul.txt"). Opening one would write the upload into the void or wait on a
    /// serial port instead of creating a file.</summary>
    private static bool IsDeviceName(string name)
    {
        string stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") return true;
        // COM0-COM9 and LPT0-LPT9; Windows also accepts the superscript digits 1, 2 and 3.
        return stem.Length == 4 && (stem[..3] is "COM" or "LPT")
            && (char.IsAsciiDigit(stem[3]) || stem[3] is '¹' or '²' or '³');
    }
}
