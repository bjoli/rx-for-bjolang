// Ported from regex-syntax/src/hir/literal.rs (rust-lang/regex). MIT OR
// Apache-2.0; see THIRD-PARTY.md.

using System.Text;

namespace BjoRx.Syntax;

/// <summary>
/// A string of bytes that a match of some pattern starts with. Exact if the
/// whole match is this string, inexact if the match may go on.
/// </summary>
public sealed class Literal : IEquatable<Literal>
{
    public byte[] Bytes { get; private set; }

    public bool IsExact { get; private set; }

    private Literal(byte[] bytes, bool exact)
    {
        Bytes = bytes;
        IsExact = exact;
    }

    public static Literal Exact(ReadOnlySpan<byte> bytes) => new(bytes.ToArray(), true);

    public static Literal Inexact(ReadOnlySpan<byte> bytes) => new(bytes.ToArray(), false);

    public static Literal Exact(string text) => Exact(Encoding.UTF8.GetBytes(text));

    public static Literal Inexact(string text) => Inexact(Encoding.UTF8.GetBytes(text));

    public Literal Clone() => new(Bytes, IsExact);

    public int Length => Bytes.Length;

    public bool IsEmpty => Bytes.Length == 0;

    public void MakeInexact() => IsExact = false;

    /// <summary>Cuts the literal to its first <paramref name="length"/> bytes, which makes it inexact.</summary>
    public void KeepFirstBytes(int length)
    {
        if (length >= Length) return;
        MakeInexact();
        Bytes = Bytes[..length];
    }

    /// <summary>A literal a prefilter would find everywhere: empty, or one very common byte.</summary>
    internal bool IsPoisonous => IsEmpty || (Length == 1 && Rank.Of(Bytes[0]) >= 250);

    public bool Equals(Literal? other) => other is not null && IsExact == other.IsExact && Bytes.AsSpan().SequenceEqual(other.Bytes);

    public override bool Equals(object? obj) => obj is Literal other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Bytes);
        hash.Add(IsExact);
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        var sb = new StringBuilder(IsExact ? "E(\"" : "I(\"");
        foreach (byte b in Bytes) sb.Append(b is >= 0x20 and < 0x7F && b != (byte)'"' ? ((char)b).ToString() : $"\\x{b:X2}");
        return sb.Append("\")").ToString();
    }
}

/// <summary>
/// A sequence of literals in order of preference, or "infinite": every
/// string, when there are too many literals to be worth listing.
/// </summary>
public sealed class Seq : IEquatable<Seq>
{
    private List<Literal>? _literals;

    private Seq(List<Literal>? literals)
    {
        _literals = literals;
    }

    public static Seq Empty() => new([]);

    public static Seq Infinite() => new(null);

    public static Seq Singleton(Literal literal) => new([literal]);

    public static Seq Of(IEnumerable<Literal> literals)
    {
        var seq = Empty();
        foreach (var lit in literals) seq.Push(lit);
        return seq;
    }

    public Seq Clone() => new(_literals?.Select(l => l.Clone()).ToList());

    /// <summary>The literals, or null when the sequence is infinite.</summary>
    public IReadOnlyList<Literal>? Literals => _literals;

    /// <summary>Adds a literal, unless it is the same as the last one.</summary>
    public void Push(Literal literal)
    {
        if (_literals is null) return;
        if (_literals.Count > 0 && _literals[^1].Equals(literal)) return;
        _literals.Add(literal);
    }

    public void MakeInexact()
    {
        if (_literals is null) return;
        foreach (var lit in _literals) lit.MakeInexact();
    }

    public void MakeInfinite() => _literals = null;

    /// <summary>
    /// Replaces each exact literal by its concatenation with every literal of
    /// <paramref name="other"/>, which is left empty. Inexact literals already
    /// end their match and stay as they are.
    /// </summary>
    public void CrossForward(Seq other)
    {
        if (CrossPreamble(other) is not var (lits1, lits2)) return;
        var crossed = new List<Literal>(lits1.Count * lits2.Count);
        foreach (var selfLit in lits1)
        {
            if (!selfLit.IsExact)
            {
                crossed.Add(selfLit);
                continue;
            }
            foreach (var otherLit in lits2)
            {
                var lit = Literal.Exact([.. selfLit.Bytes, .. otherLit.Bytes]);
                if (!otherLit.IsExact) lit.MakeInexact();
                crossed.Add(lit);
            }
        }
        lits2.Clear();
        _literals = crossed;
        Dedup();
    }

    private (List<Literal>, List<Literal>)? CrossPreamble(Seq other)
    {
        if (other._literals is not { } lits2)
        {
            // Crossing with every string: an empty literal here could be
            // followed by anything, so nothing can be said; the others just
            // stop being exact.
            if (MinLiteralLength == 0) MakeInfinite();
            else MakeInexact();
            return null;
        }
        if (_literals is not { } lits1)
        {
            lits2.Clear();
            return null;
        }
        return (lits1, lits2);
    }

    /// <summary>Appends the literals of <paramref name="other"/>, which is left empty.</summary>
    public void Union(Seq other)
    {
        if (other._literals is not { } lits2)
        {
            MakeInfinite();
            return;
        }
        if (_literals is null) return;
        _literals.AddRange(lits2);
        lits2.Clear();
        Dedup();
    }

    /// <summary>Merges adjacent equal literals; if only one of them was exact, neither is.</summary>
    public void Dedup()
    {
        if (_literals is not { Count: > 1 } lits) return;
        int w = 0;
        for (int r = 1; r < lits.Count; r++)
        {
            if (lits[r].Bytes.AsSpan().SequenceEqual(lits[w].Bytes))
            {
                if (lits[r].IsExact != lits[w].IsExact) lits[w].MakeInexact();
                continue;
            }
            lits[++w] = lits[r];
        }
        lits.RemoveRange(w + 1, lits.Count - w - 1);
    }

    public void KeepFirstBytes(int length)
    {
        if (_literals is null) return;
        foreach (var lit in _literals) lit.KeepFirstBytes(length);
    }

    public bool IsFinite => _literals is not null;

    public bool IsEmpty => _literals is { Count: 0 };

    public int? Count => _literals?.Count;

    /// <summary>True if every literal is exact (and the sequence finite).</summary>
    public bool IsExact => _literals is { } lits && lits.All(l => l.IsExact);

    /// <summary>True if no literal is exact; an infinite sequence is.</summary>
    public bool IsInexact => _literals is not { } lits || lits.All(l => !l.IsExact);

    public int? MaxUnionLength(Seq other) => Count is { } a && other.Count is { } b ? SaturatingAdd(a, b) : null;

    public int? MaxCrossLength(Seq other) => Count is { } a && other.Count is { } b ? (int)Math.Min((long)a * b, int.MaxValue) : null;

    private static int SaturatingAdd(int a, int b) => (int)Math.Min((long)a + b, int.MaxValue);

    public int? MinLiteralLength => _literals is { Count: > 0 } lits ? lits.Min(l => l.Length) : null;

    public int? MaxLiteralLength => _literals is { Count: > 0 } lits ? lits.Max(l => l.Length) : null;

    public byte[]? LongestCommonPrefix()
    {
        if (_literals is not { Count: > 0 } lits) return null;
        var first = lits[0].Bytes;
        int length = first.Length;
        foreach (var lit in lits.Skip(1))
        {
            length = lit.Bytes.AsSpan().CommonPrefixLength(first.AsSpan(0, length));
            if (length == 0) return [];
        }
        return first[..length];
    }

    /// <summary>
    /// Shrinks the sequence into a good prefilter for leftmost-first search,
    /// keeping it correct: every match still starts with one of the literals.
    /// The result is infinite when no good prefilter is left.
    /// </summary>
    public void OptimizeForPrefixByPreference()
    {
        if (Count is not { } origLength) return;
        // An empty literal matches everywhere; nothing to search for.
        if (MinLiteralLength == 0)
        {
            MakeInfinite();
            return;
        }
        // A literal after one it starts with can never be the leftmost-first
        // match there, since the earlier one is preferred.
        PreferenceTrie.Minimize(_literals!, keepExact: true);

        if (LongestCommonPrefix() is { } fix)
        {
            // A short common prefix of rare bytes is a better search than
            // the many literals: one memchr.
            if (origLength > 1 && fix.Length is >= 1 and <= 3 && Rank.Of(fix[0]) < 200)
            {
                KeepFirstBytes(1);
                Dedup();
                return;
            }
            // A few exact literals are fast to search as they are; otherwise
            // a long enough common prefix is better.
            bool isFast = IsExact && Count <= 16;
            bool useFix = fix.Length > 4 || (fix.Length > 1 && !isFast);
            if (useFix)
            {
                KeepFirstBytes(fix.Length);
                Dedup();
            }
        }

        // Exact literals let the meta searcher skip the regex engine, so the
        // shrinking below is undone if it does not pay.
        Seq? exact = IsExact ? Clone() : null;
        // Shorter literals collapse into fewer, until there are few enough.
        foreach (var (keep, limit) in (ReadOnlySpan<(int, int)>)[(5, 10), (4, 10), (3, 64), (2, 64), (1, 10)])
        {
            if (Count is not { } length || length <= limit) break;
            KeepFirstBytes(keep);
            PreferenceTrie.Minimize(_literals!, keepExact: true);
        }
        if (_literals is { } finalLits && finalLits.Any(l => l.IsPoisonous)) MakeInfinite();
        if (exact is not null && (!IsFinite || MinLiteralLength is null or <= 2 || Count > 64))
        {
            _literals = exact._literals;
        }
    }

    public bool Equals(Seq? other)
    {
        if (other is null) return false;
        if (_literals is null || other._literals is null) return _literals is null && other._literals is null;
        return _literals.SequenceEqual(other._literals);
    }

    public override bool Equals(object? obj) => obj is Seq other && Equals(other);

    public override int GetHashCode() => _literals?.Count ?? -1;

    public override string ToString() => _literals is null ? "Seq[∞]" : "Seq[" + string.Join(", ", _literals) + "]";
}

/// <summary>
/// Removes the literals that an earlier literal is a prefix of: under
/// leftmost-first semantics, the earlier one always wins where both match.
/// </summary>
internal sealed class PreferenceTrie
{
    private readonly List<List<(byte Byte, int Next)>> _states = [];
    // For each state, the 1-based index of the literal ending there, or 0.
    private readonly List<int> _matches = [];
    private int _nextLiteralIndex = 1;

    /// <summary>
    /// Drops every literal that has an earlier literal as a prefix. Unless
    /// <paramref name="keepExact"/>, that earlier literal becomes inexact,
    /// since it stands for the dropped one too.
    /// </summary>
    public static void Minimize(List<Literal> literals, bool keepExact)
    {
        var trie = new PreferenceTrie();
        var makeInexact = new List<int>();
        var kept = new List<Literal>(literals.Count);
        foreach (var lit in literals)
        {
            int result = trie.Insert(lit.Bytes);
            if (result > 0)
            {
                kept.Add(lit);
            }
            else if (!keepExact)
            {
                makeInexact.Add(-result - 1);
            }
        }
        literals.Clear();
        literals.AddRange(kept);
        foreach (int i in makeInexact) literals[i].MakeInexact();
    }

    /// <summary>The new literal's 1-based index, or minus the index of an earlier literal that is its prefix.</summary>
    private int Insert(byte[] bytes)
    {
        int prev = Root();
        if (_matches[prev] != 0) return -_matches[prev];
        foreach (byte b in bytes)
        {
            var trans = _states[prev];
            int i = BinarySearch(trans, b);
            if (i >= 0)
            {
                prev = trans[i].Next;
                if (_matches[prev] != 0) return -_matches[prev];
            }
            else
            {
                int next = CreateState();
                trans.Insert(~i, (b, next));
                prev = next;
            }
        }
        int index = _nextLiteralIndex++;
        _matches[prev] = index;
        return index;
    }

    private static int BinarySearch(List<(byte Byte, int Next)> trans, byte b)
    {
        int lo = 0, hi = trans.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (trans[mid].Byte == b) return mid;
            if (trans[mid].Byte < b) lo = mid + 1;
            else hi = mid - 1;
        }
        return ~lo;
    }

    private int Root() => _states.Count > 0 ? 0 : CreateState();

    private int CreateState()
    {
        _states.Add([]);
        _matches.Add(0);
        return _states.Count - 1;
    }
}

/// <summary>
/// Extracts the literals that every match of an <see cref="Hir"/> starts
/// with, within limits that keep the sequence small. Upstream also extracts
/// suffixes, for search strategies that are not ported.
/// </summary>
public sealed class Extractor
{
    /// <summary>A class with more scalars than this is treated as matching anything.</summary>
    public int LimitClass { get; init; } = 10;

    /// <summary>Repetitions are unrolled at most this many times.</summary>
    public int LimitRepeat { get; init; } = 10;

    public int LimitLiteralLength { get; init; } = 100;

    /// <summary>The most literals a sequence may have.</summary>
    public int LimitTotal { get; init; } = 250;

    public Seq Extract(Hir hir)
    {
        switch (hir)
        {
            case HirEmpty or HirLook:
                return Seq.Singleton(Literal.Exact(ReadOnlySpan<byte>.Empty));
            case HirLiteral lit:
            {
                var seq = Seq.Singleton(Literal.Exact(lit.Bytes));
                EnforceLiteralLength(seq);
                return seq;
            }
            case HirClass cls:
                return ExtractClass(cls.Set);
            case HirRepetition rep:
                return ExtractRepetition(rep);
            case HirCapture cap:
                return Extract(cap.Sub);
            case HirConcat concat:
                return ExtractConcat(concat.Subs);
            case HirAlternation alt:
                return ExtractAlternation(alt.Subs);
            default:
                throw new ArgumentException($"unknown HIR {hir.GetType().Name}", nameof(hir));
        }
    }

    private Seq ExtractConcat(IEnumerable<Hir> subs)
    {
        var seq = Seq.Singleton(Literal.Exact(ReadOnlySpan<byte>.Empty));
        foreach (var sub in subs)
        {
            // Once no literal is exact, nothing more can be appended.
            if (seq.IsInexact) break;
            seq = Cross(seq, Extract(sub));
        }
        return seq;
    }

    private Seq ExtractAlternation(IEnumerable<Hir> subs)
    {
        var seq = Seq.Empty();
        foreach (var sub in subs)
        {
            if (!seq.IsFinite) break;
            seq = Union(seq, Extract(sub));
        }
        return seq;
    }

    private Seq ExtractRepetition(HirRepetition rep)
    {
        var sub = Extract(rep.Sub);
        if (rep.Min == 0)
        {
            if (rep.Max != 1) sub.MakeInexact();
            var empty = Seq.Singleton(Literal.Exact(ReadOnlySpan<byte>.Empty));
            // A lazy repetition prefers to match nothing.
            return rep.Greedy ? Union(sub, empty) : Union(empty, sub);
        }
        uint limit = (uint)LimitRepeat;
        var seq = Seq.Singleton(Literal.Exact(ReadOnlySpan<byte>.Empty));
        for (uint i = 0; i < Math.Min(rep.Min, limit); i++)
        {
            if (seq.IsInexact) break;
            seq = Cross(seq, sub.Clone());
        }
        if (rep.Max != rep.Min || rep.Min > limit) seq.MakeInexact();
        return seq;
    }

    private Seq ExtractClass(ScalarClass cls)
    {
        if (ClassOverLimit(cls)) return Seq.Infinite();
        var seq = Seq.Empty();
        Span<byte> buffer = stackalloc byte[4];
        foreach (var r in cls.Ranges)
        {
            for (uint c = r.Start; c <= r.End; c++)
            {
                int n = new Rune(c).EncodeToUtf8(buffer);
                seq.Push(Literal.Exact(buffer[..n]));
            }
        }
        EnforceLiteralLength(seq);
        return seq;
    }

    private bool ClassOverLimit(ScalarClass cls)
    {
        long count = 0;
        foreach (var r in cls.Ranges)
        {
            if (count > LimitClass) return true;
            count += r.End - r.Start + 1;
        }
        return count > LimitClass;
    }

    private Seq Cross(Seq seq1, Seq seq2)
    {
        if (seq1.MaxCrossLength(seq2) > LimitTotal) seq2.MakeInfinite();
        seq1.CrossForward(seq2);
        EnforceLiteralLength(seq1);
        return seq1;
    }

    private Seq Union(Seq seq1, Seq seq2)
    {
        if (seq1.MaxUnionLength(seq2) > LimitTotal)
        {
            // Too many: shorter literals may merge into fewer.
            seq1.KeepFirstBytes(4);
            seq2.KeepFirstBytes(4);
            seq1.Dedup();
            seq2.Dedup();
            if (seq1.MaxUnionLength(seq2) > LimitTotal) seq2.MakeInfinite();
        }
        seq1.Union(seq2);
        return seq1;
    }

    private void EnforceLiteralLength(Seq seq) => seq.KeepFirstBytes(LimitLiteralLength);
}
