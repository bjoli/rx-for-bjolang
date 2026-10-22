// Ported from the Look and LookSet parts of regex-syntax/src/hir/mod.rs
// (rust-lang/regex). MIT OR Apache-2.0; see THIRD-PARTY.md.

namespace BjoRx.Syntax;

/// <summary>
/// A zero-width assertion. Only the two that <c>(std rx)</c> can write are
/// here: the start and the end of the haystack, <c>\A</c> and <c>\z</c>. The
/// values are the upstream bits, so that more can be added without renumbering.
/// </summary>
public enum Look : ushort
{
    Start = 1 << 0,
    End = 1 << 1,
}

[NonUserCode]
public static class LookExtensions
{
    /// <summary>The same assertion for a search that runs backwards.</summary>
    public static Look Reversed(this Look look) => look switch
    {
        Look.Start => Look.End,
        Look.End => Look.Start,
        _ => throw new ArgumentOutOfRangeException(nameof(look)),
    };

    public static char AsChar(this Look look) => look switch
    {
        Look.Start => 'A',
        Look.End => 'z',
        _ => throw new ArgumentOutOfRangeException(nameof(look)),
    };
}

/// <summary>A set of <see cref="Look"/>s, as a bit set.</summary>
[NonUserCode]
public readonly record struct LookSet(ushort Bits)
{
    private const ushort AllBits = (ushort)(Look.Start | Look.End);

    public static LookSet Empty => new(0);

    public static LookSet Full => new(AllBits);

    public static LookSet Singleton(Look look) => new((ushort)look);

    public bool IsEmpty => Bits == 0;

    public int Count => System.Numerics.BitOperations.PopCount(Bits);

    public bool Contains(Look look) => (Bits & (ushort)look) != 0;

    public LookSet Insert(Look look) => new((ushort)(Bits | (ushort)look));

    public LookSet Remove(Look look) => new((ushort)(Bits & ~(ushort)look));

    public LookSet Union(LookSet other) => new((ushort)(Bits | other.Bits));

    public LookSet Intersect(LookSet other) => new((ushort)(Bits & other.Bits));

    public LookSet Subtract(LookSet other) => new((ushort)(Bits & ~other.Bits));

    public IEnumerable<Look> Iterate()
    {
        foreach (var look in (Look[])[Look.Start, Look.End])
        {
            if (Contains(look)) yield return look;
        }
    }

    public override string ToString() =>
        IsEmpty ? "∅" : string.Concat(Iterate().Select([NonUserCode] (l) => l.AsChar()));
}
