// Ported from regex-syntax/src/hir/interval.rs and the ClassUnicode parts of
// regex-syntax/src/hir/mod.rs (rust-lang/regex). MIT OR Apache-2.0; see
// THIRD-PARTY.md.

using System.Text;

namespace BjoRx.Syntax;

/// <summary>
/// An inclusive range of Unicode scalar values. Neither end is a surrogate,
/// but the range may span the surrogate block; the surrogates in it are not
/// members.
/// </summary>
public readonly record struct ScalarRange : IComparable<ScalarRange>
{
    public uint Start { get; }
    public uint End { get; }

    /// <summary>The range between two scalars, in either order.</summary>
    public ScalarRange(uint a, uint b)
    {
        if (!Rune.IsValid(a)) throw new ArgumentOutOfRangeException(nameof(a), $"{a:X} is not a scalar value");
        if (!Rune.IsValid(b)) throw new ArgumentOutOfRangeException(nameof(b), $"{b:X} is not a scalar value");
        (Start, End) = a <= b ? (a, b) : (b, a);
    }

    public int CompareTo(ScalarRange other)
    {
        int c = Start.CompareTo(other.Start);
        return c != 0 ? c : End.CompareTo(other.End);
    }

    /// <summary>Overlapping or adjacent, counting by number rather than by scalar.</summary>
    internal bool IsContiguous(ScalarRange other) =>
        Math.Max(Start, other.Start) <= Math.Min(End, other.End) + 1;

    internal bool IsIntersectionEmpty(ScalarRange other) =>
        Math.Max(Start, other.Start) > Math.Min(End, other.End);

    internal bool IsSubsetOf(ScalarRange other) =>
        other.Start <= Start && Start <= other.End && other.Start <= End && End <= other.End;

    internal ScalarRange? Union(ScalarRange other) =>
        IsContiguous(other) ? new ScalarRange(Math.Min(Start, other.Start), Math.Max(End, other.End)) : null;

    internal ScalarRange? Intersect(ScalarRange other)
    {
        uint lo = Math.Max(Start, other.Start);
        uint hi = Math.Min(End, other.End);
        return lo <= hi ? new ScalarRange(lo, hi) : null;
    }

    /// <summary>This range without <paramref name="other"/>: none, one or two ranges.</summary>
    internal (ScalarRange? First, ScalarRange? Second) Difference(ScalarRange other)
    {
        if (IsSubsetOf(other)) return (null, null);
        if (IsIntersectionEmpty(other)) return (this, null);
        bool addLower = other.Start > Start;
        bool addUpper = other.End < End;
        ScalarRange? first = null, second = null;
        if (addLower) first = new ScalarRange(Start, Decrement(other.Start));
        if (addUpper)
        {
            var range = new ScalarRange(Increment(other.End), End);
            if (first is null) first = range;
            else second = range;
        }
        return (first, second);
    }

    // The next and previous scalar, stepping over the surrogate block.
    internal static uint Increment(uint c) => c == 0xD7FF ? 0xE000 : checked(c + 1);

    internal static uint Decrement(uint c) => c == 0xE000 ? 0xD7FF : checked(c - 1);

    public override string ToString() => Start == End ? $"{Start:X}" : $"{Start:X}-{End:X}";
}

/// <summary>
/// A set of Unicode scalar values, kept canonical: sorted, with no two ranges
/// overlapping or adjacent.
/// </summary>
///
/// <remarks>
/// The upstream <c>IntervalSet</c> without case folding, which this subset has
/// no use for. The operations work in place, and append their result after the
/// existing ranges before removing those, so that a set is rebuilt in one list.
/// </remarks>
public sealed class ScalarClass : IEquatable<ScalarClass>
{
    private const uint MinScalar = 0;
    private const uint MaxScalar = 0x10FFFF;

    private readonly List<ScalarRange> _ranges;

    public ScalarClass(IEnumerable<ScalarRange> ranges)
    {
        _ranges = ranges.ToList();
        Canonicalize();
    }

    public static ScalarClass Empty() => new([]);

    public ScalarClass Clone() => new(_ranges);

    public IReadOnlyList<ScalarRange> Ranges => _ranges;

    public bool IsEmpty => _ranges.Count == 0;

    public void Push(ScalarRange range)
    {
        _ranges.Add(range);
        Canonicalize();
    }

    public void Union(ScalarClass other)
    {
        if (other._ranges.Count == 0 || _ranges.SequenceEqual(other._ranges)) return;
        _ranges.AddRange(other._ranges);
        Canonicalize();
    }

    public void Intersect(ScalarClass other)
    {
        if (_ranges.Count == 0) return;
        if (other._ranges.Count == 0)
        {
            _ranges.Clear();
            return;
        }

        int drainEnd = _ranges.Count;
        int a = 0, b = 0;
        while (true)
        {
            if (_ranges[a].Intersect(other._ranges[b]) is { } ab) _ranges.Add(ab);
            if (_ranges[a].End < other._ranges[b].End)
            {
                if (++a >= drainEnd) break;
            }
            else if (++b >= other._ranges.Count)
            {
                break;
            }
        }
        _ranges.RemoveRange(0, drainEnd);
    }

    public void Difference(ScalarClass other)
    {
        if (_ranges.Count == 0 || other._ranges.Count == 0) return;

        // Both sides are canonical. An `a` range can lose pieces to several `b`
        // ranges, and a `b` range that reaches past the current `a` range is
        // kept for the next one.
        int drainEnd = _ranges.Count;
        int a = 0, b = 0;
        while (a < drainEnd && b < other._ranges.Count)
        {
            if (other._ranges[b].End < _ranges[a].Start)
            {
                b++;
                continue;
            }
            if (_ranges[a].End < other._ranges[b].Start)
            {
                _ranges.Add(_ranges[a]);
                a++;
                continue;
            }

            var range = _ranges[a];
            bool lostAll = false;
            while (b < other._ranges.Count && !range.IsIntersectionEmpty(other._ranges[b]))
            {
                var oldRange = range;
                var (first, second) = range.Difference(other._ranges[b]);
                if (first is null)
                {
                    lostAll = true;
                    break;
                }
                if (second is null)
                {
                    range = first.Value;
                }
                else
                {
                    _ranges.Add(first.Value);
                    range = second.Value;
                }
                if (other._ranges[b].End > oldRange.End) break;
                b++;
            }
            if (!lostAll) _ranges.Add(range);
            a++;
        }
        while (a < drainEnd)
        {
            _ranges.Add(_ranges[a]);
            a++;
        }
        _ranges.RemoveRange(0, drainEnd);
    }

    public void SymmetricDifference(ScalarClass other)
    {
        var intersection = Clone();
        intersection.Intersect(other);
        Union(other);
        Difference(intersection);
    }

    public void Negate()
    {
        if (_ranges.Count == 0)
        {
            _ranges.Add(new ScalarRange(MinScalar, MaxScalar));
            return;
        }

        int drainEnd = _ranges.Count;
        if (_ranges[0].Start > MinScalar)
        {
            _ranges.Add(new ScalarRange(MinScalar, ScalarRange.Decrement(_ranges[0].Start)));
        }
        for (int i = 1; i < drainEnd; i++)
        {
            _ranges.Add(new ScalarRange(
                ScalarRange.Increment(_ranges[i - 1].End),
                ScalarRange.Decrement(_ranges[i].Start)));
        }
        if (_ranges[drainEnd - 1].End < MaxScalar)
        {
            _ranges.Add(new ScalarRange(ScalarRange.Increment(_ranges[drainEnd - 1].End), MaxScalar));
        }
        _ranges.RemoveRange(0, drainEnd);
    }

    public bool IsAscii => _ranges.Count == 0 || _ranges[^1].End <= 0x7F;

    /// <summary>
    /// The length in bytes of the shortest UTF-8 string this matches, or null
    /// when the class is empty. A smaller scalar never encodes longer.
    /// </summary>
    public int? MinimumLength => _ranges.Count == 0 ? null : new Rune(_ranges[0].Start).Utf8SequenceLength;

    public int? MaximumLength => _ranges.Count == 0 ? null : new Rune(_ranges[^1].End).Utf8SequenceLength;

    /// <summary>The UTF-8 encoding of the one scalar this matches, if it matches exactly one.</summary>
    public byte[]? Literal()
    {
        if (_ranges.Count != 1 || _ranges[0].Start != _ranges[0].End) return null;
        var buf = new byte[4];
        int n = new Rune(_ranges[0].Start).EncodeToUtf8(buf);
        return buf[..n];
    }

    private void Canonicalize()
    {
        if (IsCanonical()) return;
        _ranges.Sort();

        int drainEnd = _ranges.Count;
        for (int old = 0; old < drainEnd; old++)
        {
            if (_ranges.Count > drainEnd && _ranges[^1].Union(_ranges[old]) is { } union)
            {
                _ranges[^1] = union;
                continue;
            }
            _ranges.Add(_ranges[old]);
        }
        _ranges.RemoveRange(0, drainEnd);
    }

    private bool IsCanonical()
    {
        for (int i = 1; i < _ranges.Count; i++)
        {
            if (_ranges[i - 1].CompareTo(_ranges[i]) >= 0) return false;
            if (_ranges[i - 1].IsContiguous(_ranges[i])) return false;
        }
        return true;
    }

    public bool Equals(ScalarClass? other) => other is not null && _ranges.SequenceEqual(other._ranges);

    public override bool Equals(object? obj) => obj is ScalarClass other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var r in _ranges) hash.Add(r);
        return hash.ToHashCode();
    }

    public override string ToString() => "[" + string.Join(" ", _ranges) + "]";
}
