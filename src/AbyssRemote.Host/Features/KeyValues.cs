using System.Text;

namespace AbyssRemote.Host.Features;

/// <summary>One entry of Valve's KeyValues text format. Steam keeps its list of library folders
/// (libraryfolders.vdf) and one small file per installed game (appmanifest_*.acf) in it. An
/// entry is either a piece of text or a group of named entries, and groups nest:
/// <code>
/// "AppState"
/// {
///     "appid"       "620"
///     "name"        "Portal 2"       // a comment
///     "UserConfig"  { "language"  "english" }
/// }
/// </code>
/// Only what those two kinds of file use is understood: quoted text with backslash escapes,
/// bare words, nested braces and // comments. A file that does not follow the format all the
/// way to its end is refused as a whole, because Steam may be in the middle of writing it and
/// half a file is worse than none.</summary>
public sealed class KeyValues
{
    /// <summary>Far deeper than any real Steam file. The reader calls itself once per level, so
    /// without a limit a file made of nothing but "{" could use up the stack and end the program.</summary>
    public const int MaxDepth = 32;

    private readonly List<KeyValuePair<string, KeyValues>> _children = new();

    private KeyValues(string? value) => Value = value;

    /// <summary>The text of a plain entry. Null for a group.</summary>
    public string? Value { get; }

    /// <summary>The entries of a group, in the order of the file. Empty for a plain entry.</summary>
    public IReadOnlyList<KeyValuePair<string, KeyValues>> Children => _children;

    /// <summary>The first entry with this name, or null. Names ignore case, as they do in Steam.</summary>
    public KeyValues? Child(string name)
    {
        foreach (var (key, entry) in _children)
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase)) return entry;
        return null;
    }

    /// <summary>The text of the plain entry with this name. Null when it is missing or a group.</summary>
    public string? Text(string name) => Child(name)?.Value;

    /// <summary>Reads a whole file. The result is a group holding the file's top-level entries,
    /// or null when the text is not well-formed.</summary>
    public static KeyValues? Parse(string text)
    {
        var root = new KeyValues(null);
        return new Reader(text).ReadEntries(root, depth: 0) ? root : null;
    }

    private enum Token { End, Open, Close, Text, Broken }

    private sealed class Reader(string text)
    {
        private int _at;

        /// <summary>Reads "name value" pairs into a group until its closing brace, or until the
        /// end of the text for the top level. False when the text breaks the format.</summary>
        public bool ReadEntries(KeyValues group, int depth)
        {
            while (true)
            {
                var token = Next(out string name);
                if (token == Token.End) return depth == 0;   // inside a group, the file was cut off
                if (token == Token.Close) return depth > 0;  // at the top level there is nothing to close
                if (token != Token.Text) return false;

                token = Next(out string value);
                if (token == Token.Text)
                {
                    group._children.Add(new(name, new KeyValues(value)));
                }
                else if (token == Token.Open && depth < MaxDepth)
                {
                    var child = new KeyValues(null);
                    if (!ReadEntries(child, depth + 1)) return false;
                    group._children.Add(new(name, child));
                }
                else return false;                           // a name with nothing after it
            }
        }

        private Token Next(out string word)
        {
            word = "";
            SkipSpaceAndComments();
            if (_at >= text.Length) return Token.End;
            switch (text[_at])
            {
                case '{': _at++; return Token.Open;
                case '}': _at++; return Token.Close;
                case '"': return ReadQuoted(out word) ? Token.Text : Token.Broken;
            }
            // A bare word runs until white space, a brace or a quote.
            int start = _at;
            while (_at < text.Length && !char.IsWhiteSpace(text[_at]) && text[_at] is not ('{' or '}' or '"')) _at++;
            word = text[start.._at];
            return Token.Text;
        }

        private void SkipSpaceAndComments()
        {
            while (_at < text.Length)
            {
                if (char.IsWhiteSpace(text[_at])) _at++;
                else if (text[_at] == '/' && _at + 1 < text.Length && text[_at + 1] == '/')
                    while (_at < text.Length && text[_at] != '\n') _at++;
                else return;
            }
        }

        /// <summary>Reads from an opening quote to its closing quote. False when the text ends first.</summary>
        private bool ReadQuoted(out string word)
        {
            var result = new StringBuilder();
            for (_at++; _at < text.Length; _at++)
            {
                char c = text[_at];
                if (c == '"')
                {
                    _at++;
                    word = result.ToString();
                    return true;
                }
                if (c == '\\' && _at + 1 < text.Length)
                {
                    // Steam writes a path as "C:\\Games" and a quote inside text as \". An
                    // escape it does not use is kept as written, backslash included.
                    char escaped = text[++_at];
                    result.Append(escaped switch
                    {
                        '\\' => "\\",
                        '"' => "\"",
                        'n' => "\n",
                        't' => "\t",
                        'r' => "\r",
                        _ => "\\" + escaped,
                    });
                }
                else result.Append(c);
            }
            word = "";
            return false;
        }
    }
}
