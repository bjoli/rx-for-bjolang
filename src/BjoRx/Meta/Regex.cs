// Ported from regex-automata/src/meta/regex.rs and util/pool.rs
// (rust-lang/regex). MIT OR Apache-2.0; see THIRD-PARTY.md.

using System.Collections.Concurrent;
using BjoRx.Nfa;
using BjoRx.Search;
using BjoRx.Syntax;
using HybridRegex = BjoRx.Hybrid.Regex;

namespace BjoRx.Meta;

public sealed record RegexConfig
{
    /// <summary>Search for the literals a match starts with to skip ahead.</summary>
    public bool AutoPrefilter { get; init; } = true;

    /// <summary>Use the lazy DFA. Off, the PikeVM does everything.</summary>
    public bool Hybrid { get; init; } = true;

    /// <summary>The lazy DFA cache capacity, for each direction, in approximate bytes.</summary>
    public long HybridCacheCapacity { get; init; } = 2 * (1 << 20);

    /// <summary>A limit on each NFA's size, in approximate bytes, or null for none.</summary>
    public long? NfaSizeLimit { get; init; } = 10 * (1 << 20);
}

/// <summary>What the strategies need to know about the pattern.</summary>
internal sealed class RegexInfo(RegexConfig config, Properties props)
{
    public RegexConfig Config => config;

    public Properties Props => props;

    public bool IsAlwaysAnchoredStart => props.LookSetPrefix.Contains(Look.Start);

    public bool IsAlwaysAnchoredEnd => props.LookSetSuffix.Contains(Look.End);

    /// <summary>
    /// True when the pattern cannot match in <paramref name="input"/>'s span
    /// for a reason that needs no search: anchors that cannot hold, or a span
    /// too short or, anchored at both ends, too long.
    /// </summary>
    public bool IsImpossible(in Input input)
    {
        if (input.Start > 0 && IsAlwaysAnchoredStart) return true;
        if (input.End < input.Haystack.Length && IsAlwaysAnchoredEnd) return true;
        if (props.MinimumLength is not { } minLength) return false;
        int length = Math.Max(0, input.End - input.Start);
        if (length < minLength) return true;
        if ((input.Anchored == Anchored.Yes || IsAlwaysAnchoredStart) && IsAlwaysAnchoredEnd)
        {
            if (props.MaximumLength is { } maxLength && length > maxLength) return true;
        }
        return false;
    }
}

/// <summary>
/// The working memory of a <see cref="Regex"/>'s engines. One per thread at
/// a time; <see cref="Regex"/>'s methods without a cache take one from a pool.
/// </summary>
public sealed class Cache
{
    internal readonly PikeVM.Cache? PikeVM;
    internal readonly HybridRegex.Cache? Hybrid;
    // ReverseInner's reverse DFA for the part before the inner literal.
    internal readonly Hybrid.DFA.Cache? RevInner;

    internal Cache(PikeVM.Cache? pikeVM, HybridRegex.Cache? hybrid, Hybrid.DFA.Cache? revInner = null)
    {
        PikeVM = pikeVM;
        Hybrid = hybrid;
        RevInner = revInner;
    }
}

/// <summary>
/// The caches of one regex. The first thread to use it owns a cache that it
/// gets back without synchronization beyond a compare-and-swap; other
/// threads, and the owner while its cache is out, share a pool.
/// </summary>
internal sealed class CachePool(Func<Cache> create)
{
    // Managed thread ids are positive.
    private const int Unowned = 0;
    private const int InUse = -1;

    private int _owner = Unowned;
    private int _ownerThread;
    private Cache? _ownerCache;
    private readonly ConcurrentBag<Cache> _shared = [];

    public Cache Get(out bool owned)
    {
        int me = Environment.CurrentManagedThreadId;
        int owner = Volatile.Read(ref _owner);
        if (owner == me && Interlocked.CompareExchange(ref _owner, InUse, me) == me)
        {
            owned = true;
            return _ownerCache!;
        }
        if (owner == Unowned && Interlocked.CompareExchange(ref _owner, InUse, Unowned) == Unowned)
        {
            _ownerThread = me;
            _ownerCache = create();
            owned = true;
            return _ownerCache;
        }
        owned = false;
        return _shared.TryTake(out var cache) ? cache : create();
    }

    public void Put(Cache cache, bool owned)
    {
        if (owned) Volatile.Write(ref _owner, _ownerThread);
        else _shared.Add(cache);
    }
}

/// <summary>
/// A compiled regex that picks the fastest engine for each search: a literal
/// search, the lazy DFA, or the PikeVM. Leftmost-first semantics, UTF-8
/// haystacks, linear time.
/// </summary>
///
/// <remarks>
/// Methods that take a <see cref="Cache"/> use it; the others take one from
/// a pool, so a regex can be shared between threads.
/// </remarks>
public sealed class Regex
{
    private readonly RegexInfo _info;
    private readonly Strategy _strategy;
    private readonly CachePool _pool;

    private Regex(RegexInfo info, Strategy strategy)
    {
        _info = info;
        _strategy = strategy;
        _pool = new CachePool(strategy.CreateCache);
    }

    public static Regex Create(Hir hir, RegexConfig? config = null)
    {
        var info = new RegexInfo(config ?? new RegexConfig(), hir.Properties);
        return new Regex(info, Strategy.Create(info, hir));
    }

    /// <summary>The capture groups; group 0 is the whole match.</summary>
    public GroupInfo GroupInfo => _strategy.GroupInfo;

    /// <summary>The strategy chosen, for tests and debugging.</summary>
    public string StrategyName => _strategy.Name;

    public Cache CreateCache() => _strategy.CreateCache();

    public bool IsMatch(Cache cache, in Input input)
    {
        var earliest = input with { Earliest = true };
        return !_info.IsImpossible(earliest) && _strategy.IsMatch(cache, earliest);
    }

    public Match? Find(Cache cache, in Input input) =>
        _info.IsImpossible(input) ? null : _strategy.Search(cache, input);

    /// <summary>
    /// The leftmost-first match with as many group spans as
    /// <paramref name="slots"/> holds (slot 2g starts group g, 2g+1 ends it;
    /// -1 when the group took no part). False if there is no match.
    /// </summary>
    public bool SearchSlots(Cache cache, in Input input, Span<int> slots)
    {
        slots.Fill(-1);
        return !_info.IsImpossible(input) && _strategy.SearchSlots(cache, input, slots);
    }

    public bool Captures(Cache cache, in Input input, Captures captures) => SearchSlots(cache, input, captures.Slots);

    public bool IsMatch(in Input input)
    {
        var cache = _pool.Get(out bool owned);
        try
        {
            return IsMatch(cache, input);
        }
        finally
        {
            _pool.Put(cache, owned);
        }
    }

    public Match? Find(in Input input)
    {
        var cache = _pool.Get(out bool owned);
        try
        {
            return Find(cache, input);
        }
        finally
        {
            _pool.Put(cache, owned);
        }
    }

    public bool Captures(in Input input, Captures captures)
    {
        var cache = _pool.Get(out bool owned);
        try
        {
            return Captures(cache, input, captures);
        }
        finally
        {
            _pool.Put(cache, owned);
        }
    }

    public Captures CreateCaptures() => new(GroupInfo.GroupLength);

    /// <summary>Every match, left to right, without overlap.</summary>
    public IEnumerable<Match> FindAll(Input input)
    {
        var cache = _pool.Get(out bool owned);
        try
        {
            var searcher = new Searcher(input);
            while (searcher.Advance(i => Find(cache, i)) is { } m) yield return m;
        }
        finally
        {
            _pool.Put(cache, owned);
        }
    }

    /// <summary>Every match with its groups, left to right. Each item is a fresh <see cref="Search.Captures"/>.</summary>
    public IEnumerable<Captures> CapturesAll(Input input)
    {
        var cache = _pool.Get(out bool owned);
        try
        {
            var searcher = new Searcher(input);
            var caps = CreateCaptures();
            while (searcher.Advance(i => Captures(cache, i, caps) ? caps.Overall : null) is not null)
            {
                yield return caps.Clone();
            }
        }
        finally
        {
            _pool.Put(cache, owned);
        }
    }
}
