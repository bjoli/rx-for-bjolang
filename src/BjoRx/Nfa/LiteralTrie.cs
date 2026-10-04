// Ported from regex-automata/src/nfa/thompson/literal_trie.rs
// (rust-lang/regex). MIT OR Apache-2.0; see THIRD-PARTY.md.

namespace BjoRx.Nfa;

/// <summary>
/// Compiles an alternation of literals as a trie, so that <c>foo|foobar|fox</c>
/// shares its prefixes instead of being three chains under one union.
/// </summary>
///
/// <remarks>
/// <para>
/// The trie has to keep leftmost-first preference. A literal that ends inside
/// another one — <c>z</c> in <c>zapper|z|zap</c> — puts a match point in the
/// middle of a state's transitions: the transitions added before it are
/// preferred to stopping there, and those added after it are not. So each
/// state's transitions are split into <i>chunks</i> at its match points, and
/// compile as a union of: the first chunk, a match, the next chunk, and so on.
/// </para>
/// <para>
/// A reverse trie adds each literal backwards.
/// </para>
/// </remarks>
internal sealed class LiteralTrie
{
    private readonly record struct Trans(byte Byte, int Next);

    private sealed class TState
    {
        public readonly List<Trans> Transitions = [];
        // Each chunk is a range of Transitions that ends at a match point.
        public readonly List<(int Start, int End)> Chunks = [];

        public void AddMatch()
        {
            // A second match at a state with no transitions since the last one
            // changes nothing, and a duplicate empty chunk would.
            if (Transitions.Count == 0 && Chunks.Count != 0) return;
            Chunks.Add((ActiveChunkStart, Transitions.Count));
        }

        public bool IsLeaf => Transitions.Count == 0;

        public int ActiveChunkStart => Chunks.Count == 0 ? 0 : Chunks[^1].End;

        /// <summary>The finished chunks, then the active one after the last match point.</summary>
        public IEnumerable<(int Start, int End)> AllChunks()
        {
            foreach (var c in Chunks) yield return c;
            yield return (ActiveChunkStart, Transitions.Count);
        }
    }

    private readonly List<TState> _states = [new TState()];
    private readonly bool _reverse;

    public LiteralTrie(bool reverse)
    {
        _reverse = reverse;
    }

    public void Add(byte[] bytes)
    {
        int prev = 0;
        for (int k = 0; k < bytes.Length; k++)
        {
            prev = GetOrAddState(prev, bytes[_reverse ? bytes.Length - 1 - k : k]);
        }
        _states[prev].AddMatch();
    }

    private int GetOrAddState(int from, byte b)
    {
        var state = _states[from];
        int start = state.ActiveChunkStart;
        // The active chunk is sorted by byte.
        int lo = start, hi = state.Transitions.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            int c = state.Transitions[mid].Byte.CompareTo(b);
            if (c == 0) return state.Transitions[mid].Next;
            if (c < 0) lo = mid + 1;
            else hi = mid;
        }
        int next = _states.Count;
        _states.Add(new TState());
        state.Transitions.Insert(lo, new Trans(b, next));
        return next;
    }

    private sealed class Frame
    {
        public readonly IEnumerator<(int Start, int End)> Chunks;
        public int Position;
        public int ChunkEnd;
        public readonly List<int> Union = [];
        public readonly List<Transition> Sparse = [];

        public Frame(TState state)
        {
            Chunks = state.AllChunks().GetEnumerator();
            Chunks.MoveNext();
            (Position, ChunkEnd) = Chunks.Current;
        }
    }

    /// <summary>
    /// The trie as builder states, depth first with an explicit stack so that
    /// a long literal does not recurse.
    /// </summary>
    public (int Start, int End) Compile(Builder builder)
    {
        int end = builder.AddEmpty();
        var stack = new Stack<(Frame Frame, TState State)>();
        var state = _states[0];
        var f = new Frame(state);
        while (true)
        {
            if (f.Position < f.ChunkEnd)
            {
                var t = state.Transitions[f.Position++];
                if (_states[t.Next].IsLeaf)
                {
                    f.Sparse.Add(new Transition(t.Byte, t.Byte, end));
                }
                else
                {
                    // The real target is filled in when the child is done.
                    f.Sparse.Add(new Transition(t.Byte, t.Byte, 0));
                    stack.Push((f, state));
                    state = _states[t.Next];
                    f = new Frame(state);
                }
                continue;
            }

            if (f.Sparse.Count != 0)
            {
                int chunkId = f.Sparse.Count == 1
                    ? builder.AddRange(f.Sparse[0])
                    : builder.AddSparse(f.Sparse.ToArray());
                f.Sparse.Clear();
                f.Union.Add(chunkId);
            }

            if (f.Chunks.MoveNext())
            {
                // Between two chunks is a match point.
                f.Union.Add(end);
                (f.Position, f.ChunkEnd) = f.Chunks.Current;
                continue;
            }

            int start = builder.AddUnion(f.Union);
            if (!stack.TryPop(out var parent)) return (start, end);
            var sparse = parent.Frame.Sparse;
            sparse[^1] = sparse[^1] with { Next = start };
            f = parent.Frame;
            state = parent.State;
        }
    }
}
