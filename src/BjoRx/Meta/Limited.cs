// Ported from regex-automata/src/meta/limited.rs and meta/stopat.rs
// (rust-lang/regex). MIT OR Apache-2.0; see THIRD-PARTY.md.

using BjoRx.Hybrid;
using BjoRx.Search;

namespace BjoRx.Meta;

internal enum RetryKind : byte
{
    NoMatch,
    Match,
    /// <summary>The search would scan what an earlier one did; use another strategy.</summary>
    Quadratic,
    /// <summary>The lazy DFA gave up; use the PikeVM.</summary>
    GaveUp,
}

/// <summary>
/// The outcome of a search in a reverse strategy: an offset with a match,
/// or with no match from a forward search, the offset where it stopped.
/// </summary>
[NonUserCode]
internal readonly record struct Retry(RetryKind Kind, int Offset)
{
    public static Retry NoMatch(int stoppedAt = -1) => new(RetryKind.NoMatch, stoppedAt);
    public static Retry Found(int offset) => new(RetryKind.Match, offset);
    public static readonly Retry Quadratic = new(RetryKind.Quadratic, -1);
    public static readonly Retry GaveUp = new(RetryKind.GaveUp, -1);
}

/// <summary>
/// Two DFA searches that the reverse strategies need to stay linear: a
/// reverse search that will not go below a minimum start, and a forward
/// search that says where it stopped when it finds nothing. Without them,
/// each literal candidate could rescan the same stretch of haystack.
/// </summary>
[NonUserCode]
internal static class Limited
{
    /// <summary>
    /// A reverse search from the span's end, anchored there, that reports
    /// <see cref="Retry.Quadratic"/> rather than scan below
    /// <paramref name="minStart"/>, the end of the previous candidate.
    /// </summary>
    public static Retry SearchRev(DFA dfa, DFA.Cache cache, in Input input, int minStart)
    {
        var haystack = input.Haystack.Span;
        int mat = -1;
        if (!dfa.TryStartStateReverse(cache, input, out var sid)) return Retry.GaveUp;
        int at;
        if (input.Start == input.End)
        {
            at = input.Start;
        }
        else
        {
            at = input.End - 1;
            while (true)
            {
                if (!dfa.TryNextState(cache, sid, haystack[at], out var next)) return Retry.GaveUp;
                sid = next;
                if (sid.IsTagged)
                {
                    if (sid.IsMatch) mat = at + 1;
                    else if (sid.IsDead) return mat >= 0 ? Retry.Found(mat) : Retry.NoMatch();
                }
                if (at == input.Start) break;
                at--;
                if (at < minStart) return Retry.Quadratic;
            }
        }
        bool wasDead = sid.IsDead;
        int start = input.Start;
        LazyStateId eoi;
        if (start > 0 ? !dfa.TryNextState(cache, sid, haystack[start - 1], out eoi) : !dfa.TryNextEoiState(cache, sid, out eoi))
        {
            return Retry.GaveUp;
        }
        if (eoi.IsMatch) mat = start;
        // Reaching the span's start still alive, a longer match could start
        // before it, where the caller has not looked: not a safe answer.
        if (input.Start != input.End && at == input.Start && mat > input.Start && !wasDead) return Retry.Quadratic;
        return mat >= 0 ? Retry.Found(mat) : Retry.NoMatch();
    }

    /// <summary>
    /// A forward search, anchored at the span's start in practice, that on
    /// no match reports where it stopped, so the caller can tell when a
    /// later candidate would rescan.
    /// </summary>
    public static Retry SearchFwdStopAt(DFA dfa, DFA.Cache cache, in Input input)
    {
        var haystack = input.Haystack.Span;
        int mat = -1;
        if (!dfa.TryStartStateForward(cache, input, out var sid)) return Retry.GaveUp;
        int at = input.Start;
        while (at < input.End)
        {
            if (!dfa.TryNextState(cache, sid, haystack[at], out var next)) return Retry.GaveUp;
            sid = next;
            if (sid.IsTagged)
            {
                if (sid.IsMatch)
                {
                    mat = at;
                    if (input.Earliest) return Retry.Found(at);
                }
                else if (sid.IsDead)
                {
                    return mat >= 0 ? Retry.Found(mat) : Retry.NoMatch(at);
                }
            }
            at++;
        }
        int end = input.End;
        LazyStateId eoi;
        if (end < haystack.Length ? !dfa.TryNextState(cache, sid, haystack[end], out eoi) : !dfa.TryNextEoiState(cache, sid, out eoi))
        {
            return Retry.GaveUp;
        }
        if (eoi.IsMatch) mat = end < haystack.Length ? end : haystack.Length;
        return mat >= 0 ? Retry.Found(mat) : Retry.NoMatch(at);
    }
}
