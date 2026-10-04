// Ported from regex-automata/src/hybrid/dfa.rs (rust-lang/regex). MIT OR
// Apache-2.0; see THIRD-PARTY.md.

using BjoRx.Nfa;
using BjoRx.Search;

namespace BjoRx.Hybrid;

public sealed record DfaConfig
{
    public MatchKind MatchKind { get; init; } = MatchKind.LeftmostFirst;

    /// <summary>Index transitions by byte class rather than by byte. Off only to test that it makes no difference.</summary>
    public bool ByteClasses { get; init; } = true;

    /// <summary>
    /// Tag start states, so that a search notices when it is back at the
    /// start and can let a prefilter skip ahead. Costs a little speed otherwise.
    /// </summary>
    public bool SpecializeStartStates { get; init; }

    /// <summary>The memory, in approximate bytes, that a cache may use before it is cleared.</summary>
    public long CacheCapacity { get; init; } = 2 * (1 << 20);

    /// <summary>Raise a capacity below the minimum to the minimum, rather than failing to build.</summary>
    public bool SkipCacheCapacityCheck { get; init; }

    /// <summary>
    /// After this many clears, a search gives up when it is not using the
    /// cache well (see <see cref="MinimumBytesPerState"/>), and the caller
    /// falls back to a slower engine. Null never gives up.
    /// </summary>
    public int? MinimumCacheClearCount { get; init; }

    /// <summary>
    /// The cache is used well when the bytes searched since the last clear
    /// are at least this many per state. Null gives up on reaching
    /// <see cref="MinimumCacheClearCount"/> at all.
    /// </summary>
    public int? MinimumBytesPerState { get; init; }
}

public enum DfaStatus : byte
{
    NoMatch,
    Match,
    /// <summary>The cache was cleared too often; the search must be redone with another engine.</summary>
    GaveUp,
}

/// <summary>
/// The outcome of a DFA search: one end of a match (the end for a forward
/// search, the start for a reverse one), no match, or where it gave up.
/// </summary>
public readonly record struct DfaResult(DfaStatus Status, int Offset)
{
    public static DfaResult NoMatch => default;

    public static DfaResult Found(int offset) => new(DfaStatus.Match, offset);

    public static DfaResult GaveUp(int offset) => new(DfaStatus.GaveUp, offset);

    public bool IsMatch => Status == DfaStatus.Match;

    public bool IsGaveUp => Status == DfaStatus.GaveUp;

    public override string ToString() => Status switch
    {
        DfaStatus.Match => $"match at {Offset}",
        DfaStatus.GaveUp => $"gave up at {Offset}",
        _ => "no match",
    };
}

/// <summary>
/// A lazy DFA, or hybrid NFA/DFA: a DFA whose states are built from the NFA
/// as a search first needs them, and kept in a cache of bounded size.
/// </summary>
///
/// <remarks>
/// <para>
/// A search is a table lookup per byte while it stays among states it has
/// seen, and does subset construction otherwise. When the cache is full it
/// is cleared and filling starts over; a search that keeps clearing it can
/// give up, so that a slower engine with a better worst case takes over.
/// </para>
/// <para>
/// A DFA has no capture groups and finds only one end of a match: a forward
/// search finds where the leftmost-first match ends, and a reverse search on
/// the reversed NFA, anchored there, finds where it starts.
/// </para>
/// </remarks>
public sealed partial class DFA
{
    // Two sentinels, the unknown state at 0 and the dead state after it,
    // plus room for two more states, so that a cleared cache can hold the
    // state being stepped from and the one being added.
    private const int SentinelStates = 2;
    private const int MinStates = SentinelStates + 2;

    // Upstream's accounting: a transition is a 4-byte id, and a state takes
    // a 16-byte reference besides its own bytes.
    private const int IdSize = 4;
    private const int StateSize = 16;

    public NFA Nfa { get; }
    public DfaConfig Config { get; }
    public ByteClasses Classes { get; }
    private readonly int _stride2;
    private readonly long _cacheCapacity;

    private DFA(NFA nfa, DfaConfig config, ByteClasses classes, long cacheCapacity)
    {
        Nfa = nfa;
        Config = config;
        Classes = classes;
        _stride2 = classes.Stride2;
        _cacheCapacity = cacheCapacity;
    }

    /// <summary>Builds a lazy DFA for <paramref name="nfa"/>; a reverse NFA gives a reverse DFA.</summary>
    public static DFA Create(NFA nfa, DfaConfig? config = null)
    {
        config ??= new DfaConfig();
        var classes = config.ByteClasses ? nfa.ByteClasses : BjoRx.Nfa.ByteClasses.Singletons();
        long minimum = MinimumCacheCapacity(nfa, classes);
        long capacity = config.CacheCapacity;
        if (capacity < minimum)
        {
            if (!config.SkipCacheCapacityCheck)
            {
                throw new BuildException($"lazy DFA cache capacity {capacity} is below the minimum {minimum}");
            }
            capacity = minimum;
        }
        return new DFA(nfa, config, classes, capacity);
    }

    public Cache CreateCache() => new(this);

    /// <summary>Empties a cache and forgets its history, as if it were new.</summary>
    public void ResetCache(Cache cache)
    {
        cache.Saver = default;
        ClearCache(cache);
        cache.Sparses.Resize(Nfa.States.Length);
        cache.ClearCount = 0;
        cache.Progress = null;
    }

    private int Stride => 1 << _stride2;

    internal LazyStateId UnknownId => new LazyStateId(0).ToUnknown();

    internal LazyStateId DeadId => LazyStateId.New(1 << _stride2).ToDead();

    /// <summary>
    /// The state <paramref name="current"/> goes to on <paramref name="b"/>,
    /// computing it if this cache has not yet. False if the search has to give up.
    /// </summary>
    public bool TryNextState(Cache cache, LazyStateId current, byte b, out LazyStateId next)
    {
        next = cache.Trans[current.Untagged + Classes.Get(b)];
        return !next.IsUnknown || CacheNextState(cache, current, b, out next);
    }

    /// <summary>The state <paramref name="current"/> goes to at the end of input.</summary>
    public bool TryNextEoiState(Cache cache, LazyStateId current, out LazyStateId next)
    {
        next = cache.Trans[current.Untagged + Classes.Eoi];
        return !next.IsUnknown || CacheNextState(cache, current, Determinize.Eoi, out next);
    }

    /// <summary>The start state for a search that is or is not anchored, after <paramref name="start"/>.</summary>
    public bool TryStartState(Cache cache, Anchored anchored, Start start, out LazyStateId id)
    {
        id = cache.Starts[StartIndex(anchored, start)];
        return !id.IsUnknown || CacheStartGroup(cache, anchored, start, out id);
    }

    private static int StartIndex(Anchored anchored, Start start) => (anchored == Anchored.Yes ? 2 : 0) + (int)start;

    /// <summary>A forward search starts after the byte before its span.</summary>
    internal bool TryStartStateForward(Cache cache, in Input input, out LazyStateId id) =>
        TryStartState(cache, input.Anchored, input.Start == 0 ? Start.Text : Start.Byte, out id);

    /// <summary>A reverse search starts "after" the byte at its span's end.</summary>
    internal bool TryStartStateReverse(Cache cache, in Input input, out LazyStateId id) =>
        TryStartState(cache, input.Anchored, input.End == input.Haystack.Length ? Start.Text : Start.Byte, out id);

    private byte[] GetCachedState(Cache cache, LazyStateId id) => cache.States[id.Untagged >> _stride2];

    // Determinization: upstream's `Lazy`.

    private bool CacheNextState(Cache cache, LazyStateId current, int unit, out LazyStateId next)
    {
        var builder = cache.Builder;
        builder.Clear();
        Determinize.Next(Nfa, Config.MatchKind, cache.Sparses, cache.Stack, GetCachedState(cache, current), unit, builder);
        // If adding the next state clears the cache, the current state goes
        // with it; it is saved and re-added so that its transition can be set.
        bool saveState = !FitsInCache(cache, builder.AsSpan().Length) || cache.TransLength >= LazyStateId.Max;
        if (saveState)
        {
            cache.Saver = new StateSaver(StateSaver.Kind.ToSave, current, GetCachedState(cache, current));
        }
        else if (cache.Saver.Which == StateSaver.Kind.ToSave)
        {
            // Left over from a search that gave up while saving.
            cache.Saver = default;
        }
        if (!AddBuilderState(cache, builder, toStart: false, out next)) return false;
        if (saveState)
        {
            current = cache.Saver.Id;
            cache.Saver = default;
        }
        SetTransition(cache, current, unit, next);
        return true;
    }

    private bool CacheStartGroup(Cache cache, Anchored anchored, Start start, out LazyStateId id)
    {
        int nfaStart = anchored == Anchored.Yes ? Nfa.StartAnchored : Nfa.StartUnanchored;
        var builder = cache.Builder;
        builder.StartMatches();
        Determinize.SetLookBehindFromStart(Nfa, start, builder);
        cache.Sparses.Set1.Clear();
        Determinize.EpsilonClosure(Nfa, nfaStart, builder.LookHave, cache.Stack, cache.Sparses.Set1);
        builder.StartNfa();
        Determinize.AddNfaStates(Nfa, cache.Sparses.Set1, builder);
        if (!AddBuilderState(cache, builder, Config.SpecializeStartStates, out id)) return false;
        cache.Starts[StartIndex(anchored, start)] = id;
        return true;
    }

    private bool AddBuilderState(Cache cache, StateBuilder builder, bool toStart, out LazyStateId id)
    {
        if (cache.Lookup.TryGetValue(builder.AsSpan(), out id)) return true;
        return AddState(cache, builder.ToState(), toStart, out id);
    }

    private bool AddState(Cache cache, byte[] state, bool toStart, out LazyStateId id)
    {
        if (!FitsInCache(cache, state.Length) && !TryClearCache(cache))
        {
            id = default;
            return false;
        }
        if (cache.TransLength > LazyStateId.Max && !TryClearCache(cache))
        {
            id = default;
            return false;
        }
        id = LazyStateId.New(cache.TransLength);
        if (toStart) id = id.ToStart();
        if (DfaState.IsMatch(state)) id = id.ToMatch();
        PushState(cache, state, id);
        return true;
    }

    /// <summary>Appends a state and an unknown row for it, without checking the capacity.</summary>
    private void PushState(Cache cache, byte[] state, LazyStateId id)
    {
        int stride = Stride;
        if (cache.TransLength + stride > cache.Trans.Length)
        {
            Array.Resize(ref cache.Trans, Math.Max(cache.Trans.Length * 2, cache.TransLength + stride));
        }
        cache.Trans.AsSpan(cache.TransLength, stride).Fill(UnknownId);
        cache.TransLength += stride;
        cache.MemoryUsageState += state.Length;
        cache.States.Add(state);
        cache.StatesToId[state] = id;
    }

    /// <summary>
    /// Clears the cache, or gives up if it has been cleared often enough and
    /// too few bytes per state were searched since the last clear.
    /// </summary>
    private bool TryClearCache(Cache cache)
    {
        if (Config.MinimumCacheClearCount is { } minCount && cache.ClearCount >= minCount)
        {
            if (Config.MinimumBytesPerState is not { } minBytesPer) return false;
            long minBytes = (long)minBytesPer * cache.States.Count;
            if (cache.SearchTotalLength < minBytes) return false;
        }
        ClearCache(cache);
        return true;
    }

    private void ClearCache(Cache cache)
    {
        cache.TransLength = 0;
        cache.States.Clear();
        cache.StatesToId.Clear();
        cache.MemoryUsageState = 0;
        cache.ClearCount++;
        cache.BytesSearched = 0;
        if (cache.Progress is { } p) cache.Progress = p with { Start = p.At };
        InitCache(cache);
        if (cache.Saver is { Which: StateSaver.Kind.ToSave, State: { } saved })
        {
            // The minimum capacity leaves room for it in an empty cache.
            var newId = LazyStateId.New(cache.TransLength);
            if (cache.Saver.Id.IsStart) newId = newId.ToStart();
            if (DfaState.IsMatch(saved)) newId = newId.ToMatch();
            PushState(cache, saved, newId);
            cache.Saver = new StateSaver(StateSaver.Kind.Saved, newId, null);
        }
    }

    internal void InitCache(Cache cache)
    {
        cache.Starts.AsSpan().Fill(UnknownId);
        var dead = DfaState.Dead();
        PushState(cache, dead, UnknownId);
        PushState(cache, dead, DeadId);
        SetAllTransitions(cache, UnknownId, UnknownId);
        SetAllTransitions(cache, DeadId, DeadId);
        cache.StatesToId[dead] = DeadId;
    }

    private void SetAllTransitions(Cache cache, LazyStateId from, LazyStateId to) =>
        cache.Trans.AsSpan(from.Untagged, Classes.AlphabetLength).Fill(to);

    private void SetTransition(Cache cache, LazyStateId from, int unit, LazyStateId to) =>
        cache.Trans[from.Untagged + Classes.GetByUnit(unit)] = to;

    private bool FitsInCache(Cache cache, int stateLength) =>
        cache.MemoryUsage + MemoryForOneMoreState(stateLength) <= _cacheCapacity;

    private long MemoryForOneMoreState(int stateLength) =>
        (long)Stride * IdSize + StateSize + (StateSize + IdSize) + stateLength;

    /// <summary>
    /// The smallest capacity that holds the sentinels and two states of the
    /// largest possible size, with the cache's fixed overhead.
    /// </summary>
    private static long MinimumCacheCapacity(NFA nfa, ByteClasses classes)
    {
        long stride = 1L << classes.Stride2;
        long statesLength = nfa.States.Length;
        long sparses = 4 * statesLength * IdSize;
        long trans = MinStates * stride * IdSize;
        long starts = 4 * IdSize;
        long deadStateSize = DfaState.HeaderLength;
        // Each NFA state id takes at most 5 bytes as a varint.
        long maxStateSize = DfaState.HeaderLength + statesLength * 5;
        long states = SentinelStates * (StateSize + deadStateSize) + (MinStates - SentinelStates) * (StateSize + maxStateSize);
        long statesToId = MinStates * (StateSize + IdSize);
        long stack = statesLength * IdSize;
        return trans + starts + states + statesToId + sparses + stack + maxStateSize;
    }

    /// <summary>
    /// A state kept across a cache clear, because a transition from it is
    /// being added: its old id and bytes until the clear, its new id after.
    /// </summary>
    internal readonly record struct StateSaver(StateSaver.Kind Which, LazyStateId Id, byte[]? State)
    {
        public enum Kind : byte { None, ToSave, Saved }
    }

    /// <summary>Where the search in progress started, and how far it has got, for the give-up heuristic.</summary>
    internal readonly record struct SearchProgress(int Start, int At)
    {
        public int Length => Math.Abs(At - Start);
    }

    /// <summary>
    /// A lazy DFA's states and transitions, built up across searches. One per
    /// thread; a search mutates it.
    /// </summary>
    public sealed class Cache
    {
        internal LazyStateId[] Trans = [];
        internal int TransLength;
        internal readonly LazyStateId[] Starts = new LazyStateId[4];
        internal readonly List<byte[]> States = [];
        internal readonly Dictionary<byte[], LazyStateId> StatesToId = new(DfaStateComparer.Instance);
        internal readonly Dictionary<byte[], LazyStateId>.AlternateLookup<ReadOnlySpan<byte>> Lookup;
        internal readonly SparseSets Sparses;
        internal readonly Stack<int> Stack = new();
        internal readonly StateBuilder Builder = new();
        internal StateSaver Saver;
        internal long MemoryUsageState;
        internal long BytesSearched;
        internal SearchProgress? Progress;

        /// <summary>How many times the cache was cleared since it was made or reset.</summary>
        public int ClearCount { get; internal set; }

        internal Cache(DFA dfa)
        {
            Lookup = StatesToId.GetAlternateLookup<ReadOnlySpan<byte>>();
            Sparses = new SparseSets(dfa.Nfa.States.Length);
            dfa.InitCache(this);
        }

        internal void SearchStart(int at)
        {
            if (Progress is { } p) BytesSearched += p.Length;
            Progress = new SearchProgress(at, at);
        }

        internal void SearchUpdate(int at) => Progress = Progress!.Value with { At = at };

        internal void SearchFinish(int at)
        {
            BytesSearched += Math.Abs(at - Progress!.Value.Start);
            Progress = null;
        }

        internal long SearchTotalLength => BytesSearched + (Progress?.Length ?? 0);

        /// <summary>The memory the cache uses, by upstream's accounting.</summary>
        public long MemoryUsage =>
            (long)TransLength * IdSize
            + Starts.Length * IdSize
            + (long)States.Count * StateSize
            + (long)StatesToId.Count * (StateSize + IdSize)
            + 4L * Sparses.Set1.Capacity * IdSize
            + (long)Stack.EnsureCapacity(0) * IdSize
            + Builder.Capacity
            + MemoryUsageState;
    }
}
