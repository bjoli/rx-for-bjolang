// Ported from regex-automata/src/hybrid/id.rs (rust-lang/regex). MIT OR
// Apache-2.0; see THIRD-PARTY.md.

namespace BjoRx.Hybrid;

/// <summary>
/// A lazy DFA state's id: the offset of its row in the transition table,
/// with tag bits above it for the states a search must stop at.
/// </summary>
///
/// <remarks>
/// <para>
/// The offset is premultiplied by the stride, so following a transition is
/// one add and one load: <c>trans[id + class]</c>. An id with any tag bit
/// set is greater than <see cref="Max"/>, so the search loop tests one
/// comparison per byte and looks at the tags only when it is set.
/// </para>
/// <para>
/// Upstream also has a <i>quit</i> tag, for bytes that make a search fail,
/// which only Unicode word boundaries need; it is not ported.
/// </para>
/// </remarks>
public readonly record struct LazyStateId(uint Raw)
{
    private const uint MaskUnknown = 1u << 31;
    private const uint MaskDead = 1u << 30;
    private const uint MaskStart = 1u << 29;
    private const uint MaskMatch = 1u << 28;

    /// <summary>The largest untagged id.</summary>
    public const int Max = (int)MaskMatch - 1;

    public static LazyStateId New(int offset)
    {
        if ((uint)offset > Max) throw new ArgumentOutOfRangeException(nameof(offset), $"state id {offset} exceeds {Max}");
        return new LazyStateId((uint)offset);
    }

    /// <summary>The row offset, without tags.</summary>
    public int Untagged => (int)(Raw & Max);

    public LazyStateId ToUnknown() => new(Raw | MaskUnknown);
    public LazyStateId ToDead() => new(Raw | MaskDead);
    public LazyStateId ToStart() => new(Raw | MaskStart);
    public LazyStateId ToMatch() => new(Raw | MaskMatch);

    public bool IsTagged => Raw > Max;
    public bool IsUnknown => (Raw & MaskUnknown) != 0;
    public bool IsDead => (Raw & MaskDead) != 0;
    public bool IsStart => (Raw & MaskStart) != 0;
    public bool IsMatch => (Raw & MaskMatch) != 0;

    public override string ToString()
    {
        var tags = (IsUnknown ? "U" : "") + (IsDead ? "D" : "") + (IsStart ? "S" : "") + (IsMatch ? "M" : "");
        return tags.Length == 0 ? Untagged.ToString() : $"{Untagged}{tags}";
    }
}
