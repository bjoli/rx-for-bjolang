// Ported from regex-automata/src/util/prefilter/ (rust-lang/regex). MIT OR
// Apache-2.0; see THIRD-PARTY.md.

using System.Buffers;
using BjoRx.Syntax;

namespace BjoRx.Search;

/// <summary>
/// A fast search for the literals that every match starts with. It finds
/// candidates: the regex engine starts at one and confirms or rejects it.
/// When the literals are exact, a candidate is a match.
/// </summary>
///
/// <remarks>
/// <para>
/// Upstream picks among memchr, memchr2, memchr3, memmem, Teddy, a byte set
/// and Aho-Corasick, in that order. Here the first four are .NET's
/// vectorized <c>IndexOf</c>/<c>IndexOfAny</c>, and Teddy and Aho-Corasick
/// are replaced by one search for the literals' first bytes
/// (<see cref="SearchValues{T}"/>) that then checks the literals starting
/// there in order of preference.
/// </para>
/// </remarks>
public abstract class Prefilter
{
    /// <summary>The length of the longest literal; 0 for a prefilter that searches for something else.</summary>
    public int MaxNeedleLength { get; private set; }

    /// <summary>
    /// Whether the search is likely much faster than the regex engine. The
    /// meta regex tries the reverse suffix and inner strategies only without
    /// a fast prefilter, and they need fast literal searches.
    /// </summary>
    public abstract bool IsFast { get; }

    public abstract string Name { get; }

    /// <summary>
    /// The leftmost literal in <c>haystack[start..end]</c>, and the most
    /// preferred one there, as its span; null if none.
    /// </summary>
    public abstract Match? Find(ReadOnlySpan<byte> haystack, int start, int end);

    /// <summary>The most preferred literal starting exactly at <paramref name="start"/>.</summary>
    public abstract Match? Prefix(ReadOnlySpan<byte> haystack, int start, int end);

    /// <summary>
    /// A prefilter for <paramref name="needles"/>, in order of preference;
    /// null when there are none or one is empty, since an empty literal
    /// matches everywhere.
    /// </summary>
    public static Prefilter? Create(IReadOnlyList<byte[]> needles)
    {
        if (needles.Count == 0 || needles.Any(n => n.Length == 0)) return null;
        bool allBytes = needles.All(n => n.Length == 1);
        Prefilter pre = (needles.Count, allBytes) switch
        {
            (1, true) => new Memchr(needles[0][0]),
            (2, true) => new Memchr2(needles[0][0], needles[1][0]),
            (3, true) => new Memchr3(needles[0][0], needles[1][0], needles[2][0]),
            (1, false) => new Memmem(needles[0]),
            (_, true) => new ByteSet(needles.Select(n => n[0])),
            _ => new FirstBytes(needles),
        };
        pre.MaxNeedleLength = needles.Max(n => n.Length);
        return pre;
    }

    /// <summary>
    /// The prefixes of <paramref name="hir"/>, optimized for leftmost-first
    /// search. Infinite when there is no good set of literals.
    /// </summary>
    public static Seq Prefixes(Hir hir)
    {
        var prefixes = new Extractor().Extract(hir);
        prefixes.OptimizeForPrefixByPreference();
        return prefixes;
    }

    public static Prefilter? FromHirPrefix(Hir hir) =>
        Prefixes(hir).Literals is { } lits ? Create(lits.Select(l => l.Bytes).ToList()) : null;

    public override string ToString() => Name;

    private static Match? At(int start, int length) => new Match(start, start + length);

    private sealed class Memchr(byte b) : Prefilter
    {
        public override bool IsFast => true;
        public override string Name => "memchr";

        public override Match? Find(ReadOnlySpan<byte> haystack, int start, int end)
        {
            int i = haystack[start..end].IndexOf(b);
            return i < 0 ? null : At(start + i, 1);
        }

        public override Match? Prefix(ReadOnlySpan<byte> haystack, int start, int end) =>
            start < end && haystack[start] == b ? At(start, 1) : null;
    }

    private sealed class Memchr2(byte b1, byte b2) : Prefilter
    {
        public override bool IsFast => true;
        public override string Name => "memchr2";

        public override Match? Find(ReadOnlySpan<byte> haystack, int start, int end)
        {
            int i = haystack[start..end].IndexOfAny(b1, b2);
            return i < 0 ? null : At(start + i, 1);
        }

        public override Match? Prefix(ReadOnlySpan<byte> haystack, int start, int end) =>
            start < end && (haystack[start] == b1 || haystack[start] == b2) ? At(start, 1) : null;
    }

    private sealed class Memchr3(byte b1, byte b2, byte b3) : Prefilter
    {
        public override bool IsFast => true;
        public override string Name => "memchr3";

        public override Match? Find(ReadOnlySpan<byte> haystack, int start, int end)
        {
            int i = haystack[start..end].IndexOfAny(b1, b2, b3);
            return i < 0 ? null : At(start + i, 1);
        }

        public override Match? Prefix(ReadOnlySpan<byte> haystack, int start, int end) =>
            start < end && (haystack[start] == b1 || haystack[start] == b2 || haystack[start] == b3) ? At(start, 1) : null;
    }

    private sealed class Memmem(byte[] needle) : Prefilter
    {
        public override bool IsFast => true;
        public override string Name => "memmem";

        public override Match? Find(ReadOnlySpan<byte> haystack, int start, int end)
        {
            int i = haystack[start..end].IndexOf(needle);
            return i < 0 ? null : At(start + i, needle.Length);
        }

        public override Match? Prefix(ReadOnlySpan<byte> haystack, int start, int end) =>
            haystack[start..end].StartsWith(needle) ? At(start, needle.Length) : null;
    }

    private sealed class ByteSet(IEnumerable<byte> bytes) : Prefilter
    {
        private readonly SearchValues<byte> _set = SearchValues.Create(bytes.ToArray());

        // Upstream: a byte set is not fast, since common bytes give many candidates.
        public override bool IsFast => false;
        public override string Name => "byteset";

        public override Match? Find(ReadOnlySpan<byte> haystack, int start, int end)
        {
            int i = haystack[start..end].IndexOfAny(_set);
            return i < 0 ? null : At(start + i, 1);
        }

        public override Match? Prefix(ReadOnlySpan<byte> haystack, int start, int end) =>
            start < end && _set.Contains(haystack[start]) ? At(start, 1) : null;
    }

    /// <summary>Several literals: a search for their first bytes, then a check of the literals starting there.</summary>
    private sealed class FirstBytes : Prefilter
    {
        private readonly SearchValues<byte> _firstBytes;
        // The literals by first byte, each list in order of preference.
        private readonly byte[][]?[] _byFirstByte = new byte[256][][];
        private readonly int _minimumLength;

        public FirstBytes(IReadOnlyList<byte[]> needles)
        {
            _firstBytes = SearchValues.Create(needles.Select(n => n[0]).Distinct().ToArray());
            foreach (var group in needles.GroupBy(n => n[0]))
            {
                _byFirstByte[group.Key] = group.ToArray();
            }
            _minimumLength = needles.Min(n => n.Length);
        }

        // Teddy's rule upstream: short literals give too many candidates.
        public override bool IsFast => _minimumLength >= 3;
        public override string Name => "firstbytes";

        public override Match? Find(ReadOnlySpan<byte> haystack, int start, int end)
        {
            int at = start;
            while (at < end)
            {
                int i = haystack[at..end].IndexOfAny(_firstBytes);
                if (i < 0) return null;
                at += i;
                if (Prefix(haystack, at, end) is { } m) return m;
                at++;
            }
            return null;
        }

        public override Match? Prefix(ReadOnlySpan<byte> haystack, int start, int end)
        {
            if (start >= end || _byFirstByte[haystack[start]] is not { } candidates) return null;
            var rest = haystack[start..end];
            foreach (var needle in candidates)
            {
                if (rest.StartsWith(needle)) return At(start, needle.Length);
            }
            return null;
        }
    }
}
