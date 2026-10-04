using System.Text;
using BjoRx.Utf8;

namespace BjoRx.Tests;

public static class Utf8SequencesTests
{
    private static Utf8Range R(byte s, byte e) => new(s, e);

    private static Utf8Sequence Seq(params Utf8Range[] ranges) => new(ranges);

    private static byte[] EncodeSurrogate(uint cp)
    {
        return
        [
            (byte)(((cp >> 12) & 0x0F) | 0b1110_0000),
            (byte)(((cp >> 6) & 0x3F) | 0b1000_0000),
            (byte)((cp & 0x3F) | 0b1000_0000),
        ];
    }

    private static void NeverAcceptsSurrogates(uint start, uint end)
    {
        var seqs = Utf8Sequences.For(start, end);
        for (uint cp = 0xD800; cp < 0xE000; cp++)
        {
            var buf = EncodeSurrogate(cp);
            foreach (var r in seqs)
            {
                Check.True(!r.Matches(buf), $"({start:X}, {end:X}) has {r}, which matches surrogate {cp:X}");
            }
        }
    }

    // --- Upstream tests -----------------------------------------------------

    [Test]
    public static void CodepointsNoSurrogates()
    {
        NeverAcceptsSurrogates(0x0, 0xFFFF);
        NeverAcceptsSurrogates(0x0, 0x10FFFF);
        NeverAcceptsSurrogates(0x0, 0x10FFFE);
        NeverAcceptsSurrogates(0x80, 0x10FFFF);
        NeverAcceptsSurrogates(0xD7FF, 0xE000);
    }

    [Test]
    public static void SingleCodepointOneSequence()
    {
        var seqs = new Utf8Sequences(0, 0);
        for (uint i = 0; i <= 0x10FFFF; i++)
        {
            if (!Rune.IsValid(i)) continue;
            seqs.Reset(i, i);
            Check.True(seqs.TryNext(out _), $"no sequence for {i:X}");
            Check.True(!seqs.TryNext(out _), $"more than one sequence for {i:X}");
        }
    }

    [Test]
    public static void Bmp()
    {
        Check.SequenceEqual(
            [
                Seq(R(0x0, 0x7F)),
                Seq(R(0xC2, 0xDF), R(0x80, 0xBF)),
                Seq(R(0xE0, 0xE0), R(0xA0, 0xBF), R(0x80, 0xBF)),
                Seq(R(0xE1, 0xEC), R(0x80, 0xBF), R(0x80, 0xBF)),
                Seq(R(0xED, 0xED), R(0x80, 0x9F), R(0x80, 0xBF)),
                Seq(R(0xEE, 0xEF), R(0x80, 0xBF), R(0x80, 0xBF)),
            ],
            Utf8Sequences.For(0x0, 0xFFFF),
            "BMP");
    }

    [Test]
    public static void Reverse()
    {
        Check.Equal(Seq(R(0xA, 0xB)), Seq(R(0xA, 0xB)).Reversed(), "one");
        Check.Equal(Seq(R(0xB, 0xC), R(0xA, 0xB)), Seq(R(0xA, 0xB), R(0xB, 0xC)).Reversed(), "two");
        Check.Equal(
            Seq(R(0xC, 0xD), R(0xB, 0xC), R(0xA, 0xB)),
            Seq(R(0xA, 0xB), R(0xB, 0xC), R(0xC, 0xD)).Reversed(),
            "three");
        Check.Equal(
            Seq(R(0xD, 0xE), R(0xC, 0xD), R(0xB, 0xC), R(0xA, 0xB)),
            Seq(R(0xA, 0xB), R(0xB, 0xC), R(0xC, 0xD), R(0xD, 0xE)).Reversed(),
            "four");
    }

    [Test]
    public static void DocExample()
    {
        var seqs = Utf8Sequences.For(0x0, 0xFFFF);
        bool Matches(byte[] bytes) => seqs.Any(s => s.Matches(bytes) && s.Length == bytes.Length);

        Check.True(Matches([0x61]), "a");
        Check.True(Matches([0xE2, 0x98, 0x83]), "snowman");
        Check.True(!Matches([0xF0, 0x90, 0x8D, 0x88]), "outside the BMP");
        Check.True(!Matches([0xED, 0xA0, 0x80]), "surrogate");
        Check.True(!Matches([0xFF, 0xFF]), "invalid UTF-8");
    }

    // --- Exhaustive checks ----------------------------------------------------

    /// <summary>
    /// The number of sequences that match all of <paramref name="bytes"/>.
    /// A sequence only matches when its length equals the encoding's.
    /// </summary>
    private static int CountFull(List<Utf8Sequence> seqs, ReadOnlySpan<byte> bytes)
    {
        int n = 0;
        foreach (var s in seqs)
        {
            if (s.Length == bytes.Length && s.Matches(bytes)) n++;
        }
        return n;
    }

    /// <summary>
    /// Every scalar in <paramref name="scalars"/> is matched by exactly one
    /// sequence if it is in the range and by none if it is not.
    /// </summary>
    private static void ExactlyTheRange(uint start, uint end, IEnumerable<uint> scalars)
    {
        var seqs = Utf8Sequences.For(start, end);
        Span<byte> buf = stackalloc byte[4];
        foreach (uint cp in scalars)
        {
            if (!Rune.IsValid(cp)) continue;
            int n = new Rune(cp).EncodeToUtf8(buf);
            int expected = start <= cp && cp <= end ? 1 : 0;
            int actual = CountFull(seqs, buf[..n]);
            if (actual != expected)
            {
                throw new CheckFailed($"range ({start:X}, {end:X}): {cp:X} matched {actual} sequences, expected {expected}");
            }
        }
    }

    private static IEnumerable<uint> AllScalars()
    {
        for (uint cp = 0; cp <= 0x10FFFF; cp++) yield return cp;
    }

    [Test]
    public static void ExhaustiveOverInterestingRanges()
    {
        (uint, uint)[] ranges =
        [
            (0x0, 0x10FFFF), (0x0, 0x7F), (0x7F, 0x80), (0x80, 0x7FF), (0x7FF, 0x800),
            (0x800, 0xFFFF), (0xD7FF, 0xE000), (0xD000, 0xDFFF), (0xFFFF, 0x10000),
            (0x10000, 0x10FFFF), (0x3FF, 0x10401), (0x41, 0x5A), (0x400, 0x52F),
            (0x1F600, 0x1F64F), (0x123, 0x45678), (0x10FFFF, 0x10FFFF),
        ];
        foreach (var (s, e) in ranges) ExactlyTheRange(s, e, AllScalars());
    }

    [Test]
    public static void RandomRangesSampled()
    {
        var rng = new Random(20261004);
        for (int k = 0; k < 2000; k++)
        {
            uint a = (uint)rng.Next(0, 0x110000);
            uint b = (uint)rng.Next(0, 0x110000);
            if (a > b) (a, b) = (b, a);
            var probes = new List<uint> { a, b, a == 0 ? 0 : a - 1, b + 1 };
            for (int i = 0; i < 300; i++) probes.Add((uint)rng.Next(0, 0x110000));
            for (int i = 0; i < 100; i++) probes.Add((uint)rng.Next((int)a, (int)b + 1));
            ExactlyTheRange(a, b, probes.Where(p => p <= 0x10FFFF));
        }
    }

    [Test]
    public static void NeverMatchesInvalidUtf8()
    {
        var seqs = Utf8Sequences.For(0x0, 0x10FFFF);
        byte[][] invalid =
        [
            [0x80], [0xBF], [0xC0, 0x80], [0xC1, 0xBF], [0xE0, 0x80, 0x80], [0xE0, 0x9F, 0xBF],
            [0xF0, 0x80, 0x80, 0x80], [0xF0, 0x8F, 0xBF, 0xBF], [0xF4, 0x90, 0x80, 0x80],
            [0xF5, 0x80, 0x80, 0x80], [0xFF], [0xFE],
        ];
        foreach (var bytes in invalid)
        {
            Check.Equal(0, CountFull(seqs, bytes), Convert.ToHexString(bytes));
        }
    }
}
