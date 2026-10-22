// Ported from regex-automata/src/nfa/thompson/pikevm.rs (rust-lang/regex).
// MIT OR Apache-2.0; see THIRD-PARTY.md.

using BjoRx.Nfa;
using BjoRx.Syntax;

namespace BjoRx.Search;

/// <summary>
/// Runs an NFA directly, keeping every state it could be in at once, so that
/// a search takes time linear in the haystack times the size of the NFA. The
/// slowest engine, but the only one that reports where every group matched
/// over the whole haystack, and the reference the faster engines are checked
/// against.
/// </summary>
///
/// <remarks>
/// <para>
/// Leftmost-first preference comes from order: the states are kept in the
/// order of preference, the epsilon closure visits alternatives in that order,
/// and once a state reaches a match, the states after it — all less preferred
/// — are dropped.
/// </para>
/// <para>
/// A match is reported one byte late: a state set reaches a Match state at
/// the position after the last byte it consumed, and that is only noticed
/// when the set is stepped. So the search runs one position past the span's
/// end.
/// </para>
/// </remarks>
[NonUserCode]
public sealed class PikeVM
{
    public NFA Nfa { get; }

    public PikeVM(NFA nfa)
    {
        if (nfa.IsReverse) throw new ArgumentException("the PikeVM runs forward NFAs only", nameof(nfa));
        if (nfa.GroupInfo.GroupLength == 0)
        {
            throw new ArgumentException("the PikeVM needs at least the implicit group 0", nameof(nfa));
        }
        Nfa = nfa;
    }

    public static PikeVM Create(Hir hir) => new(Compiler.Compile(hir));

    public Cache CreateCache() => new(this);

    /// <summary>
    /// Whether the regex can match the empty string while the NFA follows
    /// UTF-8; then an empty match inside a scalar has to be filtered out.
    /// </summary>
    private bool Utf8Empty => Nfa.HasEmpty && Nfa.IsUtf8;

    public bool IsMatch(Cache cache, Input input)
    {
        input = input with { Earliest = true };
        Span<int> slots = stackalloc int[2];
        return SearchSlots(cache, input, slots);
    }

    public Match? Find(Cache cache, Input input)
    {
        Span<int> slots = stackalloc int[2];
        return SearchSlots(cache, input, slots) ? new Match(slots[0], slots[1]) : null;
    }

    /// <summary>The leftmost-first match and where each group matched in it, or false.</summary>
    public bool Captures(Cache cache, Input input, Captures captures)
    {
        captures.Clear();
        return SearchSlots(cache, input, captures.Slots);
    }

    public IEnumerable<Match> FindAll(Cache cache, Input input)
    {
        var searcher = new Searcher(input);
        while (searcher.Advance([NonUserCode] (i) => Find(cache, i)) is { } m) yield return m;
    }

    public IEnumerable<Captures> CapturesAll(Cache cache, Input input)
    {
        var searcher = new Searcher(input);
        var caps = new Captures(Nfa.GroupInfo.GroupLength);
        while (searcher.Advance([NonUserCode] (i) => Captures(cache, i, caps) ? caps.Overall : null) is not null)
        {
            yield return caps.Clone();
        }
    }

    /// <summary>
    /// Searches and writes the match's slots, as many as <paramref name="slots"/>
    /// has room for, filled with -1 for groups that took no part.
    /// </summary>
    public bool SearchSlots(Cache cache, Input input, Span<int> slots)
    {
        if (slots.Length < 2) throw new ArgumentException("room for at least group 0's two slots is needed", nameof(slots));
        // Slots past the NFA's groups never get written.
        slots.Fill(-1);
        var buffer = cache.SlotBuffer.AsSpan(0, Math.Min(slots.Length, cache.SlotBuffer.Length));
        int? end = SearchImp(cache, input, buffer);
        // An empty match may have landed inside a scalar; look on, one byte
        // further each time. An anchored search cannot move on.
        if (end is not null && Utf8Empty)
        {
            if (input.Anchored == Anchored.Yes)
            {
                if (!input.IsCharBoundary(end.Value)) end = null;
            }
            else
            {
                while (end is { } e && !input.IsCharBoundary(e))
                {
                    input = input with { Start = input.Start + 1 };
                    end = SearchImp(cache, input, buffer);
                }
            }
        }
        if (end is null) return false;
        buffer.CopyTo(slots);
        return true;
    }

    /// <summary>The end of the match, with its slots written to <paramref name="slots"/>.</summary>
    private int? SearchImp(Cache cache, Input input, Span<int> slots)
    {
        cache.SetupSearch(slots.Length);
        slots.Fill(-1);
        if (input.IsDone) return null;

        var haystack = input.Haystack.Span;
        // A search is anchored when asked to be or when the pattern is.
        bool anchored = input.Anchored == Anchored.Yes || Nfa.IsAlwaysStartAnchored;
        int startId = Nfa.StartAnchored;
        var curr = cache.Curr;
        var next = cache.Next;
        int? matchEnd = null;

        // The search includes the span's end: matches are seen one byte late.
        for (int at = input.Start; at <= input.End; at++)
        {
            if (curr.Set.IsEmpty)
            {
                // Nothing left to extend: the match found is final, and an
                // anchored search that has moved past its start cannot begin one.
                if (matchEnd is not null) break;
                if (anchored && at > input.Start) break;
            }

            // The unanchored prefix (?s-u:.)*? is simulated rather than run:
            // the start state is added at every position until there is a
            // match, which is what a lazy prefix does, and only at the first
            // position for an anchored search. Its slots are all absent, since
            // the prefix is outside every group.
            if (matchEnd is null && (!anchored || at == input.Start))
            {
                var absent = next.Slots.AllAbsent();
                EpsilonClosure(cache.Stack, absent, curr, haystack, at, startId);
            }

            if (Nexts(cache.Stack, curr, next, haystack, at, slots))
            {
                matchEnd = at;
            }
            if (input.Earliest && matchEnd is not null) break;

            (curr, next) = (next, curr);
            next.Set.Clear();
        }
        // Leave the cache's two sets where it expects them.
        cache.Curr = curr;
        cache.Next = next;
        return matchEnd;
    }

    /// <summary>
    /// Steps every state in <paramref name="curr"/> over the byte at
    /// <paramref name="at"/> into <paramref name="next"/>. Stops at the first
    /// match state, since everything after it is less preferred, and copies
    /// that thread's slots out.
    /// </summary>
    private bool Nexts(Stack<Frame> stack, ActiveStates curr, ActiveStates next, ReadOnlySpan<byte> haystack, int at, Span<int> slots)
    {
        var set = curr.Set;
        for (int i = 0; i < set.Count; i++)
        {
            int sid = set[i];
            if (!Step(stack, curr.Slots, next, haystack, at, sid)) continue;
            curr.Slots.ForState(sid).CopyTo(slots);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Follows a byte transition out of <paramref name="sid"/>, adding the
    /// target's epsilon closure to <paramref name="next"/>. True if
    /// <paramref name="sid"/> is a match state.
    /// </summary>
    private bool Step(Stack<Frame> stack, SlotTable currSlots, ActiveStates next, ReadOnlySpan<byte> haystack, int at, int sid)
    {
        ref readonly var state = ref Nfa.States[sid];
        switch (state.Kind)
        {
            case StateKind.ByteRange:
                if (at < haystack.Length && state.Trans.Matches(haystack[at]))
                {
                    EpsilonClosure(stack, currSlots.ForState(sid), next, haystack, at + 1, state.Trans.Next);
                }
                return false;
            case StateKind.Sparse:
                if (at < haystack.Length && SparseNext(state.Transitions, haystack[at]) is var target && target >= 0)
                {
                    EpsilonClosure(stack, currSlots.ForState(sid), next, haystack, at + 1, target);
                }
                return false;
            case StateKind.Match:
                return true;
            default:
                return false;
        }
    }

    private static int SparseNext(Transition[] transitions, byte b)
    {
        // Sorted and non-overlapping, so the first range that reaches b decides.
        foreach (var t in transitions)
        {
            if (b < t.Start) return -1;
            if (b <= t.End) return t.Next;
        }
        return -1;
    }

    [NonUserCode]
    internal readonly record struct Frame(bool Restore, int SidOrSlot, int Offset);

    /// <summary>
    /// Adds every state reachable from <paramref name="sid"/> without
    /// consuming input to <paramref name="next"/>, in order of preference,
    /// each with the slots as they are on the way to it.
    /// </summary>
    /// <remarks>
    /// <paramref name="currSlots"/> is the thread's slots, changed in place as
    /// Capture states are passed and restored from the stack on the way back,
    /// so one array serves every path.
    /// </remarks>
    private void EpsilonClosure(Stack<Frame> stack, Span<int> currSlots, ActiveStates next, ReadOnlySpan<byte> haystack, int at, int sid)
    {
        stack.Push(new Frame(false, sid, 0));
        while (stack.TryPop(out var frame))
        {
            if (frame.Restore)
            {
                currSlots[frame.SidOrSlot] = frame.Offset;
                continue;
            }
            Explore(stack, currSlots, next, haystack, at, frame.SidOrSlot);
        }
    }

    private void Explore(Stack<Frame> stack, Span<int> currSlots, ActiveStates next, ReadOnlySpan<byte> haystack, int at, int sid)
    {
        // Following the first successor in a loop rather than pushing and
        // popping it saves most of the stack traffic.
        while (true)
        {
            if (!next.Set.Insert(sid)) return;
            ref readonly var state = ref Nfa.States[sid];
            switch (state.Kind)
            {
                case StateKind.Fail or StateKind.Match or StateKind.ByteRange or StateKind.Sparse:
                    currSlots.CopyTo(next.Slots.ForState(sid));
                    return;
                case StateKind.Look:
                    if (!LookMatches(state.Look, haystack, at)) return;
                    sid = state.Next;
                    break;
                case StateKind.Union:
                    var alts = state.Alternates;
                    for (int i = alts.Length - 1; i >= 1; i--) stack.Push(new Frame(false, alts[i], 0));
                    sid = alts[0];
                    break;
                case StateKind.BinaryUnion:
                    stack.Push(new Frame(false, state.Alt2, 0));
                    sid = state.Alt1;
                    break;
                case StateKind.Capture:
                    // Slots the caller did not ask for are not tracked.
                    if (state.Slot < currSlots.Length)
                    {
                        stack.Push(new Frame(true, state.Slot, currSlots[state.Slot]));
                        currSlots[state.Slot] = at;
                    }
                    sid = state.Next;
                    break;
            }
        }
    }

    internal static bool LookMatches(Look look, ReadOnlySpan<byte> haystack, int at) => look switch
    {
        Look.Start => at == 0,
        Look.End => at == haystack.Length,
        _ => throw new NotSupportedException(look.ToString()),
    };

    /// <summary>The state set and per-state slots for one step of the search.</summary>
    [NonUserCode]
    internal sealed class ActiveStates(int states, int slotsPerState)
    {
        public readonly SparseSet Set = new(states);
        public readonly SlotTable Slots = new(states, slotsPerState);
    }

    /// <summary>
    /// The slots of every active state, in one array: a row per NFA state,
    /// plus a row at the end that is always absent, for the start of a search.
    /// </summary>
    [NonUserCode]
    internal sealed class SlotTable
    {
        private readonly int[] _table;
        private readonly int _slotsPerState;
        private int _slotsForCaptures;

        public SlotTable(int states, int slotsPerState)
        {
            _slotsPerState = slotsPerState;
            _slotsForCaptures = slotsPerState;
            _table = new int[states * slotsPerState + slotsPerState];
            Array.Fill(_table, -1);
        }

        /// <summary>Only as many slots as the caller asked for are carried.</summary>
        public void SetupSearch(int slotsForCaptures) =>
            _slotsForCaptures = Math.Min(slotsForCaptures, _slotsPerState);

        public Span<int> ForState(int sid) => _table.AsSpan(sid * _slotsPerState, _slotsForCaptures);

        public Span<int> AllAbsent()
        {
            var row = _table.AsSpan(_table.Length - _slotsPerState, _slotsForCaptures);
            row.Fill(-1);
            return row;
        }
    }

    /// <summary>
    /// The PikeVM's working memory. One per thread: a search uses it all, and
    /// keeps it for the next search so that searching does not allocate.
    /// </summary>
    [NonUserCode]
    public sealed class Cache
    {
        internal readonly Stack<Frame> Stack = new();
        internal ActiveStates Curr;
        internal ActiveStates Next;
        // Where a search writes its slots, so that a failed retry after an
        // empty match inside a scalar does not clobber the caller's.
        internal readonly int[] SlotBuffer;

        internal Cache(PikeVM vm)
        {
            int states = vm.Nfa.States.Length;
            int slots = vm.Nfa.GroupInfo.SlotLength;
            Curr = new ActiveStates(states, slots);
            Next = new ActiveStates(states, slots);
            SlotBuffer = new int[slots];
        }

        internal void SetupSearch(int slotLength)
        {
            Stack.Clear();
            Curr.Set.Clear();
            Next.Set.Clear();
            Curr.Slots.SetupSearch(slotLength);
            Next.Slots.SetupSearch(slotLength);
        }
    }
}
