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
    /// backwards from the end when every match ends there, or from a literal
    /// every match ends with or contains.
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
        return ReverseAnchored.Create(core)
            ?? ReverseSuffix.Create(core, hir)
            ?? ReverseInner.Create(core, hir)
            ?? (Strategy)core;
    }

    /// <summary>
    /// Upstream's rule: a fast search for the literals every match starts
    /// with beats searching for a later literal and then backwards.
    /// </summary>
    protected static bool HasFastLiteralPrefilter(Core core) =>
        core.Pre is { IsFast: true } and not FixedDistanceSets;

    /// <summary>
    /// Fast fixed-distance sets check several bytes per candidate and, for
    /// a pattern of fixed length, need no reverse search; that beats a
    /// reverse strategy searching for a single byte (dates: 15.8 against
    /// 9.2 GB/s), but not one searching for a longer literal (<c>[a-z]shing</c>:
    /// 4.6 against 41 GB/s).
    /// </summary>
    protected static bool SetsBeatOneByte(Core core, Prefilter reverseLiteral) =>
        core.Pre is FixedDistanceSets { IsFast: true } && reverseLiteral.MaxNeedleLength <= 1;

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
/// it starts, unless every match has the same length; the PikeVM then finds
/// the groups in that span only. When the DFA gives up or could not be
/// built, the PikeVM searches alone.
/// </summary>
internal sealed class Core : Strategy
{
    public RegexInfo Info { get; }
    public Prefilter? Pre { get; }
    public PikeVM PikeVM { get; }
    public HybridRegex? Hybrid { get; }

    private Core(RegexInfo info, Prefilter? pre, PikeVM pikeVM, HybridRegex? hybrid)
    {
        Info = info;
        Pre = pre;
        PikeVM = pikeVM;
        Hybrid = hybrid;
    }

    public static Core Create(RegexInfo info, Prefilter? pre, Hir hir)
    {
        var config = info.Config;
        var nfa = Compiler.Compile(hir, new CompilerConfig { NfaSizeLimit = config.NfaSizeLimit });
        // Without a fast literal search, the bytes at fixed distances from a
        // match's start may make a better one (not upstream; .NET's idea).
        if (config.AutoPrefilter && !info.IsAlwaysAnchoredStart && pre is not { IsFast: true }
            && FixedDistanceSets.FromNfa(nfa) is { } sets)
        {
            pre = sets;
        }
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
                hybrid = HybridRegex.FromNfas(nfa, nfarev, dfaConfig, HybridRegex.FixedLengthOf(hir));
            }
            catch (BuildException)
            {
                // The cache capacity is too small for this NFA.
            }
        }
        return new Core(info, pre, new PikeVM(nfa), hybrid);
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

/// <summary>
/// Every match ends with one literal: find it, search backwards from its end
/// for where a match starts, then forwards from there for where it ends
/// (which may be past this literal: <c>[a-z]+ing</c> in <c>tingling</c>).
/// Upstream's ReverseSuffix.
/// </summary>
internal sealed class ReverseSuffix : Strategy
{
    private readonly Core _core;
    private readonly Prefilter _pre;

    private ReverseSuffix(Core core, Prefilter pre)
    {
        _core = core;
        _pre = pre;
    }

    public static ReverseSuffix? Create(Core core, Hir hir)
    {
        var info = core.Info;
        // Like the core's prefilter, only for unanchored searches, and the
        // reverse search needs the lazy DFA.
        if (!info.Config.AutoPrefilter || info.IsAlwaysAnchoredStart || core.Hybrid is null) return null;
        if (HasFastLiteralPrefilter(core)) return null;
        var suffixes = new Extractor { Kind = ExtractKind.Suffix }.Extract(hir);
        suffixes.OptimizeForSuffixByPreference();
        if (suffixes.LongestCommonSuffix() is not { Length: > 0 } lcs) return null;
        if (Prefilter.Create([lcs]) is not { IsFast: true } pre) return null;
        if (SetsBeatOneByte(core, pre)) return null;
        if (!LiteralProofs.SuffixHasNoEarlierMatch(hir, lcs)) return null;
        return new ReverseSuffix(core, pre);
    }

    public override string Name => "reverse suffix";

    public override GroupInfo GroupInfo => _core.GroupInfo;

    public override Cache CreateCache() => _core.CreateCache();

    /// <summary>
    /// Where the first match starts: at each occurrence of the suffix, a
    /// reverse search for a match ending with it, which must not rescan what
    /// the search at the previous occurrence did.
    /// </summary>
    private Retry SearchStart(Cache cache, in Input input)
    {
        var haystack = input.Haystack.Span;
        int spanStart = input.Start, end = input.End, minStart = 0;
        while (_pre.Find(haystack, spanStart, end) is { } lit)
        {
            var rev = input with { Anchored = Anchored.Yes, End = lit.End };
            var start = Limited.SearchRev(_core.Hybrid!.Reverse, cache.Hybrid!.Reverse, rev, minStart);
            if (start.Kind != RetryKind.NoMatch) return start;
            if (spanStart >= end) break;
            spanStart = lit.Start + 1;
            minStart = lit.End;
        }
        return Retry.NoMatch();
    }

    public override Match? Search(Cache cache, in Input input)
    {
        if (input.Anchored == Anchored.Yes) return _core.Search(cache, input);
        var start = SearchStart(cache, input);
        switch (start.Kind)
        {
            case RetryKind.Quadratic:
                return _core.Search(cache, input);
            case RetryKind.GaveUp:
                return _core.SearchNoFail(cache, input);
            case RetryKind.NoMatch:
                return null;
        }
        var fwd = input with { Anchored = Anchored.Yes, Start = start.Offset };
        var end = _core.Hybrid!.Forward.TrySearchFwd(cache.Hybrid!.Forward, fwd);
        if (end.IsGaveUp) return _core.SearchNoFail(cache, input);
        if (!end.IsMatch) throw new InvalidOperationException("a suffix and a reverse match imply a match");
        return new Match(start.Offset, end.Offset);
    }

    public override bool IsMatch(Cache cache, in Input input)
    {
        if (input.Anchored == Anchored.Yes) return _core.IsMatch(cache, input);
        return SearchStart(cache, input).Kind switch
        {
            RetryKind.Match => true,
            RetryKind.NoMatch => false,
            _ => _core.PikeVM.IsMatch(cache.PikeVM!, input),
        };
    }

    public override bool SearchSlots(Cache cache, in Input input, Span<int> slots)
    {
        if (input.Anchored == Anchored.Yes) return _core.SearchSlots(cache, input, slots);
        if (slots.Length <= 2)
        {
            if (Search(cache, input) is not { } m) return false;
            CopyMatchToSlots(m, slots);
            return true;
        }
        var start = SearchStart(cache, input);
        return start.Kind switch
        {
            RetryKind.Quadratic => _core.SearchSlots(cache, input, slots),
            RetryKind.GaveUp => _core.PikeVM.SearchSlots(cache.PikeVM!, input, slots),
            RetryKind.NoMatch => false,
            // Only the start is known; the PikeVM finds the end with the groups.
            _ => _core.PikeVM.SearchSlots(cache.PikeVM!, input with { Start = start.Offset, Anchored = Anchored.Yes }, slots),
        };
    }
}

/// <summary>
/// Every match contains one of some literals after a known prefix: find a
/// literal, search backwards for the prefix to find where the match starts,
/// then forwards from there for where it ends. Upstream's ReverseInner.
/// </summary>
internal sealed class ReverseInner : Strategy
{
    private readonly Core _core;
    private readonly Prefilter _pre;
    // The reverse DFA of the part of the pattern before the literals.
    private readonly DFA _prefixRev;

    private ReverseInner(Core core, Prefilter pre, DFA prefixRev)
    {
        _core = core;
        _pre = pre;
        _prefixRev = prefixRev;
    }

    public static ReverseInner? Create(Core core, Hir hir)
    {
        var info = core.Info;
        if (!info.Config.AutoPrefilter || info.IsAlwaysAnchoredStart || core.Hybrid is null) return null;
        // A fast prefix prefilter beats this; a slow one does not.
        if (HasFastLiteralPrefilter(core)) return null;
        if (InnerPrefilter.Create(hir) is not { } inner) return null;
        if (SetsBeatOneByte(core, inner.Prefilter)) return null;
        if (!LiteralProofs.InnerHasNoEarlierMatch(inner.Prefix, inner.Literals)) return null;
        try
        {
            var nfarev = Compiler.Compile(inner.Prefix, new CompilerConfig
            {
                Reverse = true,
                WhichCaptures = WhichCaptures.None,
                NfaSizeLimit = info.Config.NfaSizeLimit,
            });
            // Only searched in reverse, from a literal back to the start.
            var dfa = DFA.Create(nfarev, new DfaConfig
            {
                MatchKind = MatchKind.All,
                SpecializeStartStates = false,
                CacheCapacity = info.Config.HybridCacheCapacity,
                MinimumCacheClearCount = 3,
                MinimumBytesPerState = 10,
            });
            return new ReverseInner(core, inner.Prefilter, dfa);
        }
        catch (BuildException)
        {
            return null;
        }
    }

    public override string Name => "reverse inner";

    public override GroupInfo GroupInfo => _core.GroupInfo;

    public override Cache CreateCache() =>
        new(_core.PikeVM.CreateCache(), _core.Hybrid!.CreateCache(), _prefixRev.CreateCache());

    /// <summary>
    /// The first match: at each literal candidate, a reverse search for the
    /// prefix, then a forward search for the whole pattern from its start.
    /// Neither may rescan what earlier ones did, which would make the search
    /// quadratic; then the caller falls back to the core.
    /// </summary>
    private RetryKind SearchFull(Cache cache, in Input input, out Match match)
    {
        match = default;
        var haystack = input.Haystack.Span;
        int spanStart = input.Start, end = input.End, minMatchStart = 0, minPreStart = 0;
        while (_pre.Find(haystack, spanStart, end) is { } lit)
        {
            if (lit.Start < minPreStart) return RetryKind.Quadratic;
            var rev = input with { Anchored = Anchored.Yes, End = lit.Start };
            var start = Limited.SearchRev(_prefixRev, cache.RevInner!, rev, minMatchStart);
            if (start.Kind is RetryKind.Quadratic or RetryKind.GaveUp) return start.Kind;
            if (start.Kind == RetryKind.Match)
            {
                var fwd = input with { Anchored = Anchored.Yes, Start = start.Offset };
                var stop = Limited.SearchFwdStopAt(_core.Hybrid!.Forward, cache.Hybrid!.Forward, fwd);
                if (stop.Kind == RetryKind.GaveUp) return RetryKind.GaveUp;
                if (stop.Kind == RetryKind.Match)
                {
                    match = new Match(start.Offset, stop.Offset);
                    return RetryKind.Match;
                }
                minPreStart = stop.Offset;
                spanStart = lit.Start + 1;
            }
            if (spanStart >= end) break;
            spanStart = lit.Start + 1;
            minMatchStart = lit.End;
        }
        return RetryKind.NoMatch;
    }

    public override Match? Search(Cache cache, in Input input)
    {
        if (input.Anchored == Anchored.Yes) return _core.Search(cache, input);
        return SearchFull(cache, input, out var m) switch
        {
            RetryKind.Quadratic => _core.Search(cache, input),
            RetryKind.GaveUp => _core.SearchNoFail(cache, input),
            RetryKind.NoMatch => null,
            _ => m,
        };
    }

    public override bool IsMatch(Cache cache, in Input input)
    {
        if (input.Anchored == Anchored.Yes) return _core.IsMatch(cache, input);
        return SearchFull(cache, input, out _) switch
        {
            RetryKind.Match => true,
            RetryKind.NoMatch => false,
            _ => _core.PikeVM.IsMatch(cache.PikeVM!, input),
        };
    }

    public override bool SearchSlots(Cache cache, in Input input, Span<int> slots)
    {
        if (input.Anchored == Anchored.Yes) return _core.SearchSlots(cache, input, slots);
        if (slots.Length <= 2)
        {
            if (Search(cache, input) is not { } found) return false;
            CopyMatchToSlots(found, slots);
            return true;
        }
        return SearchFull(cache, input, out var m) switch
        {
            RetryKind.Quadratic => _core.SearchSlots(cache, input, slots),
            RetryKind.GaveUp => _core.PikeVM.SearchSlots(cache.PikeVM!, input, slots),
            RetryKind.NoMatch => false,
            _ => _core.SearchSlotsInMatch(cache, input, m.Start, m.End, slots),
        };
    }
}
