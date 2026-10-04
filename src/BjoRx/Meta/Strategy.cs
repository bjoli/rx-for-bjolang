// Ported from regex-automata/src/meta/strategy.rs and meta/wrappers.rs
// (rust-lang/regex). MIT OR Apache-2.0; see THIRD-PARTY.md.

using BjoRx.Hybrid;
using BjoRx.Nfa;
using BjoRx.Search;
using BjoRx.Syntax;
using HybridRegex = BjoRx.Hybrid.Regex;

namespace BjoRx.Meta;

/// <summary>
/// One way of running a regex. The meta regex picks one when it is built.
/// Upstream also has strategies that search for a suffix or an inner
/// literal and then search backwards; they are not ported.
/// </summary>
internal abstract class Strategy
{
    public abstract string Name { get; }

    public abstract GroupInfo GroupInfo { get; }

    public abstract Cache CreateCache();

    public abstract Match? Search(Cache cache, in Input input);

    public abstract bool IsMatch(Cache cache, in Input input);

    /// <summary>The match and as many of its slots as <paramref name="slots"/> holds; false if none.</summary>
    public abstract bool SearchSlots(Cache cache, in Input input, Span<int> slots);

    /// <summary>
    /// Picks the strategy for <paramref name="hir"/>: a literal search alone
    /// if the literals are the whole regex, else the core engines, searching
    /// backwards from the end when every match ends there.
    /// </summary>
    public static Strategy Create(RegexInfo info, Hir hir)
    {
        Prefilter? pre = null;
        // An anchored regex is tried only at the start, so a prefilter does
        // not get to skip anything.
        if (!info.IsAlwaysAnchoredStart && info.Config.AutoPrefilter)
        {
            var prefixes = Prefilter.Prefixes(hir);
            if (Pre.FromPrefixes(info, prefixes) is { } literalsOnly) return literalsOnly;
            pre = prefixes.Literals is { } lits ? Prefilter.Create(lits.Select(l => l.Bytes).ToList()) : null;
        }
        var core = Core.Create(info, pre, hir);
        return ReverseAnchored.Create(core) ?? (Strategy)core;
    }

    protected static void CopyMatchToSlots(Match m, Span<int> slots)
    {
        if (slots.Length > 0) slots[0] = m.Start;
        if (slots.Length > 1) slots[1] = m.End;
    }
}

/// <summary>The regex is a set of exact literals, so finding a literal is finding a match.</summary>
internal sealed class Pre(Prefilter pre) : Strategy
{
    private static readonly GroupInfo ImplicitOnly = new([null]);

    public override string Name => "prefilter " + pre.Name;

    public override GroupInfo GroupInfo => ImplicitOnly;

    public static Pre? FromPrefixes(RegexInfo info, Seq prefixes)
    {
        // Only when the literals say everything: exact, no groups for the
        // caller to see, and no assertions, which extraction treats as always
        // true (it gets "ab" from a\zb).
        if (!prefixes.IsExact || info.Props.ExplicitCapturesLength != 0 || !info.Props.LookSet.IsEmpty) return null;
        var pre = Prefilter.Create(prefixes.Literals!.Select(l => l.Bytes).ToList());
        return pre is null ? null : new Pre(pre);
    }

    public override Cache CreateCache() => new(null, null);

    public override Match? Search(Cache cache, in Input input)
    {
        if (input.IsDone) return null;
        var haystack = input.Haystack.Span;
        return input.Anchored == Anchored.Yes
            ? pre.Prefix(haystack, input.Start, input.End)
            : pre.Find(haystack, input.Start, input.End);
    }

    public override bool IsMatch(Cache cache, in Input input) => Search(cache, input) is not null;

    public override bool SearchSlots(Cache cache, in Input input, Span<int> slots)
    {
        if (Search(cache, input) is not { } m) return false;
        CopyMatchToSlots(m, slots);
        return true;
    }
}

/// <summary>
/// The lazy DFA where it can, the PikeVM where it cannot: the forward DFA
/// (with the prefilter) finds where a match ends and the reverse one where
/// it starts; the PikeVM then finds the groups in that span only. When the
/// DFA gives up or could not be built, the PikeVM searches alone.
/// </summary>
internal sealed class Core : Strategy
{
    public RegexInfo Info { get; }
    public PikeVM PikeVM { get; }
    public HybridRegex? Hybrid { get; }

    private Core(RegexInfo info, PikeVM pikeVM, HybridRegex? hybrid)
    {
        Info = info;
        PikeVM = pikeVM;
        Hybrid = hybrid;
    }

    public static Core Create(RegexInfo info, Prefilter? pre, Hir hir)
    {
        var config = info.Config;
        var nfa = Compiler.Compile(hir, new CompilerConfig { NfaSizeLimit = config.NfaSizeLimit });
        HybridRegex? hybrid = null;
        if (config.Hybrid)
        {
            var nfarev = Compiler.Compile(hir, new CompilerConfig
            {
                Reverse = true,
                WhichCaptures = WhichCaptures.None,
                NfaSizeLimit = config.NfaSizeLimit,
            });
            var dfaConfig = new DfaConfig
            {
                Prefilter = pre,
                SpecializeStartStates = pre is not null,
                CacheCapacity = config.HybridCacheCapacity,
                // Upstream's meta settings: give up after three clears, unless
                // each state built has served ten bytes of haystack.
                MinimumCacheClearCount = 3,
                MinimumBytesPerState = 10,
            };
            try
            {
                hybrid = HybridRegex.FromNfas(nfa, nfarev, dfaConfig);
            }
            catch (BuildException)
            {
                // The cache capacity is too small for this NFA.
            }
        }
        return new Core(info, new PikeVM(nfa), hybrid);
    }

    public override string Name => Hybrid is null ? "core (PikeVM)" : "core";

    public override GroupInfo GroupInfo => PikeVM.Nfa.GroupInfo;

    public override Cache CreateCache() => new(PikeVM.CreateCache(), Hybrid?.CreateCache());

    public override Match? Search(Cache cache, in Input input)
    {
        if (Hybrid is not null && Hybrid.TrySearch(cache.Hybrid!, input, out var m)) return m;
        return SearchNoFail(cache, input);
    }

    public Match? SearchNoFail(Cache cache, in Input input)
    {
        Span<int> slots = stackalloc int[2];
        return PikeVM.SearchSlots(cache.PikeVM!, input, slots) ? new Match(slots[0], slots[1]) : null;
    }

    public override bool IsMatch(Cache cache, in Input input)
    {
        if (Hybrid is not null)
        {
            // Where a match ends is enough to know there is one.
            var end = Hybrid.Forward.TrySearchFwd(cache.Hybrid!.Forward, input);
            if (!end.IsGaveUp) return end.IsMatch;
        }
        return PikeVM.IsMatch(cache.PikeVM!, input);
    }

    public override bool SearchSlots(Cache cache, in Input input, Span<int> slots)
    {
        // Without slots for explicit groups, the span is all that is needed.
        if (slots.Length <= 2)
        {
            if (Search(cache, input) is not { } found) return false;
            CopyMatchToSlots(found, slots);
            return true;
        }
        if (Hybrid is null || !Hybrid.TrySearch(cache.Hybrid!, input, out var m))
        {
            return PikeVM.SearchSlots(cache.PikeVM!, input, slots);
        }
        if (m is not { } match) return false;
        return SearchSlotsInMatch(cache, input, match.Start, match.End, slots);
    }

    /// <summary>The groups of a match already found, by the PikeVM on its span alone.</summary>
    public bool SearchSlotsInMatch(Cache cache, in Input input, int start, int end, Span<int> slots)
    {
        var span = input with { Start = start, End = end, Anchored = Anchored.Yes };
        if (!PikeVM.SearchSlots(cache.PikeVM!, span, slots))
        {
            throw new InvalidOperationException("the PikeVM must match where the lazy DFA did");
        }
        return true;
    }
}

/// <summary>
/// Every match ends at the end of the haystack, so one reverse search from
/// there, anchored, finds it. Upstream's ReverseAnchored.
/// </summary>
internal sealed class ReverseAnchored : Strategy
{
    private readonly Core _core;

    private ReverseAnchored(Core core)
    {
        _core = core;
    }

    public static ReverseAnchored? Create(Core core)
    {
        // Also anchored at the start: the forward search is anchored too.
        if (!core.Info.IsAlwaysAnchoredEnd || core.Info.IsAlwaysAnchoredStart || core.Hybrid is null) return null;
        return new ReverseAnchored(core);
    }

    public override string Name => "reverse anchored";

    public override GroupInfo GroupInfo => _core.GroupInfo;

    public override Cache CreateCache() => _core.CreateCache();

    // The meta regex has checked that the span ends at the haystack's end;
    // a match is impossible otherwise.
    private DfaResult SearchStart(Cache cache, in Input input) =>
        _core.Hybrid!.Reverse.TrySearchRev(cache.Hybrid!.Reverse, input with { Anchored = Anchored.Yes });

    public override Match? Search(Cache cache, in Input input)
    {
        // An anchored search finds its start either way; the core does it.
        if (input.Anchored == Anchored.Yes) return _core.Search(cache, input);
        var start = SearchStart(cache, input);
        if (start.IsGaveUp) return _core.SearchNoFail(cache, input);
        return start.IsMatch ? new Match(start.Offset, input.End) : null;
    }

    public override bool IsMatch(Cache cache, in Input input)
    {
        if (input.Anchored == Anchored.Yes) return _core.IsMatch(cache, input);
        var start = SearchStart(cache, input);
        if (start.IsGaveUp) return _core.PikeVM.IsMatch(cache.PikeVM!, input);
        return start.IsMatch;
    }

    public override bool SearchSlots(Cache cache, in Input input, Span<int> slots)
    {
        if (input.Anchored == Anchored.Yes) return _core.SearchSlots(cache, input, slots);
        var start = SearchStart(cache, input);
        if (start.IsGaveUp) return _core.PikeVM.SearchSlots(cache.PikeVM!, input, slots);
        if (!start.IsMatch) return false;
        if (slots.Length <= 2)
        {
            CopyMatchToSlots(new Match(start.Offset, input.End), slots);
            return true;
        }
        return _core.SearchSlotsInMatch(cache, input, start.Offset, input.End, slots);
    }
}
