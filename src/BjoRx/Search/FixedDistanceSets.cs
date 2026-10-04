// Not ported from rust-lang/regex: the idea is .NET's RegexFindOptimizations
// (FixedDistanceSets), applied to the UTF-8 NFA; no .NET code is used. MIT
// OR Apache-2.0 like the rest of BjoRx.

using System.Buffers;
using BjoRx.Nfa;
using BjoRx.Syntax;

namespace BjoRx.Search;

/// <summary>
/// A prefilter for patterns without good literals: the bytes that can occur
/// at each fixed distance from the start of a match. It searches for the
/// rarest of these sets and checks up to two more at their distances.
/// </summary>
///
/// <remarks>
/// <para>
/// <c>[A-Z][0-9]{5}[a-z]</c> has no literal prefix, but every match has a
/// digit at distance 1 and an uppercase letter at 0, which a vectorized
/// <see cref="SearchValues{T}"/> search finds quickly. In UTF-8 this also
/// covers big classes of multi-byte scalars, whose encodings share bytes:
/// every scalar in <c>[😀-🙏]</c> starts <c>F0 9F 98</c> or <c>F0 9F 99</c>.
/// </para>
/// <para>
/// The sets come from the forward NFA, level by level from its anchored
/// start: the bytes its states can consume after <i>d</i> bytes. Assertions
/// are assumed to hold, which only adds bytes. The analysis stops at the
/// first level where a match can end, since longer distances need not exist.
/// </para>
/// </remarks>
public sealed class FixedDistanceSets : Prefilter
{
    /// <summary>How far from a match's start bytes are collected.</summary>
    internal const int MaxDistance = 8;

    // A set is worth searching for only if its estimated frequency (see
    // Score) is below this: digits or uppercase letters are, lowercase
    // letters or a space are not.
    private const double UsefulScore = 0.5;

    private readonly int _primaryDistance;
    private readonly SearchValues<byte> _primary;
    private readonly bool[] _primaryBytes;
    private readonly int _primaryCount;
    private readonly (int Distance, bool[] Bytes)[] _checks;

    private FixedDistanceSets(ByteSetAt primary, ByteSetAt[] checks)
    {
        _primaryDistance = primary.Distance;
        _primaryBytes = primary.Bytes;
        _primaryCount = primary.Count;
        _primaryScore = primary.Score;
        _primary = SearchValues.Create(Enumerable.Range(0, 256).Where(b => primary.Bytes[b]).Select(b => (byte)b).ToArray());
        _checks = checks.Select(c => (c.Distance, c.Bytes)).ToArray();
    }

    /// <summary>The bytes that can be at <see cref="Distance"/> from a match's start.</summary>
    public sealed record ByteSetAt(int Distance, bool[] Bytes)
    {
        public int Count => Bytes.Count(b => b);

        /// <summary>
        /// How often the set's bytes occur, estimated from their ranks as
        /// Zipf's law would have it: the byte ranked k-th most common has
        /// frequency ∝ 1/k. Lower is better.
        /// </summary>
        public double Score
        {
            get
            {
                double score = 0;
                for (int b = 0; b < 256; b++)
                {
                    if (Bytes[b]) score += 1.0 / (256 - Rank.Of((byte)b));
                }
                return score;
            }
        }

        public override string ToString()
        {
            var ranges = new List<string>();
            for (int b = 0; b < 256; b++)
            {
                if (!Bytes[b]) continue;
                int e = b;
                while (e < 255 && Bytes[e + 1]) e++;
                ranges.Add(b == e ? $"{b:X2}" : $"{b:X2}-{e:X2}");
                b = e;
            }
            return $"{Distance}:[{string.Join(" ", ranges)}]";
        }
    }

    // Below this, the searched bytes are rare: '-', '@', 'X' or a UTF-8
    // continuation byte like 9F, but not 'a' or 'e'.
    private const double FastScore = 0.1;

    private readonly double _primaryScore;

    /// <summary>
    /// A few rare bytes: a cheap vectorized compare with few false
    /// candidates, as good as a literal search.
    /// </summary>
    public override bool IsFast => _primaryCount <= 3 && _primaryScore < FastScore;

    public override string Name => "fixed-distance sets";

    /// <summary>
    /// The byte sets at distances 0, 1, … from a match's start, up to
    /// <see cref="MaxDistance"/> and short of the shortest match.
    /// </summary>
    public static List<ByteSetAt> Analyze(NFA nfa)
    {
        var result = new List<ByteSetAt>();
        var current = new SparseSet(nfa.States.Length);
        var next = new SparseSet(nfa.States.Length);
        var stack = new Stack<int>();
        bool canEnd = Closure(nfa, nfa.StartAnchored, current, stack);
        for (int d = 0; d < MaxDistance && !canEnd && !current.IsEmpty; d++)
        {
            var bytes = new bool[256];
            next.Clear();
            canEnd = false;
            for (int i = 0; i < current.Count; i++)
            {
                ref readonly var s = ref nfa.States[current[i]];
                if (s.Kind == StateKind.ByteRange)
                {
                    bytes.AsSpan(s.Trans.Start, s.Trans.End - s.Trans.Start + 1).Fill(true);
                    canEnd |= Closure(nfa, s.Trans.Next, next, stack);
                }
                else if (s.Kind == StateKind.Sparse)
                {
                    foreach (var t in s.Transitions)
                    {
                        bytes.AsSpan(t.Start, t.End - t.Start + 1).Fill(true);
                        canEnd |= Closure(nfa, t.Next, next, stack);
                    }
                }
            }
            result.Add(new ByteSetAt(d, bytes));
            (current, next) = (next, current);
        }
        return result;
    }

    /// <summary>
    /// Adds the states reachable from <paramref name="start"/> without
    /// consuming a byte to <paramref name="set"/>; true if a Match is among them.
    /// </summary>
    private static bool Closure(NFA nfa, int start, SparseSet set, Stack<int> stack)
    {
        bool match = false;
        stack.Push(start);
        while (stack.TryPop(out int id))
        {
            if (!set.Insert(id)) continue;
            ref readonly var s = ref nfa.States[id];
            switch (s.Kind)
            {
                case StateKind.Match:
                    match = true;
                    break;
                case StateKind.Union:
                    foreach (int alt in s.Alternates) stack.Push(alt);
                    break;
                case StateKind.BinaryUnion:
                    stack.Push(s.Alt1);
                    stack.Push(s.Alt2);
                    break;
                case StateKind.Capture or StateKind.Look:
                    stack.Push(s.Next);
                    break;
            }
        }
        return match;
    }

    /// <summary>
    /// The prefilter for <paramref name="nfa"/>, or null when no set is rare
    /// enough to be worth a search (or the empty string matches).
    /// </summary>
    public static FixedDistanceSets? FromNfa(NFA nfa)
    {
        var sets = Analyze(nfa).Where(s => s.Count < 256).OrderBy(s => s.Score).ThenBy(s => s.Distance).ToList();
        if (sets.Count == 0 || sets[0].Score >= UsefulScore) return null;
        // Up to two more sets, the rarest, are checked at each candidate.
        return new FixedDistanceSets(sets[0], sets.Skip(1).Take(2).ToArray());
    }

    /// <summary>
    /// The leftmost position at or after <paramref name="start"/> where a
    /// match can start, as an empty span; every set must fit before
    /// <paramref name="end"/>, since a match lies inside the span.
    /// </summary>
    public override Match? Find(ReadOnlySpan<byte> haystack, int start, int end)
    {
        int pos = start;
        while (true)
        {
            int from = pos + _primaryDistance;
            if (from >= end) return null;
            int i = haystack[from..end].IndexOfAny(_primary);
            if (i < 0) return null;
            int candidate = from + i - _primaryDistance;
            if (ChecksHold(haystack, candidate, end)) return new Match(candidate, candidate);
            pos = candidate + 1;
        }
    }

    public override Match? Prefix(ReadOnlySpan<byte> haystack, int start, int end)
    {
        int at = start + _primaryDistance;
        return at < end && _primaryBytes[haystack[at]] && ChecksHold(haystack, start, end) ? new Match(start, start) : null;
    }

    private bool ChecksHold(ReadOnlySpan<byte> haystack, int candidate, int end)
    {
        foreach (var (distance, bytes) in _checks)
        {
            int at = candidate + distance;
            if (at >= end || !bytes[haystack[at]]) return false;
        }
        return true;
    }

    public override string ToString() =>
        $"{Name} {new ByteSetAt(_primaryDistance, _primaryBytes)} then {string.Join(", ", _checks.Select(c => new ByteSetAt(c.Distance, c.Bytes)))}";
}
