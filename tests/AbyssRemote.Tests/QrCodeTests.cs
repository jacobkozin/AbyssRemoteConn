using System.Text;
using AbyssRemote.Host.App;

namespace AbyssRemote.Tests;

/// <summary>The QR encoder is checked three ways: against values printed in ISO/IEC 18004 and
/// in widely used tutorials (Reed-Solomon codewords, generator polynomials, format and version
/// information), against the structure every symbol must have, and by reading symbols back
/// with a small reader written separately in this file. No scanner app is involved.</summary>
[TestClass]
public sealed class QrCodeTests
{
    private static readonly int[] Versions = Enumerable.Range(QrCode.MinVersion, QrCode.MaxVersion).ToArray();

    // ---- Values published in the standard ----

    [TestMethod]
    public void ReedSolomonMatchesTheStandardsWorkedExample()
    {
        // ISO/IEC 18004, "Symbol encoding example": 01234567 as a version 1-M symbol.
        byte[] data = Convert.FromHexString("10200C566180EC11EC11EC11EC11EC11");
        CollectionAssert.AreEqual(Convert.FromHexString("A524D4C1ED36C7872C55"), QrCode.ErrorCorrection(data, 10));
    }

    [TestMethod]
    public void ReedSolomonMatchesTheHelloWorldExample()
    {
        // The "HELLO WORLD" 1-M example used by most QR tutorials.
        byte[] data = [32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17];
        CollectionAssert.AreEqual(new byte[] { 196, 35, 39, 119, 235, 215, 231, 226, 93, 23 }, QrCode.ErrorCorrection(data, 10));
    }

    [TestMethod]
    [DataRow(7, "87 229 146 149 238 102 21")]
    [DataRow(10, "251 67 46 61 118 70 64 94 32 45")]
    [DataRow(13, "74 152 176 100 86 100 106 104 130 218 206 140 78")]
    [DataRow(16, "120 104 107 109 102 161 76 3 91 191 147 169 182 194 225 120")]
    [DataRow(18, "215 234 158 94 184 97 118 170 79 187 152 148 252 179 5 98 96 153")]
    [DataRow(22, "210 171 247 242 93 230 14 109 221 53 200 74 8 172 98 80 219 134 160 105 165 231")]
    [DataRow(24, "229 121 135 48 211 117 251 126 159 180 169 152 192 226 228 218 111 0 117 232 87 96 227 21")]
    [DataRow(26, "173 125 158 2 103 182 118 17 145 201 111 28 165 53 161 21 245 142 13 102 48 227 153 145 218 70")]
    public void GeneratorPolynomialsMatchTheStandardsTable(int degree, string exponents)
    {
        // ISO/IEC 18004 Annex A lists each generator polynomial as powers of alpha.
        byte[] expected = exponents.Split(' ').Select(e => Gf.Exp[int.Parse(e)]).ToArray();
        CollectionAssert.AreEqual(expected, QrCode.GeneratorPolynomial(degree));
    }

    [TestMethod]
    public void FormatInformationMatchesTheStandardsTable()
    {
        // ISO/IEC 18004 Annex C, all 32 sequences. Level bits: M = 00, L = 01, H = 10, Q = 11.
        string[] m = ["101010000010010", "101000100100101", "101111001111100", "101101101001011", "100010111111001", "100000011001110", "100111110010111", "100101010100000"];
        string[] l = ["111011111000100", "111001011110011", "111110110101010", "111100010011101", "110011000101111", "110001100011000", "110110001000001", "110100101110110"];
        string[] h = ["001011010001001", "001001110111110", "001110011100111", "001100111010000", "000011101100010", "000001001010101", "000110100001100", "000100000111011"];
        string[] q = ["011010101011111", "011000001101000", "011111100110001", "011101000000110", "010010010110100", "010000110000011", "010111011011010", "010101111101101"];
        for (int mask = 0; mask < 8; mask++)
        {
            Assert.AreEqual(m[mask], Bits(QrCode.FormatBits(mask), 15), $"M, mask {mask}");
            Assert.AreEqual(l[mask], Bits(QrCode.FormatBits(mask, 0b01), 15), $"L, mask {mask}");
            Assert.AreEqual(h[mask], Bits(QrCode.FormatBits(mask, 0b10), 15), $"H, mask {mask}");
            Assert.AreEqual(q[mask], Bits(QrCode.FormatBits(mask, 0b11), 15), $"Q, mask {mask}");
        }
    }

    [TestMethod]
    [DataRow(7, 0x07C94)]
    [DataRow(8, 0x085BC)]
    [DataRow(9, 0x09A99)]
    [DataRow(10, 0x0A4D3)]
    public void VersionInformationMatchesTheStandardsTable(int version, int expected) =>
        Assert.AreEqual(expected, QrCode.VersionBits(version)); // ISO/IEC 18004 Annex D

    [TestMethod]
    public void ByteCapacityMatchesTheStandardsTable()
    {
        // ISO/IEC 18004 table 7, level M, "8-bit byte" column.
        int[] published = [14, 26, 42, 62, 84, 106, 122, 152, 180, 213];
        CollectionAssert.AreEqual(published, Versions.Select(QrCode.Capacity).ToArray());
    }

    [TestMethod]
    public void DataModuleCountMatchesTheStandardsTable()
    {
        // ISO/IEC 18004 table 1, "data modules except (C)": what is left after the fixed patterns.
        int[] published = [208, 359, 567, 807, 1079, 1383, 1568, 1936, 2336, 2768];
        foreach (int version in Versions)
        {
            var code = QrCode.Encode(new byte[QrCode.Capacity(version)]);
            Assert.AreEqual(version, code.Version);
            int dataModules = Cells(code).Count(cell => !code.IsFunction(cell.Row, cell.Column));
            Assert.AreEqual(published[version - 1], dataModules, $"version {version}");
        }
    }

    // ---- Encoding steps ----

    [TestMethod]
    public void DataCodewordsCarryModeLengthBytesAndPadding()
    {
        // 0100 (bytes) 00000011 (three of them) 'a' 'b' 'c' 0000 (end), then EC 11 EC 11 ...
        byte[] expected = Convert.FromHexString("40361626" + "30" + "EC11EC11EC11EC11EC11EC");
        CollectionAssert.AreEqual(expected, QrCode.DataCodewords("abc"u8.ToArray(), version: 1));
    }

    [TestMethod]
    public void AFullSymbolNeedsNoPadding()
    {
        byte[] text = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMN"); // 14 bytes: exactly version 1
        byte[] codewords = QrCode.DataCodewords(text, version: 1);
        Assert.AreEqual(16, codewords.Length);
        Assert.AreEqual(0x40, codewords[0]);
        Assert.AreEqual(0xE0, codewords[15], "the last byte's low half and the 4-bit end marker");
    }

    [TestMethod]
    public void FromVersionTenTheLengthTakesSixteenBits()
    {
        byte[] codewords = QrCode.DataCodewords(new byte[200], version: 10);
        Assert.AreEqual(216, codewords.Length);
        CollectionAssert.AreEqual(new byte[] { 0x40, 0x0C, 0x80 }, codewords[..3], "0100, then 200 as sixteen bits");
    }

    [TestMethod]
    public void BlocksAreInterleaved()
    {
        // Version 8-M: two blocks of 38 data codewords, then two of 39, each with 22 for error correction.
        byte[] data = Enumerable.Range(0, 154).Select(i => (byte)i).ToArray();
        byte[] all = QrCode.AddErrorCorrection(data, version: 8);
        Assert.AreEqual(242, all.Length);
        CollectionAssert.AreEqual(new byte[] { 0, 38, 76, 115, 1, 39, 77, 116 }, all[..8], "the first codeword of each block, then the second");
        CollectionAssert.AreEqual(new byte[] { 114, 153 }, all[152..154], "the two longer blocks end last");
        byte[] firstBlockEc = QrCode.ErrorCorrection(data.AsSpan(0, 38), 22);
        Assert.AreEqual(firstBlockEc[0], all[154]);
        Assert.AreEqual(firstBlockEc[1], all[158]);
    }

    [TestMethod]
    public void TheSmallestVersionThatFitsIsUsed()
    {
        Assert.AreEqual(1, QrCode.Encode("https://x.y").Version);
        Assert.AreEqual(3, QrCode.Encode("https://192.168.1.20:8444/#pair=12345678").Version);     // 40 bytes
        Assert.AreEqual(4, QrCode.Encode("https://192.168.100.200:65535/#pair=12345678").Version); // 44 bytes, the longest address
        Assert.AreEqual(33, QrCode.Encode("https://192.168.100.200:65535/#pair=12345678").Size);
        Assert.AreEqual(10, QrCode.Encode(new byte[213]).Version);
        Assert.ThrowsExactly<ArgumentException>(() => QrCode.Encode(new byte[214]));
    }

    // ---- Structure of a finished symbol ----

    [TestMethod]
    public void FinderPatternsSitInThreeCornersWithALightBorder()
    {
        string[] finder = ["#######", "#.....#", "#.###.#", "#.###.#", "#.###.#", "#.....#", "#######"];
        foreach (int version in Versions)
        {
            var code = QrCode.Encode(new byte[QrCode.Capacity(version)]);
            int far = code.Size - 7;
            foreach (var (top, left) in new[] { (0, 0), (0, far), (far, 0) })
            {
                for (int r = 0; r < 7; r++)
                    for (int c = 0; c < 7; c++)
                        Assert.AreEqual(finder[r][c] == '#', code.IsDark(top + r, left + c), $"version {version}, finder at {top},{left}");
                // The light separator: the row and column just outside the 7x7 square.
                int outsideRow = top == 0 ? 7 : top - 1, outsideColumn = left == 0 ? 7 : left - 1;
                for (int i = 0; i < 8; i++)
                {
                    Assert.IsFalse(code.IsDark(outsideRow, left == 0 ? i : code.Size - 1 - i), $"version {version}, separator row");
                    Assert.IsFalse(code.IsDark(top == 0 ? i : code.Size - 1 - i, outsideColumn), $"version {version}, separator column");
                }
            }
            Assert.IsFalse(code.IsFunction(code.Size - 1, code.Size - 1), "the fourth corner holds data");
        }
    }

    [TestMethod]
    public void TimingPatternsAlternateAndTheDarkModuleIsDark()
    {
        foreach (int version in Versions)
        {
            var code = QrCode.Encode(new byte[QrCode.Capacity(version)]);
            for (int i = 8; i < code.Size - 8; i++)
            {
                Assert.AreEqual(i % 2 == 0, code.IsDark(6, i), $"version {version}, row 6, column {i}");
                Assert.AreEqual(i % 2 == 0, code.IsDark(i, 6), $"version {version}, column 6, row {i}");
            }
            Assert.IsTrue(code.IsDark(4 * version + 9, 8), $"version {version}: the module at (4V + 9, 8) is always dark");
        }
    }

    [TestMethod]
    public void AlignmentPatternsSitAtTheStandardsCentres()
    {
        // ISO/IEC 18004 table E.1; the grid points under a finder pattern have none.
        var centres = new Dictionary<int, (int Row, int Column)[]>
        {
            [1] = [],
            [2] = [(18, 18)],
            [6] = [(34, 34)],
            [7] = [(6, 22), (22, 6), (22, 22), (22, 38), (38, 22), (38, 38)],
            [10] = [(6, 28), (28, 6), (28, 28), (28, 50), (50, 28), (50, 50)],
        };
        string[] pattern = ["#####", "#...#", "#.#.#", "#...#", "#####"];
        foreach (var (version, expected) in centres)
        {
            var code = QrCode.Encode(new byte[QrCode.Capacity(version)]);
            foreach (var (row, column) in expected)
                for (int r = -2; r <= 2; r++)
                    for (int c = -2; c <= 2; c++)
                    {
                        Assert.IsTrue(code.IsFunction(row + r, column + c));
                        Assert.AreEqual(pattern[r + 2][c + 2] == '#', code.IsDark(row + r, column + c), $"version {version}, alignment at {row},{column}");
                    }
        }
    }

    [TestMethod]
    public void BothCopiesOfTheFormatInformationNameLevelMAndTheMask()
    {
        foreach (int version in Versions)
        {
            var code = QrCode.Encode(new byte[QrCode.Capacity(version)]);
            int expected = QrCode.FormatBits(code.Mask);
            Assert.AreEqual(expected, Reader.FormatNearTopLeft(code), $"version {version}, first copy");
            Assert.AreEqual(expected, Reader.FormatSplitCopy(code), $"version {version}, second copy");
            Assert.AreEqual(code.Mask, ((expected ^ 0x5412) >> 10) & 0b111);
            Assert.AreEqual(0b00, (expected ^ 0x5412) >> 13, "level M");
        }
    }

    [TestMethod]
    public void VersionInformationIsDrawnTwiceFromVersionSeven()
    {
        foreach (int version in new[] { 7, 8, 9, 10 })
        {
            var code = QrCode.Encode(new byte[QrCode.Capacity(version)]);
            int bottomLeft = 0, topRight = 0;
            for (int i = 0; i < 18; i++)
            {
                // Bit i sits at row size-11+(i mod 3), column i/3 in the bottom-left block,
                // and mirrored in the top-right block.
                if (code.IsDark(code.Size - 11 + i % 3, i / 3)) bottomLeft |= 1 << i;
                if (code.IsDark(i / 3, code.Size - 11 + i % 3)) topRight |= 1 << i;
            }
            Assert.AreEqual(QrCode.VersionBits(version), bottomLeft, $"version {version}");
            Assert.AreEqual(QrCode.VersionBits(version), topRight, $"version {version}");
        }
        var six = QrCode.Encode(new byte[QrCode.Capacity(6)]);
        Assert.IsFalse(six.IsFunction(0, six.Size - 11), "version 6 has no version information; that corner of the block holds data");
    }

    // ---- Masking ----

    [TestMethod]
    public void MaskConditionsFollowTheStandard()
    {
        // ISO/IEC 18004 table 10, written out for the top-left 6x6 modules (# = flipped).
        string[][] published =
        [
            ["#.#.#.", ".#.#.#", "#.#.#.", ".#.#.#", "#.#.#.", ".#.#.#"],
            ["######", "......", "######", "......", "######", "......"],
            ["#..#..", "#..#..", "#..#..", "#..#..", "#..#..", "#..#.."],
            ["#..#..", "..#..#", ".#..#.", "#..#..", "..#..#", ".#..#."],
            ["###...", "###...", "...###", "...###", "###...", "###..."],
            ["######", "#.....", "#..#..", "#.#.#.", "#..#..", "#....."],
            ["######", "###...", "##.##.", "#.#.#.", "#.##.#", "#...##"],
            ["#.#.#.", "...###", "#...##", ".#.#.#", "###...", ".###.."],
        ];
        for (int mask = 0; mask < 8; mask++)
            for (int row = 0; row < 6; row++)
                for (int column = 0; column < 6; column++)
                    Assert.AreEqual(published[mask][row][column] == '#', QrCode.MaskFlips(mask, row, column), $"mask {mask} at {row},{column}");
    }

    [TestMethod]
    public void PenaltyRulesScoreKnownShapes()
    {
        // A 12x12 checkerboard has no runs, no blocks, no finder look-alikes and is half dark.
        var grid = Grid(12, (row, column) => (row + column) % 2 == 0);
        Assert.AreEqual(0, QrCode.Penalty(grid));

        // Rule 1: a run of five costs 3, and each extra module 1 more. Painting row 0 dark from
        // column 0 to 6 makes one run of seven (3 + 2 = 5) and nothing else in a checkerboard.
        for (int column = 0; column < 7; column++) grid[0, column] = true;
        Assert.AreEqual(5, QrCode.Penalty(grid));

        // Rule 2: one 2x2 block costs 3. (Rows 5 and 6, columns 5 and 6 of a checkerboard become
        // one colour by flipping two modules, which also creates no run of five.)
        grid = Grid(12, (row, column) => (row + column) % 2 == 0);
        grid[5, 6] = grid[5, 5];
        grid[6, 5] = grid[5, 5];
        Assert.AreEqual(3, QrCode.Penalty(grid));

        // Rule 4: all light is 50% away from half dark: ten steps of 5% = 100. On top of that
        // come rule 1 (24 lines with a run of twelve: 24 x 10) and rule 2 (121 blocks x 3).
        Assert.AreEqual(100 + 240 + 363, QrCode.Penalty(new bool[12, 12]));
    }

    [TestMethod]
    public void AFinderLookAlikeCostsForty()
    {
        // One row holding dark-light-dark-dark-dark-light-dark followed by four light modules;
        // every other row is a pattern that scores nothing by itself.
        const string row = "#.###.#....#";
        var plain = Grid(12, (r, c) => (r + c) % 2 == 0);
        var withFinder = Grid(12, (r, c) => r == 4 ? row[c] == '#' : (r + c) % 2 == 0);
        var withoutFinder = Grid(12, (r, c) => r == 4 ? "#.###.#.#..#"[c] == '#' : (r + c) % 2 == 0); // the same but only three light modules follow
        Assert.AreEqual(0, QrCode.Penalty(plain));
        Assert.AreEqual(40, QrCode.Penalty(withFinder) - QrCode.Penalty(withoutFinder),
            "the two grids differ in one module that completes the four light modules");
    }

    [TestMethod]
    public void TheMaskWithTheLowestPenaltyIsChosen()
    {
        foreach (string text in new[] { "https://192.168.1.20:8444/#pair=12345678", "HELLO", new string('a', 150) })
        {
            byte[] data = Encoding.ASCII.GetBytes(text);
            var chosen = QrCode.Encode(data);
            byte[] codewords = QrCode.AddErrorCorrection(QrCode.DataCodewords(data, chosen.Version), chosen.Version);
            int[] penalties = Enumerable.Range(0, 8).Select(mask => QrCode.Penalty(Matrix(QrCode.Build(chosen.Version, codewords, mask)))).ToArray();
            Assert.AreEqual(Array.IndexOf(penalties, penalties.Min()), chosen.Mask, text);
            Assert.AreEqual(penalties.Min(), QrCode.Penalty(Matrix(chosen)));
        }
    }

    // ---- Reading symbols back ----

    [TestMethod]
    [DataRow("https://192.168.1.20:8444/#pair=12345678")]
    [DataRow("https://10.0.0.5:8444/")]
    [DataRow("https://192.168.100.200:65535/#pair=00000000")]
    [DataRow("a")]
    [DataRow("")]
    public void ASymbolReadsBackAsItsText(string text)
    {
        var code = QrCode.Encode(text);
        Assert.AreEqual(text, Encoding.UTF8.GetString(Reader.Read(code)));
    }

    [TestMethod]
    public void EveryVersionAndEveryMaskReadsBack()
    {
        var random = new Random(18004);
        foreach (int version in Versions)
        {
            byte[] data = new byte[QrCode.Capacity(version)];
            random.NextBytes(data);
            byte[] codewords = QrCode.AddErrorCorrection(QrCode.DataCodewords(data, version), version);
            for (int mask = 0; mask < 8; mask++)
            {
                var code = QrCode.Build(version, codewords, mask);
                Assert.AreEqual(mask, code.Mask);
                CollectionAssert.AreEqual(data, Reader.Read(code), $"version {version}, mask {mask}");
            }
        }
    }

    [TestMethod]
    public void NonAsciiTextIsStoredAsUtf8()
    {
        const string text = "Jürgen’s PC ✓";
        Assert.AreEqual(text, Encoding.UTF8.GetString(Reader.Read(QrCode.Encode(text))));
    }

    // ---- Rendering ----

    [TestMethod]
    public void ThePictureHasAQuietZoneAndWholePixelModules()
    {
        var code = QrCode.Encode("https://192.168.1.20:8444/#pair=12345678");
        Color dark = Color.FromArgb(10, 14, 23), light = Color.White;
        using var bitmap = QrRenderer.ToBitmap(code, pixelsPerModule: 3, dark, light);

        Assert.AreEqual((code.Size + 8) * 3, bitmap.Width);
        Assert.AreEqual(bitmap.Width, bitmap.Height);
        for (int row = 0; row < code.Size + 8; row++)
            for (int column = 0; column < code.Size + 8; column++)
            {
                bool inside = row >= 4 && row < code.Size + 4 && column >= 4 && column < code.Size + 4;
                bool expectDark = inside && code.IsDark(row - 4, column - 4);
                // Every pixel of the module has the module's colour: no smoothing, no gaps.
                foreach (var (dx, dy) in new[] { (0, 0), (2, 0), (0, 2), (2, 2), (1, 1) })
                    Assert.AreEqual((expectDark ? dark : light).ToArgb(), bitmap.GetPixel(column * 3 + dx, row * 3 + dy).ToArgb(), $"module {row},{column}");
            }
    }

    [TestMethod]
    public void TheModuleSizeIsTheLargestThatFits()
    {
        var code = QrCode.Encode("https://192.168.1.20:8444/#pair=12345678"); // version 3: 29 + 8 = 37 modules
        Assert.AreEqual(37, QrRenderer.ModulesPerSide(code));
        Assert.AreEqual(4, QrRenderer.PixelsPerModule(code, 176));
        Assert.AreEqual(7, QrRenderer.PixelsPerModule(code, 264));
        Assert.AreEqual(1, QrRenderer.PixelsPerModule(code, 10), "never zero");
    }

    // ---- Helpers ----

    private static string Bits(int value, int count) => Convert.ToString(value, 2).PadLeft(count, '0');

    private static IEnumerable<(int Row, int Column)> Cells(QrCode code) =>
        from row in Enumerable.Range(0, code.Size) from column in Enumerable.Range(0, code.Size) select (row, column);

    private static bool[,] Matrix(QrCode code)
    {
        var grid = new bool[code.Size, code.Size];
        foreach (var (row, column) in Cells(code)) grid[row, column] = code.IsDark(row, column);
        return grid;
    }

    private static bool[,] Grid(int size, Func<int, int, bool> dark)
    {
        var grid = new bool[size, size];
        for (int row = 0; row < size; row++)
            for (int column = 0; column < size; column++) grid[row, column] = dark(row, column);
        return grid;
    }

    /// <summary>GF(256) built from scratch for the tests: powers of alpha = 2 modulo x^8+x^4+x^3+x^2+1.</summary>
    private static class Gf
    {
        public static readonly byte[] Exp = new byte[255]; // Exp[n] = alpha^n
        private static readonly int[] Log = new int[256];  // the other way round

        static Gf()
        {
            int value = 1;
            for (int n = 0; n < 255; n++)
            {
                Exp[n] = (byte)value;
                Log[value] = n;
                value <<= 1;
                if (value >= 256) value ^= 0x11D;
            }
        }

        public static byte Multiply(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[(Log[a] + Log[b]) % 255];
    }

    /// <summary>A minimal QR reader for exact symbols (no camera, no damage). It shares no code
    /// with the encoder: it finds the mask in the format information, walks the data modules
    /// in the standard's zigzag, undoes the interleaving, checks every block's Reed-Solomon
    /// syndromes and then parses the byte-mode data.</summary>
    private static class Reader
    {
        // Level M: (error correction codewords per block, blocks, data codewords per block,
        // blocks that are one longer). Typed in again from ISO/IEC 18004 table 9.
        private static readonly (int Ec, int Blocks, int Data, int Longer)[] Table =
        [
            (10, 1, 16, 0), (16, 1, 28, 0), (26, 1, 44, 0), (18, 2, 32, 0), (24, 2, 43, 0),
            (16, 4, 27, 0), (18, 4, 31, 0), (22, 2, 38, 2), (22, 3, 36, 2), (26, 4, 43, 1),
        ];

        /// <summary>The copy around the top-left finder: bits 0-7 run down column 8 (skipping
        /// the timing row), bits 8-14 run left along row 8 (skipping the timing column).</summary>
        public static int FormatNearTopLeft(QrCode code)
        {
            (int Row, int Column)[] cells =
            [
                (0, 8), (1, 8), (2, 8), (3, 8), (4, 8), (5, 8), (7, 8), (8, 8),
                (8, 7), (8, 5), (8, 4), (8, 3), (8, 2), (8, 1), (8, 0),
            ];
            return Pack(code, cells);
        }

        /// <summary>The second copy: bits 0-7 run left from the top-right corner along row 8,
        /// bits 8-14 run down column 8 to the bottom-left corner.</summary>
        public static int FormatSplitCopy(QrCode code)
        {
            int n = code.Size;
            (int Row, int Column)[] cells =
            [
                (8, n - 1), (8, n - 2), (8, n - 3), (8, n - 4), (8, n - 5), (8, n - 6), (8, n - 7), (8, n - 8),
                (n - 7, 8), (n - 6, 8), (n - 5, 8), (n - 4, 8), (n - 3, 8), (n - 2, 8), (n - 1, 8),
            ];
            return Pack(code, cells);
        }

        private static int Pack(QrCode code, (int Row, int Column)[] cells)
        {
            int value = 0;
            for (int bit = 0; bit < cells.Length; bit++)
                if (code.IsDark(cells[bit].Row, cells[bit].Column)) value |= 1 << bit;
            return value;
        }

        public static byte[] Read(QrCode code)
        {
            int size = code.Size, version = (size - 17) / 4;
            int mask = ((FormatNearTopLeft(code) ^ 0x5412) >> 10) & 0b111;

            // Walk the column pairs from the right edge; the direction turns around after each pair.
            var bits = new List<bool>();
            bool up = true;
            for (int right = size - 1; right > 0; right -= 2)
            {
                if (right == 6) right--; // the vertical timing pattern is not part of a pair
                for (int n = 0; n < size; n++)
                {
                    int row = up ? size - 1 - n : n;
                    foreach (int column in new[] { right, right - 1 })
                    {
                        if (code.IsFunction(row, column)) continue;
                        bits.Add(code.IsDark(row, column) ^ Flipped(mask, row, column));
                    }
                }
                up = !up;
            }

            var (ec, blocks, dataLength, longer) = Table[version - 1];
            int blockCount = blocks + longer, total = blocks * dataLength + longer * (dataLength + 1) + blockCount * ec;
            Assert.IsTrue(bits.Count - total * 8 is >= 0 and < 8, "only the remainder bits may be left over");
            Assert.IsTrue(bits.Skip(total * 8).All(bit => !bit), "the remainder bits are light before masking");
            var stream = new byte[total];
            for (int i = 0; i < total * 8; i++)
                if (bits[i]) stream[i / 8] |= (byte)(0x80 >> (i % 8));

            // Undo the interleaving: codeword k of every block comes before codeword k + 1 of any.
            var dataBlocks = Enumerable.Range(0, blockCount).Select(b => new List<byte>()).ToArray();
            var ecBlocks = Enumerable.Range(0, blockCount).Select(b => new List<byte>()).ToArray();
            int position = 0;
            for (int k = 0; k < dataLength + 1; k++)
                for (int b = 0; b < blockCount; b++)
                    if (k < dataLength + (b >= blocks ? 1 : 0)) dataBlocks[b].Add(stream[position++]);
            for (int k = 0; k < ec; k++)
                for (int b = 0; b < blockCount; b++) ecBlocks[b].Add(stream[position++]);
            Assert.AreEqual(total, position);

            // A block without errors evaluates to zero at alpha^0 ... alpha^(ec - 1).
            for (int b = 0; b < blockCount; b++)
            {
                byte[] block = [.. dataBlocks[b], .. ecBlocks[b]];
                for (int root = 0; root < ec; root++)
                {
                    byte sum = 0;
                    foreach (byte codeword in block) sum = (byte)(Gf.Multiply(sum, Gf.Exp[root]) ^ codeword);
                    Assert.AreEqual(0, sum, $"block {b}, syndrome {root}");
                }
            }

            // Byte mode: 0100, the length (8 bits, or 16 from version 10), the bytes, 0000.
            byte[] data = dataBlocks.SelectMany(block => block).ToArray();
            int cursor = 0;
            int Take(int count)
            {
                int value = 0;
                for (int i = 0; i < count; i++, cursor++) value = (value << 1) | ((data[cursor / 8] >> (7 - cursor % 8)) & 1);
                return value;
            }
            Assert.AreEqual(0b0100, Take(4), "mode indicator");
            int length = Take(version < 10 ? 8 : 16);
            byte[] text = Enumerable.Range(0, length).Select(_ => (byte)Take(8)).ToArray();
            int terminator = Math.Min(4, data.Length * 8 - cursor);
            Assert.AreEqual(0, Take(terminator), "terminator");
            while (cursor % 8 != 0) Assert.AreEqual(0, Take(1), "bits up to the next whole byte");
            for (int pad = 0xEC; cursor < data.Length * 8; pad ^= 0xEC ^ 0x11) Assert.AreEqual(pad, Take(8), "pad byte");
            return text;
        }

        // Table 10 again, as the standard writes it (i = row, j = column).
        private static bool Flipped(int mask, int i, int j) => mask switch
        {
            0 => (i + j) % 2 == 0,
            1 => i % 2 == 0,
            2 => j % 3 == 0,
            3 => (i + j) % 3 == 0,
            4 => (i / 2 + j / 3) % 2 == 0,
            5 => (i * j) % 2 + (i * j) % 3 == 0,
            6 => ((i * j) % 2 + (i * j) % 3) % 2 == 0,
            _ => ((i * j) % 3 + (i + j) % 2) % 2 == 0,
        };
    }
}
