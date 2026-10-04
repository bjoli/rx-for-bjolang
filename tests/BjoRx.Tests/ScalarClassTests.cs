using System.Collections;
using BjoRx.Syntax;

namespace BjoRx.Tests;

/// <summary>The class tests from regex-syntax/src/hir/mod.rs, plus a randomized check.</summary>
public static class ScalarClassTests
{
    private static ScalarClass U() => ScalarClass.Empty();

    private static ScalarClass U(params (uint, uint)[] ranges) =>
        new(ranges.Select(r => new ScalarRange(r.Item1, r.Item2)));

    private static ScalarClass U(params (char, char)[] ranges) =>
        U(ranges.Select(r => ((uint)r.Item1, (uint)r.Item2)).ToArray());

    private static ScalarClass Op(ScalarClass a, Action<ScalarClass> op)
    {
        var c = a.Clone();
        op(c);
        return c;
    }

    private static ScalarClass Union(ScalarClass a, ScalarClass b) => Op(a, c => c.Union(b));
    private static ScalarClass Intersect(ScalarClass a, ScalarClass b) => Op(a, c => c.Intersect(b));
    private static ScalarClass Difference(ScalarClass a, ScalarClass b) => Op(a, c => c.Difference(b));
    private static ScalarClass SymDifference(ScalarClass a, ScalarClass b) => Op(a, c => c.SymmetricDifference(b));
    private static ScalarClass Negate(ScalarClass a) => Op(a, c => c.Negate());

    [Test]
    public static void RangeCanonical()
    {
        var range = new ScalarRange(0xFF, 0);
        Check.Equal(0u, range.Start, "start");
        Check.Equal(0xFFu, range.End, "end");
    }

    [Test]
    public static void RangeRejectsSurrogates()
    {
        Check.Throws<ArgumentOutOfRangeException>(() => _ = new ScalarRange(0xD800, 0xD800), "surrogate");
        Check.Throws<ArgumentOutOfRangeException>(() => _ = new ScalarRange(0, 0x110000), "too big");
    }

    [Test]
    public static void Canonicalize()
    {
        Check.Equal(U(('a', 'c'), ('x', 'z')), U(('a', 'c'), ('x', 'z')), "sorted");
        Check.SequenceEqual(U(('a', 'c'), ('x', 'z')).Ranges, U(('x', 'z'), ('a', 'c')).Ranges, "reordered");
        Check.SequenceEqual(U(('w', 'z')).Ranges, U(('x', 'z'), ('w', 'y')).Ranges, "overlap");
        Check.SequenceEqual(
            U(('a', 'j'), ('l', 's')).Ranges,
            U(('c', 'f'), ('a', 'g'), ('d', 'j'), ('a', 'c'), ('m', 'p'), ('l', 's')).Ranges,
            "many");
        Check.SequenceEqual(U(('u', 'z')).Ranges, U(('x', 'z'), ('u', 'w')).Ranges, "adjacent");
        Check.SequenceEqual(U((0u, 0x10FFFFu)).Ranges, U((0u, 0x10FFFFu), (0u, 0x10FFFFu)).Ranges, "all twice");
        Check.SequenceEqual(U(('a', 'b')).Ranges, U(('a', 'a'), ('b', 'b')).Ranges, "singletons");
    }

    [Test]
    public static void NegateUnicode()
    {
        Check.Equal(U((0u, 0x60u), (0x62u, 0x10FFFFu)), Negate(U(('a', 'a'))), "a");
        Check.Equal(U((0u, 0x60u), (0x63u, 0x10FFFFu)), Negate(U(('a', 'a'), ('b', 'b'))), "ab");
        Check.Equal(U((0u, 0x60u), (0x64u, 0x77u), (0x7Bu, 0x10FFFFu)), Negate(U(('a', 'c'), ('x', 'z'))), "a-c x-z");
        Check.Equal(U((0x62u, 0x10FFFFu)), Negate(U(((char)0, 'a'))), "up to a");
        Check.Equal(U((0u, 0x60u)), Negate(U((0x61u, 0x10FFFFu))), "from a");
        Check.Equal(U(), Negate(U((0u, 0x10FFFFu))), "all");
        Check.Equal(U((0u, 0x10FFFFu)), Negate(U()), "none");
        Check.Equal(U((0x10FFFEu, 0x10FFFEu)), Negate(U((0u, 0x10FFFDu), (0x10FFFFu, 0x10FFFFu))), "one hole");
        Check.Equal(U((0xE000u, 0x10FFFFu)), Negate(U((0u, 0xD7FFu))), "below surrogates");
        Check.Equal(U((0xD7FFu, 0x10FFFFu)), Negate(U((0u, 0xD7FEu))), "below D7FF");
        Check.Equal(U((0u, 0xD7FFu)), Negate(U((0xE000u, 0x10FFFFu))), "above surrogates");
        Check.Equal(U((0u, 0xE000u)), Negate(U((0xE001u, 0x10FFFFu))), "above E000");
    }

    [Test]
    public static void UnionUnicode()
    {
        Check.Equal(U(('a', 'z'), ('A', 'C')), Union(U(('a', 'g'), ('m', 't'), ('A', 'C')), U(('a', 'z'))), "union");
    }

    [Test]
    public static void IntersectUnicode()
    {
        Check.Equal(U(), Intersect(U(), U(('a', 'a'))), "1");
        Check.Equal(U(('a', 'a')), Intersect(U(('a', 'a')), U(('a', 'a'))), "2");
        Check.Equal(U(), Intersect(U(('a', 'a')), U(('b', 'b'))), "3");
        Check.Equal(U(('a', 'a')), Intersect(U(('a', 'a')), U(('a', 'c'))), "4");
        Check.Equal(U(('a', 'b')), Intersect(U(('a', 'b')), U(('a', 'c'))), "5");
        Check.Equal(U(('b', 'b')), Intersect(U(('a', 'b')), U(('b', 'c'))), "6");
        Check.Equal(U(), Intersect(U(('a', 'b')), U(('c', 'd'))), "7");
        Check.Equal(U(('b', 'c')), Intersect(U(('b', 'c')), U(('a', 'd'))), "8");
        Check.Equal(U(('a', 'b'), ('d', 'e'), ('g', 'h')), Intersect(U(('a', 'b'), ('d', 'e'), ('g', 'h')), U(('a', 'h'))), "9");
        Check.Equal(
            U(('a', 'b'), ('d', 'e'), ('g', 'h')),
            Intersect(U(('a', 'b'), ('d', 'e'), ('g', 'h')), U(('a', 'b'), ('d', 'e'), ('g', 'h'))),
            "10");
        Check.Equal(U(), Intersect(U(('a', 'b'), ('g', 'h')), U(('d', 'e'), ('k', 'l'))), "11");
        Check.Equal(U(('h', 'h')), Intersect(U(('a', 'b'), ('d', 'e'), ('g', 'h')), U(('h', 'h'))), "12");
        Check.Equal(U(), Intersect(U(('a', 'b'), ('e', 'f'), ('i', 'j')), U(('c', 'd'), ('g', 'h'), ('k', 'l'))), "13");
        Check.Equal(U(('b', 'f')), Intersect(U(('a', 'b'), ('c', 'd'), ('e', 'f')), U(('b', 'c'), ('d', 'e'), ('f', 'g'))), "14");
    }

    [Test]
    public static void DifferenceUnicode()
    {
        Check.Equal(U(), Difference(U(('a', 'a')), U(('a', 'a'))), "1");
        Check.Equal(U(('a', 'a')), Difference(U(('a', 'a')), U()), "2");
        Check.Equal(U(), Difference(U(), U(('a', 'a'))), "3");
        Check.Equal(U(('b', 'z')), Difference(U(('a', 'z')), U(('a', 'a'))), "4");
        Check.Equal(U(('a', 'y')), Difference(U(('a', 'z')), U(('z', 'z'))), "5");
        Check.Equal(U(('a', 'l'), ('n', 'z')), Difference(U(('a', 'z')), U(('m', 'm'))), "6");
        Check.Equal(U(), Difference(U(('a', 'c'), ('g', 'i'), ('r', 't')), U(('a', 'z'))), "7");
        Check.Equal(U(('a', 'c')), Difference(U(('a', 'c'), ('g', 'i'), ('r', 't')), U(('d', 'v'))), "8");
        Check.Equal(
            U(('a', 'a'), ('h', 'i'), ('r', 'r')),
            Difference(U(('a', 'c'), ('g', 'i'), ('r', 't')), U(('b', 'g'), ('s', 'u'))),
            "9");
        Check.Equal(
            U(('a', 'a'), ('h', 'i'), ('r', 'r')),
            Difference(U(('a', 'c'), ('g', 'i'), ('r', 't')), U(('b', 'd'), ('e', 'g'), ('s', 'u'))),
            "10");
        Check.Equal(U(('x', 'z')), Difference(U(('x', 'z')), U(('a', 'c'), ('e', 'g'), ('s', 'u'))), "11");
        Check.Equal(
            U(('d', 'd'), ('h', 'r'), ('v', 'z')),
            Difference(U(('a', 'z')), U(('a', 'c'), ('e', 'g'), ('s', 'u'))),
            "12");
    }

    [Test]
    public static void SymmetricDifferenceUnicode()
    {
        Check.Equal(U(('a', 'f'), ('n', 't')), SymDifference(U(('a', 'm')), U(('g', 't'))), "symdiff");
    }

    [Test]
    public static void LengthsAndLiteral()
    {
        Check.Equal(1, U(('a', 'z')).MinimumLength, "ascii min");
        Check.Equal(4, U(('a', 'a'), (0x10000u, 0x10000u)).MaximumLength, "astral max");
        Check.Equal(null, U().MinimumLength, "empty min");
        Check.SequenceEqual(new byte[] { 0xE2, 0x98, 0x83 }, U((0x2603u, 0x2603u)).Literal()!, "snowman literal");
        Check.True(U(('a', 'b')).Literal() is null, "two scalars are no literal");
    }

    // --- Randomized against a bit set ---------------------------------------------------

    private const int Domain = 0x110000;

    private static BitArray Bits(ScalarClass c)
    {
        var bits = new BitArray(Domain);
        foreach (var r in c.Ranges)
        {
            for (uint x = r.Start; x <= r.End; x++)
            {
                if (x is < 0xD800 or > 0xDFFF) bits[(int)x] = true;
            }
        }
        return bits;
    }

    private static uint RandomScalar(Random rng)
    {
        // Mostly small values and values near the surrogate block and the top,
        // where the edge cases are.
        uint x = rng.Next(4) switch
        {
            0 => (uint)rng.Next(0, 300),
            1 => (uint)rng.Next(0xD700, 0xE100),
            2 => (uint)rng.Next(0x10FF00, 0x110000),
            _ => (uint)rng.Next(0, Domain),
        };
        return x is >= 0xD800 and <= 0xDFFF ? 0xD7FF : x;
    }

    private static ScalarClass RandomClass(Random rng)
    {
        int n = rng.Next(0, 6);
        var ranges = new List<ScalarRange>();
        for (int i = 0; i < n; i++) ranges.Add(new ScalarRange(RandomScalar(rng), RandomScalar(rng)));
        return new ScalarClass(ranges);
    }

    private static void IsCanonical(ScalarClass c, string what)
    {
        for (int i = 1; i < c.Ranges.Count; i++)
        {
            Check.True(c.Ranges[i - 1].End + 1 < c.Ranges[i].Start, $"{what}: {c} is not canonical");
        }
    }

    [Test]
    public static void RandomOperationsAgreeWithBitSets()
    {
        var rng = new Random(1234);
        for (int k = 0; k < 300; k++)
        {
            var a = RandomClass(rng);
            var b = RandomClass(rng);
            var ba = Bits(a);
            var bb = Bits(b);

            void Agree(ScalarClass got, BitArray expected, string op)
            {
                IsCanonical(got, op);
                var actual = Bits(got);
                for (int i = 0; i < Domain; i++)
                {
                    if (actual[i] != expected[i])
                    {
                        throw new CheckFailed($"{op} of {a} and {b} gave {got}, which is wrong at {i:X}");
                    }
                }
            }

            Agree(Union(a, b), new BitArray(ba).Or(bb), "union");
            Agree(Intersect(a, b), new BitArray(ba).And(bb), "intersect");
            Agree(Difference(a, b), new BitArray(ba).And(new BitArray(bb).Not()), "difference");
            Agree(SymDifference(a, b), new BitArray(ba).Xor(bb), "symmetric difference");
            var notA = new BitArray(ba).Not();
            for (int s = 0xD800; s <= 0xDFFF; s++) notA[s] = false;
            Agree(Negate(a), notA, "negate");
        }
    }
}
