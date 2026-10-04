// Ported from regex-automata/src/util/search.rs, util/empty.rs and
// util/iter.rs (rust-lang/regex). MIT OR Apache-2.0; see THIRD-PARTY.md.

namespace BjoRx.Search;

public enum Anchored : byte
{
    /// <summary>A match may start anywhere in the span.</summary>
    No,
    /// <summary>A match must start where the span starts.</summary>
    Yes,
}

/// <summary>Which matches an automaton reports.</summary>
public enum MatchKind : byte
{
    /// <summary>The match a backtracking engine would find first. What every search reports.</summary>
    LeftmostFirst,
    /// <summary>
    /// Every match, with no preference among them. A reverse DFA uses it to
    /// find the earliest start of a match whose end is already known.
    /// </summary>
    All,
}

/// <summary>
/// What to search: a haystack, the span of it to look in, and how. Matching
/// sees the whole haystack, so <c>\A</c> holds only at offset 0 and <c>\z</c>
/// only at the haystack's end, wherever the span is.
/// </summary>
public readonly record struct Input
{
    public ReadOnlyMemory<byte> Haystack { get; }
    public int Start { get; init; }
    public int End { get; init; }
    public Anchored Anchored { get; init; }

    /// <summary>Stop at the first match seen, even if a longer one would be preferred.</summary>
    public bool Earliest { get; init; }

    public Input(ReadOnlyMemory<byte> haystack)
    {
        Haystack = haystack;
        Start = 0;
        End = haystack.Length;
    }

    public Input(byte[] haystack) : this((ReadOnlyMemory<byte>)haystack) { }

    public Input WithSpan(int start, int end)
    {
        if (start < 0 || end > Haystack.Length || start > end + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"invalid span {start}..{end} for haystack of length {Haystack.Length}");
        }
        return this with { Start = start, End = end };
    }

    /// <summary>
    /// True once the span is past its end, which is how an iterator that has
    /// stepped past the last position knows it is finished.
    /// </summary>
    public bool IsDone => Start > End;

    /// <summary>
    /// True if <paramref name="offset"/> does not split a scalar's encoding.
    /// The end of the haystack is a boundary; a continuation byte is not.
    /// </summary>
    public bool IsCharBoundary(int offset) => IsCharBoundary(Haystack.Span, offset);

    public static bool IsCharBoundary(ReadOnlySpan<byte> bytes, int offset)
    {
        if ((uint)offset >= (uint)bytes.Length) return offset == bytes.Length;
        byte b = bytes[offset];
        return b <= 0x7F || b >= 0xC0;
    }
}

/// <summary>A match: <c>Start</c> inclusive, <c>End</c> exclusive.</summary>
public readonly record struct Match(int Start, int End)
{
    public bool IsEmpty => Start == End;

    public int Length => End - Start;

    public override string ToString() => $"{Start}..{End}";
}

/// <summary>
/// The capture slots of one match: for group g, slot 2g is where it started
/// and slot 2g+1 where it ended, or -1 if it took no part.
/// </summary>
public sealed class Captures
{
    public int[] Slots { get; }

    public int GroupLength => Slots.Length / 2;

    public Captures(int groups)
    {
        Slots = new int[2 * groups];
        Array.Fill(Slots, -1);
    }

    public bool IsMatch => Slots.Length >= 2 && Slots[0] >= 0;

    public Match? Get(int group)
    {
        if (group * 2 + 1 >= Slots.Length) return null;
        int s = Slots[2 * group], e = Slots[2 * group + 1];
        return s < 0 || e < 0 ? null : new Match(s, e);
    }

    public Match? Overall => Get(0);

    public void Clear() => Array.Fill(Slots, -1);

    public Captures Clone()
    {
        var c = new Captures(GroupLength);
        Slots.CopyTo(c.Slots, 0);
        return c;
    }

    public override string ToString() =>
        "[" + string.Join(", ", Enumerable.Range(0, GroupLength).Select(g => Get(g)?.ToString() ?? "-")) + "]";
}

/// <summary>
/// Steps through the matches of a regex in a haystack, from left to right
/// and without overlap.
/// </summary>
///
/// <remarks>
/// <para>
/// The next search starts where the last match ended. An empty match there
/// would be found again forever, so when a search finds an empty match at the
/// previous match's end, it is discarded and the search tried again one
/// <i>byte</i> later. An engine never reports an empty match that splits a
/// scalar (it searches on one byte further, as upstream's util/empty.rs
/// does), so that byte step cannot surface one; an anchored search, which
/// cannot move on, then just ends.
/// </para>
/// </remarks>
public sealed class Searcher
{
    private Input _input;
    private int? _lastMatchEnd;

    public Searcher(Input input)
    {
        _input = input;
    }

    public Input Input => _input;

    /// <summary>The next match, using <paramref name="find"/> to search the remaining input.</summary>
    public Match? Advance(Func<Input, Match?> find)
    {
        if (_input.IsDone) return null;
        var m = find(_input);
        if (m is not { } found) return null;
        if (found.IsEmpty && found.End == _lastMatchEnd)
        {
            _input = _input with { Start = _input.Start + 1 };
            if (_input.IsDone) return null;
            m = find(_input);
            if (m is not { } again) return null;
            found = again;
        }
        _input = _input with { Start = found.End };
        _lastMatchEnd = found.End;
        return found;
    }
}
