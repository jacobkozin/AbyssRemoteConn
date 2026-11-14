using System.Text;

namespace AbyssRemote.Host.App;

/// <summary>A QR code: a square grid of dark and light modules that a phone camera reads.
/// Written by hand because the project uses no packages. It follows ISO/IEC 18004 for what
/// the window needs: bytes as data, error correction level M (about 15% of the symbol may be
/// unreadable), and versions 1 to 10, which hold up to 213 bytes. An address with a pairing
/// code is about 45 characters and fits version 4.
///
/// The steps, in the order the code below takes them:
/// 1. turn the text into data codewords (mode, length, bytes, padding),
/// 2. add Reed-Solomon error correction codewords and interleave the blocks,
/// 3. draw the fixed patterns a scanner looks for,
/// 4. fill the remaining modules with the codeword bits in a zigzag,
/// 5. try the eight masks and keep the one a scanner reads most easily.</summary>
public sealed class QrCode
{
    public const int MinVersion = 1, MaxVersion = 10;

    // Error correction level M, one row per version, from ISO/IEC 18004 table 9: error
    // correction codewords per block, then how many blocks of how many data codewords.
    // Larger versions split the data into blocks, some of them one codeword longer.
    private static readonly (int EcPerBlock, int Blocks, int DataPerBlock, int LongerBlocks)[] BlockTable =
    [
        (10, 1, 16, 0), // version 1
        (16, 1, 28, 0),
        (26, 1, 44, 0),
        (18, 2, 32, 0),
        (24, 2, 43, 0),
        (16, 4, 27, 0),
        (18, 4, 31, 0),
        (22, 2, 38, 2), // 2 blocks of 38 and 2 of 39
        (22, 3, 36, 2),
        (26, 4, 43, 1), // version 10
    ];

    // Row and column positions of the alignment pattern centres (ISO/IEC 18004 table E.1).
    private static readonly int[][] AlignmentCentres =
    [
        [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34], [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50],
    ];

    private readonly bool[,] _dark;     // [row, column], true = dark
    private readonly bool[,] _function; // true = part of a fixed pattern, not data

    private QrCode(int version, int mask, bool[,] dark, bool[,] function)
    {
        Version = version;
        Mask = mask;
        _dark = dark;
        _function = function;
    }

    /// <summary>1 to 10; each version adds four modules per side.</summary>
    public int Version { get; }

    /// <summary>Modules per side: 21 for version 1, 57 for version 10.</summary>
    public int Size => SizeOf(Version);

    /// <summary>Which of the eight masks was applied (0 to 7).</summary>
    public int Mask { get; }

    public bool IsDark(int row, int column) => _dark[row, column];

    /// <summary>True for modules that belong to a finder, timing or alignment pattern or to
    /// the format and version information, false for data modules.</summary>
    public bool IsFunction(int row, int column) => _function[row, column];

    public static int SizeOf(int version) => 17 + 4 * version;

    /// <summary>How many bytes of text fit in one version.</summary>
    public static int Capacity(int version) => DataCodewordCount(version) - (version < 10 ? 2 : 3); // mode + length take 12 or 20 bits

    public static int DataCodewordCount(int version)
    {
        var (_, blocks, dataPerBlock, longer) = BlockTable[version - 1];
        return blocks * dataPerBlock + longer * (dataPerBlock + 1);
    }

    public static QrCode Encode(string text) => Encode(Encoding.UTF8.GetBytes(text));

    public static QrCode Encode(byte[] data)
    {
        int version = MinVersion;
        while (Capacity(version) < data.Length)
            if (++version > MaxVersion) throw new ArgumentException($"A QR code here holds at most {Capacity(MaxVersion)} bytes.", nameof(data));
        return Build(version, AddErrorCorrection(DataCodewords(data, version), version));
    }

    /// <summary>Steps 3 to 5 for codewords that are already complete.
    /// <paramref name="forcedMask"/> lets a test ask for one particular mask.</summary>
    internal static QrCode Build(int version, byte[] codewords, int? forcedMask = null)
    {
        int size = SizeOf(version);
        var dark = new bool[size, size];
        var function = new bool[size, size];
        DrawFunctionPatterns(version, dark, function);
        PlaceCodewords(codewords, dark, function);

        int best = forcedMask ?? 0, lowest = int.MaxValue;
        for (int mask = 0; mask < 8 && forcedMask is null; mask++)
        {
            ApplyMask(mask, dark, function);
            DrawFormatBits(mask, dark, function);
            int penalty = Penalty(dark);
            if (penalty < lowest) (best, lowest) = (mask, penalty);
            ApplyMask(mask, dark, function); // applying the same mask again undoes it
        }
        ApplyMask(best, dark, function);
        DrawFormatBits(best, dark, function);
        return new QrCode(version, best, dark, function);
    }

    // ---- Step 1: data codewords ----

    /// <summary>The data part of the symbol: "these are bytes", how many, the bytes, an
    /// end marker, then filler up to the exact size the version holds.</summary>
    internal static byte[] DataCodewords(byte[] data, int version)
    {
        int total = DataCodewordCount(version);
        var bits = new List<bool>(total * 8);
        void Append(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }

        Append(0b0100, 4);                                // mode indicator: byte mode
        Append(data.Length, version < 10 ? 8 : 16);       // character count
        foreach (byte b in data) Append(b, 8);
        Append(0, Math.Min(4, total * 8 - bits.Count));   // terminator, shortened if the data fills the symbol
        Append(0, (8 - bits.Count % 8) % 8);              // up to a whole byte
        for (int pad = 0xEC; bits.Count < total * 8; pad ^= 0xEC ^ 0x11) Append(pad, 8); // pad bytes EC, 11, EC, ...

        var codewords = new byte[total];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i]) codewords[i / 8] |= (byte)(0x80 >> (i % 8));
        return codewords;
    }

    // ---- Step 2: error correction ----

    /// <summary>Splits the data into blocks, computes each block's error correction codewords,
    /// and interleaves them (first codeword of every block, then the second of every block...),
    /// so a scratch that wipes out neighbouring modules is spread over several blocks.</summary>
    internal static byte[] AddErrorCorrection(byte[] data, int version)
    {
        var (ecPerBlock, blocks, dataPerBlock, longer) = BlockTable[version - 1];
        var dataBlocks = new List<byte[]>();
        var ecBlocks = new List<byte[]>();
        int offset = 0;
        for (int i = 0; i < blocks + longer; i++)
        {
            int length = i < blocks ? dataPerBlock : dataPerBlock + 1; // the longer blocks come last
            byte[] block = data.AsSpan(offset, length).ToArray();
            offset += length;
            dataBlocks.Add(block);
            ecBlocks.Add(ErrorCorrection(block, ecPerBlock));
        }

        var all = new List<byte>(data.Length + ecPerBlock * dataBlocks.Count);
        for (int i = 0; i <= dataPerBlock; i++)
            foreach (byte[] block in dataBlocks)
                if (i < block.Length) all.Add(block[i]);
        for (int i = 0; i < ecPerBlock; i++)
            foreach (byte[] block in ecBlocks) all.Add(block[i]);
        return all.ToArray();
    }

    /// <summary>Reed-Solomon error correction codewords for one block: the remainder of dividing
    /// the data (as a polynomial, times x^count) by the generator polynomial. All arithmetic is
    /// in the 256-element field GF(256), where adding is XOR.</summary>
    internal static byte[] ErrorCorrection(ReadOnlySpan<byte> data, int count)
    {
        byte[] generator = GeneratorPolynomial(count);
        var remainder = new byte[count];
        foreach (byte value in data)
        {
            // Long division, one data codeword per step.
            byte factor = (byte)(value ^ remainder[0]);
            Array.Copy(remainder, 1, remainder, 0, count - 1);
            remainder[count - 1] = 0;
            for (int i = 0; i < count; i++) remainder[i] ^= Multiply(generator[i], factor);
        }
        return remainder;
    }

    /// <summary>(x - a^0)(x - a^1)...(x - a^(degree-1)), where a = 2 generates the field.
    /// Returned highest power first, without the leading coefficient (always 1).</summary>
    internal static byte[] GeneratorPolynomial(int degree)
    {
        var coefficients = new byte[degree];
        coefficients[degree - 1] = 1; // the polynomial "1"
        byte root = 1;                // a^0
        for (int i = 0; i < degree; i++)
        {
            // Multiply what we have by (x - root).
            for (int j = 0; j < degree; j++)
            {
                coefficients[j] = Multiply(coefficients[j], root);
                if (j + 1 < degree) coefficients[j] ^= coefficients[j + 1];
            }
            root = Multiply(root, 2);
        }
        return coefficients;
    }

    /// <summary>Multiplication in GF(256) as QR codes define it: polynomials over bits,
    /// reduced by x^8 + x^4 + x^3 + x^2 + 1 (0x11D).</summary>
    internal static byte Multiply(byte a, byte b)
    {
        int product = 0;
        for (int bit = 7; bit >= 0; bit--)
        {
            product = (product << 1) ^ ((product >> 7) * 0x11D);
            if (((b >> bit) & 1) != 0) product ^= a;
        }
        return (byte)product;
    }

    // ---- Step 3: the fixed patterns ----

    private static void DrawFunctionPatterns(int version, bool[,] dark, bool[,] function)
    {
        int size = SizeOf(version);
        void Set(int row, int column, bool isDark)
        {
            dark[row, column] = isDark;
            function[row, column] = true;
        }

        // Timing patterns: alternating modules in row 6 and column 6 give the scanner a ruler.
        for (int i = 0; i < size; i++)
        {
            Set(6, i, i % 2 == 0);
            Set(i, 6, i % 2 == 0);
        }

        // Finder patterns: the three big squares in the corners, each with a light border.
        foreach (var (centreRow, centreColumn) in new[] { (3, 3), (3, size - 4), (size - 4, 3) })
            for (int dr = -4; dr <= 4; dr++)
                for (int dc = -4; dc <= 4; dc++)
                {
                    int row = centreRow + dr, column = centreColumn + dc;
                    if (row < 0 || row >= size || column < 0 || column >= size) continue;
                    int ring = Math.Max(Math.Abs(dr), Math.Abs(dc)); // 0 = centre ... 4 = the light border
                    Set(row, column, ring != 2 && ring != 4);
                }

        // Alignment patterns: small squares that help with a tilted or curved symbol. They sit
        // on a grid; the three grid points that fall on a finder pattern are left out.
        int[] centres = AlignmentCentres[version - 1];
        for (int i = 0; i < centres.Length; i++)
            for (int j = 0; j < centres.Length; j++)
            {
                bool onFinder = (i == 0 && j == 0) || (i == 0 && j == centres.Length - 1) || (i == centres.Length - 1 && j == 0);
                if (onFinder) continue;
                for (int dr = -2; dr <= 2; dr++)
                    for (int dc = -2; dc <= 2; dc++)
                        Set(centres[i] + dr, centres[j] + dc, Math.Max(Math.Abs(dr), Math.Abs(dc)) != 1);
            }

        DrawFormatBits(0, dark, function); // reserves the modules; the real mask is written later
        Set(size - 8, 8, true);            // one module that is always dark

        // Version information (version 7 and up): 18 bits, twice, next to two finder patterns.
        if (version >= 7)
        {
            int bits = VersionBits(version);
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((bits >> i) & 1) != 0;
                int a = size - 11 + i % 3, b = i / 3;
                Set(b, a, bit);
                Set(a, b, bit);
            }
        }
    }

    /// <summary>Writes the 15 format bits (error correction level and mask) in both places:
    /// around the top-left finder pattern, and split between the other two.</summary>
    private static void DrawFormatBits(int mask, bool[,] dark, bool[,] function)
    {
        int size = dark.GetLength(0), bits = FormatBits(mask);
        void Set(int row, int column, int bit)
        {
            dark[row, column] = ((bits >> bit) & 1) != 0;
            function[row, column] = true;
        }

        for (int i = 0; i <= 5; i++) Set(i, 8, i);
        Set(7, 8, 6);
        Set(8, 8, 7);
        Set(8, 7, 8);
        for (int i = 9; i < 15; i++) Set(8, 14 - i, i);

        for (int i = 0; i < 8; i++) Set(8, size - 1 - i, i);
        for (int i = 8; i < 15; i++) Set(size - 15 + i, 8, i);
    }

    /// <summary>Level M (binary 00) and the mask number, protected by a BCH(15,5) code and
    /// XORed with a fixed pattern so the result is never all zeros.</summary>
    internal static int FormatBits(int mask, int levelBits = 0b00)
    {
        int data = (levelBits << 3) | mask;
        int remainder = data;
        for (int i = 0; i < 10; i++) remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
        return ((data << 10) | remainder) ^ 0x5412;
    }

    /// <summary>The version number protected by a BCH(18,6) code.</summary>
    internal static int VersionBits(int version)
    {
        int remainder = version;
        for (int i = 0; i < 12; i++) remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
        return (version << 12) | remainder;
    }

    // ---- Step 4: the data, in a zigzag ----

    /// <summary>Fills the data modules two columns at a time, starting at the bottom right and
    /// snaking up and down towards the left. Column 6 holds a timing pattern and is skipped.
    /// Modules left over at the end (the "remainder bits") stay light.</summary>
    private static void PlaceCodewords(byte[] codewords, bool[,] dark, bool[,] function)
    {
        int size = dark.GetLength(0), bit = 0;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            bool upward = ((right + 1) & 2) == 0;
            for (int step = 0; step < size; step++)
            {
                int row = upward ? size - 1 - step : step;
                for (int j = 0; j < 2; j++)
                {
                    int column = right - j;
                    if (function[row, column] || bit >= codewords.Length * 8) continue;
                    dark[row, column] = ((codewords[bit / 8] >> (7 - bit % 8)) & 1) != 0;
                    bit++;
                }
            }
        }
    }

    // ---- Step 5: masking ----

    /// <summary>Flips the data modules a mask selects. Masks break up large single-colour
    /// areas and shapes that look like finder patterns, which confuse scanners.</summary>
    private static void ApplyMask(int mask, bool[,] dark, bool[,] function)
    {
        int size = dark.GetLength(0);
        for (int row = 0; row < size; row++)
            for (int column = 0; column < size; column++)
                if (!function[row, column] && MaskFlips(mask, row, column)) dark[row, column] = !dark[row, column];
    }

    /// <summary>The eight mask conditions of ISO/IEC 18004 table 10.</summary>
    internal static bool MaskFlips(int mask, int row, int column) => mask switch
    {
        0 => (row + column) % 2 == 0,
        1 => row % 2 == 0,
        2 => column % 3 == 0,
        3 => (row + column) % 3 == 0,
        4 => (row / 2 + column / 3) % 2 == 0,
        5 => row * column % 2 + row * column % 3 == 0,
        6 => (row * column % 2 + row * column % 3) % 2 == 0,
        7 => ((row + column) % 2 + row * column % 3) % 2 == 0,
        _ => throw new ArgumentOutOfRangeException(nameof(mask)),
    };

    /// <summary>The standard's score for how hard a symbol is to scan; lower is better.</summary>
    internal static int Penalty(bool[,] dark)
    {
        int size = dark.GetLength(0), penalty = 0, darkCount = 0;
        var line = new bool[size];
        for (int i = 0; i < size; i++)
        {
            for (int j = 0; j < size; j++) line[j] = dark[i, j]; // row i
            penalty += LinePenalty(line);
            for (int j = 0; j < size; j++) line[j] = dark[j, i]; // column i
            penalty += LinePenalty(line);
        }
        for (int row = 0; row < size; row++)
            for (int column = 0; column < size; column++)
            {
                if (dark[row, column]) darkCount++;
                // Rule 2: every 2x2 block of one colour costs 3.
                if (row + 1 < size && column + 1 < size && dark[row, column] == dark[row, column + 1]
                    && dark[row, column] == dark[row + 1, column] && dark[row, column] == dark[row + 1, column + 1]) penalty += 3;
            }
        // Rule 4: every full 5% the share of dark modules is away from 50% costs 10.
        int total = size * size;
        return penalty + 10 * (Math.Abs(darkCount * 20 - total * 10) / total);
    }

    // Dark-light-dark-dark-dark-light-dark with four light modules on one side: what a finder
    // pattern looks like along a line.
    private static readonly bool[] FinderThenLight = [true, false, true, true, true, false, true, false, false, false, false];
    private static readonly bool[] LightThenFinder = [false, false, false, false, true, false, true, true, true, false, true];

    private static int LinePenalty(bool[] line)
    {
        int penalty = 0, run = 1;
        // Rule 1: five modules of one colour in a row cost 3, each further one 1 more.
        for (int i = 1; i <= line.Length; i++)
        {
            if (i < line.Length && line[i] == line[i - 1]) { run++; continue; }
            if (run >= 5) penalty += run - 2;
            run = 1;
        }
        // Rule 3: each finder look-alike costs 40.
        for (int start = 0; start + FinderThenLight.Length <= line.Length; start++)
        {
            var window = line.AsSpan(start, FinderThenLight.Length);
            if (window.SequenceEqual(FinderThenLight) || window.SequenceEqual(LightThenFinder)) penalty += 40;
        }
        return penalty;
    }
}
