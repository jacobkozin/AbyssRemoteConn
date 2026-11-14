using System.Text;
using AbyssRemote.Host.Features;

namespace AbyssRemote.Tests;

/// <summary>The reader for Steam's KeyValues text files. Every text here is made up for the test.</summary>
[TestClass]
public sealed class KeyValuesTests
{
    private const string Sample =
        "\"AppState\"\n{\n" +
        "\t\"appid\"\t\t\"620\"\n" +
        "\t\"name\"\t\t\"Portal 2\"\n" +
        "\t\"UserConfig\"\n\t{\n\t\t\"language\"\t\t\"english\"\n\t\t\"Depots\"\n\t\t{\n\t\t\t\"621\"\t\t\"1\"\n\t\t}\n\t}\n" +
        "\t\"empty\"\t\t\"\"\n" +
        "}\n";

    [TestMethod]
    public void ReadsTextAndNestedGroups()
    {
        var state = KeyValues.Parse(Sample)!.Child("AppState")!;

        Assert.IsNull(state.Value, "a group has no text of its own");
        Assert.AreEqual("620", state.Text("appid"));
        Assert.AreEqual("Portal 2", state.Text("name"));
        Assert.AreEqual("", state.Text("empty"));
        Assert.AreEqual("english", state.Child("UserConfig")!.Text("language"));
        Assert.AreEqual("1", state.Child("UserConfig")!.Child("Depots")!.Text("621"));
        CollectionAssert.AreEqual(new[] { "appid", "name", "UserConfig", "empty" }, state.Children.Select(entry => entry.Key).ToArray());
    }

    [TestMethod]
    public void NamesIgnoreCaseAndMissingOnesAreNull()
    {
        var state = KeyValues.Parse(Sample)!.Child("appstate")!;

        Assert.AreEqual("620", state.Text("APPID"));
        Assert.IsNull(state.Text("missing"));
        Assert.IsNull(state.Child("missing"));
        Assert.IsNull(state.Text("UserConfig"), "a group is not text");
        Assert.AreEqual(0, state.Child("appid")!.Children.Count, "text has no entries of its own");
    }

    [TestMethod]
    public void TheFirstOfTwoEntriesWithOneNameWins()
    {
        var root = KeyValues.Parse("\"a\" \"1\"\n\"a\" \"2\"")!;

        Assert.AreEqual("1", root.Text("a"));
        Assert.AreEqual(2, root.Children.Count);
    }

    [TestMethod]
    public void EscapesInQuotedTextAreUndone()
    {
        var root = KeyValues.Parse("""
            "path"    "C:\\Program Files (x86)\\Steam"
            "quote"   "say \"hi\" twice"
            "lines"   "one\ntwo\tthree\rfour"
            "other"   "kept \q as written"
            """)!;

        Assert.AreEqual(@"C:\Program Files (x86)\Steam", root.Text("path"));
        Assert.AreEqual("say \"hi\" twice", root.Text("quote"));
        Assert.AreEqual("one\ntwo\tthree\rfour", root.Text("lines"));
        Assert.AreEqual(@"kept \q as written", root.Text("other"));
    }

    [TestMethod]
    public void CommentsAndBareWordsAreUnderstood()
    {
        var state = KeyValues.Parse("""
            // a comment line
            AppState // a bare name, then a comment
            {
                appid 620            // bare text
                "url" "https://example.invalid/a//b"   // two slashes inside quotes are text
                "name"{"inner""value"}// no spaces at all
            }
            // the end
            """)!.Child("AppState")!;

        Assert.AreEqual("620", state.Text("appid"));
        Assert.AreEqual("https://example.invalid/a//b", state.Text("url"));
        Assert.AreEqual("value", state.Child("name")!.Text("inner"));
        Assert.AreEqual(3, state.Children.Count);
    }

    [TestMethod]
    public void AnEmptyFileIsAnEmptyGroup()
    {
        Assert.AreEqual(0, KeyValues.Parse("")!.Children.Count);
        Assert.AreEqual(0, KeyValues.Parse("  \n\t// only a comment")!.Children.Count);
    }

    [TestMethod]
    [DataRow("\"name\"", DisplayName = "a name with nothing after it")]
    [DataRow("\"name\" \"text", DisplayName = "text whose closing quote is missing")]
    [DataRow("\"name\" {", DisplayName = "a group that is never closed")]
    [DataRow("\"name\" { \"a\" \"1\"", DisplayName = "a group cut off after an entry")]
    [DataRow("\"name\" { \"a\" }", DisplayName = "a name followed by a closing brace")]
    [DataRow("}", DisplayName = "a closing brace with nothing open")]
    [DataRow("\"a\" \"1\" }", DisplayName = "one closing brace too many")]
    [DataRow("{ \"a\" \"1\" }", DisplayName = "a group without a name")]
    [DataRow("\"a\" { { } }", DisplayName = "a group where a name belongs")]
    public void TextThatBreaksTheFormatIsRefused(string text) => Assert.IsNull(KeyValues.Parse(text));

    [TestMethod]
    public void AFileCutOffAnywhereIsRefused()
    {
        // Every way of cutting the sample short, as a crash in the middle of writing it would.
        int lastBrace = Sample.LastIndexOf('}');
        for (int length = 1; length <= lastBrace; length++)
            Assert.IsNull(KeyValues.Parse(Sample[..length]), $"cut after {length} characters");

        Assert.IsNotNull(KeyValues.Parse(Sample[..(lastBrace + 1)]));
    }

    [TestMethod]
    public void GarbageNeverThrows()
    {
        // Mostly the characters that mean something to the reader, so every path is taken.
        const string alphabet = "\"\\{}/ \n\tab\0";
        var random = new Random(12345);
        for (int round = 0; round < 2000; round++)
        {
            var text = new StringBuilder();
            for (int length = random.Next(60); length > 0; length--) text.Append(alphabet[random.Next(alphabet.Length)]);
            KeyValues.Parse(text.ToString()); // has to come back, with or without a result
        }

        var bytes = new byte[8192];
        random.NextBytes(bytes);
        KeyValues.Parse(Encoding.Latin1.GetString(bytes));
    }

    [TestMethod]
    public void VeryDeepNestingIsRefusedInsteadOfEndingTheProgram()
    {
        static string Nested(int levels) => string.Concat(Enumerable.Repeat("\"a\"{", levels)) + new string('}', levels);

        Assert.IsNotNull(KeyValues.Parse(Nested(KeyValues.MaxDepth)));
        Assert.IsNull(KeyValues.Parse(Nested(KeyValues.MaxDepth + 1)));
        Assert.IsNull(KeyValues.Parse(Nested(200_000)), "this many levels would otherwise overflow the stack");
    }
}
