// Ported from regex-automata/src/util/determinize/mod.rs (rust-lang/regex).
// MIT OR Apache-2.0; see THIRD-PARTY.md.

using BjoRx.Nfa;
using BjoRx.Search;
using BjoRx.Syntax;

namespace BjoRx.Hybrid;

/// <summary>
/// What comes before a search's start, which decides its start state.
/// Upstream tells six cases apart for line anchors and word boundaries; with
/// only <c>\A</c>/<c>\z</c> the one that matters is whether there is anything.
/// </summary>
public enum Start : byte
{
    /// <summary>Some byte: the search starts inside the haystack.</summary>
    Byte,
    /// <summary>Nothing: the search starts at the edge of the haystack.</summary>
    Text,
}

/// <summary>
/// The parts of subset construction that turn NFA states into DFA states:
/// the epsilon closure, and the transition from one DFA state on one unit.
/// </summary>
///
/// <remarks>
/// A unit is a byte (0–255) or <see cref="Eoi"/>, the end of input. A
/// match is delayed by one unit: the state reached <i>from</i> a set holding
/// the NFA's Match state is the matching one. That gives the search one byte
/// of look-ahead to settle <c>\z</c>, which is also why every search ends by
/// following the end-of-input transition.
/// </remarks>
internal static class Determinize
{
    public const int Eoi = 256;

    /// <summary>
    /// The state that <paramref name="state"/> goes to on <paramref name="unit"/>,
    /// built in <paramref name="builder"/>.
    /// </summary>
    public static void Next(
        NFA nfa, MatchKind matchKind, SparseSets sparses, Stack<int> stack,
        byte[] state, int unit, StateBuilder builder)
    {
        sparses.Clear();
        foreach (int id in DfaState.NfaStateIds(state)) sparses.Set1.Insert(id);

        // An assertion that waited for look-ahead may hold now, and then the
        // closure has to be recomputed with it. Only \z is such an assertion,
        // and it holds exactly at the end of input.
        var lookNeed = DfaState.LookNeed(state);
        if (!lookNeed.IsEmpty)
        {
            var lookHave = DfaState.LookHave(state);
            if (unit == Eoi) lookHave = lookHave.Insert(Look.End);
            // States omit unconditional epsilon transitions, so redoing the
            // closure without a new assertion could change it.
            if (!lookHave.Subtract(DfaState.LookHave(state)).Intersect(lookNeed).IsEmpty)
            {
                for (int i = 0; i < sparses.Set1.Count; i++)
                {
                    EpsilonClosure(nfa, sparses.Set1[i], lookHave, stack, sparses.Set2);
                }
                sparses.Swap();
                sparses.Set2.Clear();
            }
        }

        // Upstream sets the look-behind assertions after this unit here (line
        // starts, word boundaries); \A never holds after one.
        builder.StartMatches();
        var set1 = sparses.Set1;
        for (int i = 0; i < set1.Count; i++)
        {
            int id = set1[i];
            ref readonly var s = ref nfa.States[id];
            switch (s.Kind)
            {
                case StateKind.Match:
                    // The new state matches because the old one held a Match:
                    // the delay by one unit. Under leftmost-first, the states
                    // after it are less preferred than this match and dropped.
                    builder.SetIsMatch();
                    if (matchKind != MatchKind.All) goto done;
                    break;
                case StateKind.ByteRange:
                    if (unit != Eoi && s.Trans.Matches((byte)unit))
                    {
                        EpsilonClosure(nfa, s.Trans.Next, builder.LookHave, stack, sparses.Set2);
                    }
                    break;
                case StateKind.Sparse:
                    if (unit != Eoi && SparseNext(s.Transitions, (byte)unit) is var next && next >= 0)
                    {
                        EpsilonClosure(nfa, next, builder.LookHave, stack, sparses.Set2);
                    }
                    break;
            }
        }
        done:
        builder.StartNfa();
        AddNfaStates(nfa, sparses.Set2, builder);
    }

    private static int SparseNext(Transition[] transitions, byte b)
    {
        foreach (var t in transitions)
        {
            if (b < t.Start) return -1;
            if (b <= t.End) return t.Next;
        }
        return -1;
    }

    /// <summary>
    /// Adds the states reachable from <paramref name="start"/> without
    /// consuming input to <paramref name="set"/>, in order of preference,
    /// passing only the assertions in <paramref name="lookHave"/>.
    /// </summary>
    public static void EpsilonClosure(NFA nfa, int start, LookSet lookHave, Stack<int> stack, SparseSet set)
    {
        if (!nfa.States[start].IsEpsilon)
        {
            set.Insert(start);
            return;
        }
        stack.Push(start);
        while (stack.TryPop(out int id))
        {
            // Following the first successor in a loop rather than pushing
            // and popping it saves most of the stack traffic.
            while (set.Insert(id))
            {
                ref readonly var s = ref nfa.States[id];
                if (s.Kind == StateKind.Look)
                {
                    if (!lookHave.Contains(s.Look)) break;
                    id = s.Next;
                }
                else if (s.Kind == StateKind.Union)
                {
                    var alts = s.Alternates;
                    if (alts.Length == 0) break;
                    // The earliest alternative is popped first.
                    for (int i = alts.Length - 1; i >= 1; i--) stack.Push(alts[i]);
                    id = alts[0];
                }
                else if (s.Kind == StateKind.BinaryUnion)
                {
                    stack.Push(s.Alt2);
                    id = s.Alt1;
                }
                else if (s.Kind == StateKind.Capture)
                {
                    id = s.Next;
                }
                else
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Records the states of a closure that tell DFA states apart: those that
    /// consume input, assertions, matches, and also unions.
    /// </summary>
    /// <remarks>
    /// Unions look redundant, but recomputing a closure from the states after
    /// a union rather than from the union itself can reorder them when an
    /// assertion sits in a loop, and the order is the match preference
    /// (upstream's <c>(?:\b|%)+</c> on <c>z%</c>).
    /// </remarks>
    public static void AddNfaStates(NFA nfa, SparseSet set, StateBuilder builder)
    {
        for (int i = 0; i < set.Count; i++)
        {
            int id = set[i];
            ref readonly var s = ref nfa.States[id];
            switch (s.Kind)
            {
                case StateKind.Capture:
                    break;
                case StateKind.Look:
                    builder.AddNfaStateId(id);
                    builder.LookNeed = builder.LookNeed.Insert(s.Look);
                    break;
                default:
                    builder.AddNfaStateId(id);
                    break;
            }
        }
        // Without assertions to wait on, the ones that held do not tell the
        // state apart from others.
        if (builder.LookNeed.IsEmpty) builder.LookHave = LookSet.Empty;
    }

    /// <summary>The assertions that hold at a search's start, given what comes before it.</summary>
    public static void SetLookBehindFromStart(NFA nfa, Start start, StateBuilder builder)
    {
        // A reverse NFA has \A and \z swapped, so its Start is the haystack's
        // end; either way it holds when nothing comes before the search.
        if (start == Start.Text && !nfa.LookSetAny.IsEmpty)
        {
            builder.LookHave = builder.LookHave.Insert(Look.Start);
        }
    }
}
