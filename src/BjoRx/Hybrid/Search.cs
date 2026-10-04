// Ported from regex-automata/src/hybrid/search.rs, the search methods of
// hybrid/dfa.rs and util/empty.rs (rust-lang/regex). MIT OR Apache-2.0; see
// THIRD-PARTY.md.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BjoRx.Search;

namespace BjoRx.Hybrid;

public sealed partial class DFA
{
    /// <summary>
    /// Where the leftmost-first match ends, searching forward; with
    /// <see cref="Input.Earliest"/>, where the first match seen ends.
    /// </summary>
    public DfaResult TrySearchFwd(Cache cache, in Input input)
    {
        var result = FindFwd(cache, input);
        if (!result.IsMatch || !_utf8Empty) return result;
        // An empty match may have landed inside a scalar; look on.
        if (input.Anchored == Anchored.Yes)
        {
            return input.IsCharBoundary(result.Offset) ? result : DfaResult.NoMatch;
        }
        var next = input;
        while (!next.IsCharBoundary(result.Offset))
        {
            next = next with { Start = next.Start + 1 };
            result = FindFwd(cache, next);
            if (!result.IsMatch) return result;
        }
        return result;
    }

    /// <summary>
    /// Where a match starts, searching backwards from the span's end with the
    /// reverse DFA. Under <see cref="MatchKind.All"/>, the earliest start.
    /// </summary>
    public DfaResult TrySearchRev(Cache cache, in Input input)
    {
        var result = FindRev(cache, input);
        if (!result.IsMatch || !_utf8Empty) return result;
        if (input.Anchored == Anchored.Yes)
        {
            return input.IsCharBoundary(result.Offset) ? result : DfaResult.NoMatch;
        }
        var next = input;
        while (!next.IsCharBoundary(result.Offset))
        {
            if (next.End == 0) return DfaResult.NoMatch;
            next = next with { End = next.End - 1 };
            result = FindRev(cache, next);
            if (!result.IsMatch) return result;
        }
        return result;
    }


    // The search loops use these rather than the Try methods: passing the
    // current state by reference makes the JIT keep it in memory, which puts
    // a store and a load on every byte's path. They return raw id 0 to give
    // up; no state has it, since offset 0 is the unknown sentinel and tagged.

    private LazyStateId Next(Cache cache, LazyStateId current, byte b) =>
        TryNextState(cache, current, b, out var next) ? next : default;

    private LazyStateId NextEoi(Cache cache, LazyStateId current) =>
        TryNextEoiState(cache, current, out var next) ? next : default;

    private LazyStateId StartForward(Cache cache, in Input input) =>
        TryStartStateForward(cache, input, out var id) ? id : default;

    private LazyStateId StartReverse(Cache cache, in Input input) =>
        TryStartStateReverse(cache, input, out var id) ? id : default;

    private DfaResult FindFwd(Cache cache, in Input input)
    {
        if (input.IsDone) return DfaResult.NoMatch;
        var pre = input.Anchored == Anchored.Yes ? null : _prefilter;
        return input.Earliest ? FindFwdImp(cache, input, pre, earliest: true) : FindFwdImp(cache, input, pre, earliest: false);
    }

    /// <summary>
    /// One state per byte, with the loop unrolled four times; it leaves the
    /// fast path only for a tagged state (unknown, dead, match or start).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private DfaResult FindFwdImp(Cache cache, in Input input, Prefilter? pre, bool earliest)
    {
        bool universalStart = _universalStart;
        int mat = -1;
        var sid = StartForward(cache, input);
        if (sid.Raw == 0) return DfaResult.GaveUp(input.Start);
        var haystack = input.Haystack.Span;
        int end = input.End;
        int at = input.Start;
        ref byte classes = ref MemoryMarshal.GetArrayDataReference(_classTable);
        ref byte hay = ref MemoryMarshal.GetReference(haystack);

        if (pre is not null)
        {
            if (pre.Find(haystack, at, end) is not { } span) return DfaResult.NoMatch;
            at = span.Start;
            if (!universalStart && (sid = StartForward(cache, input with { Start = at })).Raw == 0) return DfaResult.GaveUp(at);
        }
        cache.SearchStart(at);
        while (at < end)
        {
            if (sid.IsTagged)
            {
                cache.SearchUpdate(at);
                if ((sid = Next(cache, sid, haystack[at])).Raw == 0) return DfaResult.GaveUp(at);
            }
            else
            {
                // The table only grows outside this loop, so the reference
                // stays good until the next unknown state.
                ref LazyStateId trans = ref MemoryMarshal.GetArrayDataReference(cache.Trans);
                var prev = sid;
                while (at < end)
                {
                    prev = Unsafe.Add(ref trans, (nuint)(sid.Raw + Unsafe.Add(ref classes, Unsafe.Add(ref hay, at))));
                    if (prev.IsTagged || at + 3 >= end)
                    {
                        (prev, sid) = (sid, prev);
                        break;
                    }
                    at++;
                    sid = Unsafe.Add(ref trans, (nuint)(prev.Raw + Unsafe.Add(ref classes, Unsafe.Add(ref hay, at))));
                    if (sid.IsTagged) break;
                    at++;
                    prev = Unsafe.Add(ref trans, (nuint)(sid.Raw + Unsafe.Add(ref classes, Unsafe.Add(ref hay, at))));
                    if (prev.IsTagged)
                    {
                        (prev, sid) = (sid, prev);
                        break;
                    }
                    at++;
                    sid = Unsafe.Add(ref trans, (nuint)(prev.Raw + Unsafe.Add(ref classes, Unsafe.Add(ref hay, at))));
                    if (sid.IsTagged) break;
                    at++;
                }
                // The state for the byte at `at` is not built yet; `prev` is
                // the state before it.
                if (sid.IsUnknown)
                {
                    cache.SearchUpdate(at);
                    if ((sid = Next(cache, prev, haystack[at])).Raw == 0) return DfaResult.GaveUp(at);
                }
            }
            if (sid.IsTagged)
            {
                if (sid.IsStart)
                {
                    // Back at the start: nothing is under way, so the
                    // prefilter can skip to the next candidate. Not with a
                    // match pending, which the skip would lose.
                    if (pre is not null && mat < 0)
                    {
                        if (pre.Find(haystack, at, end) is not { } span)
                        {
                            cache.SearchFinish(end);
                            return DfaResult.NoMatch;
                        }
                        if (span.Start > at)
                        {
                            at = span.Start;
                            if (!universalStart && (sid = StartForward(cache, input with { Start = at })).Raw == 0)
                            {
                                return DfaResult.GaveUp(at);
                            }
                            continue;
                        }
                    }
                }
                else if (sid.IsMatch)
                {
                    // Matches are delayed by a byte: this state, entered on
                    // the byte at `at`, means a match ended just before it.
                    mat = at;
                    if (earliest)
                    {
                        cache.SearchFinish(at);
                        return DfaResult.Found(mat);
                    }
                }
                else if (sid.IsDead)
                {
                    cache.SearchFinish(at);
                    return mat >= 0 ? DfaResult.Found(mat) : DfaResult.NoMatch;
                }
            }
            at++;
        }

        // One more transition, for a match that ends at the span's end: on
        // the byte after the span if there is one, else on end of input.
        if (end < haystack.Length)
        {
            if ((sid = Next(cache, sid, haystack[end])).Raw == 0) return DfaResult.GaveUp(end);
        }
        else if ((sid = NextEoi(cache, sid)).Raw == 0)
        {
            return DfaResult.GaveUp(haystack.Length);
        }
        if (sid.IsMatch) mat = end;
        cache.SearchFinish(end);
        return mat >= 0 ? DfaResult.Found(mat) : DfaResult.NoMatch;
    }

    private DfaResult FindRev(Cache cache, in Input input)
    {
        if (input.IsDone) return DfaResult.NoMatch;
        return input.Earliest ? FindRevImp(cache, input, earliest: true) : FindRevImp(cache, input, earliest: false);
    }

    /// <summary>The forward loop mirrored: from the span's end down to its start.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private DfaResult FindRevImp(Cache cache, in Input input, bool earliest)
    {
        int mat = -1;
        var sid = StartReverse(cache, input);
        if (sid.Raw == 0) return DfaResult.GaveUp(input.End);
        var haystack = input.Haystack.Span;
        int start = input.Start;
        if (start == input.End) return EoiRev(cache, haystack, start, sid, mat);
        int at = input.End - 1;
        ref byte classes = ref MemoryMarshal.GetArrayDataReference(_classTable);
        ref byte hay = ref MemoryMarshal.GetReference(haystack);

        cache.SearchStart(at);
        while (true)
        {
            if (sid.IsTagged)
            {
                cache.SearchUpdate(at);
                if ((sid = Next(cache, sid, haystack[at])).Raw == 0) return DfaResult.GaveUp(at);
            }
            else
            {
                ref LazyStateId trans = ref MemoryMarshal.GetArrayDataReference(cache.Trans);
                var prev = sid;
                while (at >= start)
                {
                    prev = Unsafe.Add(ref trans, (nuint)(sid.Raw + Unsafe.Add(ref classes, Unsafe.Add(ref hay, at))));
                    if (prev.IsTagged || at <= start + 3)
                    {
                        (prev, sid) = (sid, prev);
                        break;
                    }
                    at--;
                    sid = Unsafe.Add(ref trans, (nuint)(prev.Raw + Unsafe.Add(ref classes, Unsafe.Add(ref hay, at))));
                    if (sid.IsTagged) break;
                    at--;
                    prev = Unsafe.Add(ref trans, (nuint)(sid.Raw + Unsafe.Add(ref classes, Unsafe.Add(ref hay, at))));
                    if (prev.IsTagged)
                    {
                        (prev, sid) = (sid, prev);
                        break;
                    }
                    at--;
                    sid = Unsafe.Add(ref trans, (nuint)(prev.Raw + Unsafe.Add(ref classes, Unsafe.Add(ref hay, at))));
                    if (sid.IsTagged) break;
                    at--;
                }
                if (sid.IsUnknown)
                {
                    cache.SearchUpdate(at);
                    if ((sid = Next(cache, prev, haystack[at])).Raw == 0) return DfaResult.GaveUp(at);
                }
            }
            if (sid.IsTagged)
            {
                if (sid.IsStart)
                {
                }
                else if (sid.IsMatch)
                {
                    mat = at + 1;
                    if (earliest)
                    {
                        cache.SearchFinish(at);
                        return DfaResult.Found(mat);
                    }
                }
                else if (sid.IsDead)
                {
                    cache.SearchFinish(at);
                    return mat >= 0 ? DfaResult.Found(mat) : DfaResult.NoMatch;
                }
            }
            if (at == start) break;
            at--;
        }
        cache.SearchFinish(start);
        return EoiRev(cache, haystack, start, sid, mat);
    }

    /// <summary>The reverse search's last transition: on the byte before the span, or end of input.</summary>
    private DfaResult EoiRev(Cache cache, ReadOnlySpan<byte> haystack, int start, LazyStateId sid, int mat)
    {
        if (start > 0)
        {
            if ((sid = Next(cache, sid, haystack[start - 1])).Raw == 0) return DfaResult.GaveUp(start);
        }
        else if ((sid = NextEoi(cache, sid)).Raw == 0)
        {
            return DfaResult.GaveUp(start);
        }
        if (sid.IsMatch) mat = start;
        return mat >= 0 ? DfaResult.Found(mat) : DfaResult.NoMatch;
    }
}
