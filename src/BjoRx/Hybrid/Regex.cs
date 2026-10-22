// Ported from regex-automata/src/hybrid/regex.rs (rust-lang/regex). MIT OR
// Apache-2.0; see THIRD-PARTY.md.

using BjoRx.Nfa;
using BjoRx.Search;
using BjoRx.Syntax;

namespace BjoRx.Hybrid;

/// <summary>
/// A regex made of two lazy DFAs: a forward one that finds where a match
/// ends, and a reverse one that finds where it starts. No capture groups.
/// </summary>
[NonUserCode]
public sealed class Regex
{
    public DFA Forward { get; }
    public DFA Reverse { get; }

    /// <summary>
    /// The length in bytes of every match, when they all have the same:
    /// then a match starts that far before its end, and the reverse search
    /// is not needed. An idea from .NET's NonBacktracking engine.
    /// </summary>
    public int? FixedLength { get; }

    private Regex(DFA forward, DFA reverse, int? fixedLength)
    {
        Forward = forward;
        Reverse = reverse;
        FixedLength = fixedLength;
    }

    /// <summary>
    /// Compiles both NFAs for <paramref name="hir"/>. The reverse DFA reports
    /// every match, so that it runs to the earliest start rather than stopping
    /// at the first one it sees.
    /// </summary>
    public static Regex Create(Hir hir, DfaConfig? config = null)
    {
        config ??= new DfaConfig();
        var forward = Compiler.Compile(hir, new CompilerConfig { WhichCaptures = WhichCaptures.None });
        var reverse = Compiler.Compile(hir, new CompilerConfig { Reverse = true, WhichCaptures = WhichCaptures.None });
        return FromNfas(forward, reverse, config, FixedLengthOf(hir));
    }

    /// <summary>The length in bytes of every match of <paramref name="hir"/>, if they all have the same.</summary>
    public static int? FixedLengthOf(Hir hir) =>
        hir.Properties.MinimumLength is { } min && hir.Properties.MaximumLength == min ? min : null;

    public static Regex FromNfas(NFA forward, NFA reverse, DfaConfig config, int? fixedLength = null) =>
        new(DFA.Create(forward, config),
            DFA.Create(reverse, config with { MatchKind = MatchKind.All, Prefilter = null, SpecializeStartStates = false }),
            fixedLength);

    public Cache CreateCache() => new(this);

    public void ResetCache(Cache cache)
    {
        Forward.ResetCache(cache.Forward);
        Reverse.ResetCache(cache.Reverse);
    }

    /// <summary>Whether there is a match, in <paramref name="isMatch"/>; false if the search gave up.</summary>
    public bool TryIsMatch(Cache cache, in Input input, out bool isMatch)
    {
        var result = Forward.TrySearchFwd(cache.Forward, input with { Earliest = true });
        isMatch = result.IsMatch;
        return !result.IsGaveUp;
    }

    /// <summary>The leftmost-first match, in <paramref name="match"/>; false if the search gave up.</summary>
    public bool TrySearch(Cache cache, in Input input, out Match? match)
    {
        match = null;
        var end = Forward.TrySearchFwd(cache.Forward, input);
        if (end.IsGaveUp) return false;
        if (!end.IsMatch) return true;
        // An empty match at the start, or an anchored one, starts where the search did.
        if (input.Start == end.Offset || IsAnchored(input))
        {
            match = new Match(input.Start, end.Offset);
            return true;
        }
        if (FixedLength is { } length)
        {
            match = new Match(end.Offset - length, end.Offset);
            return true;
        }
        // The match starts at the earliest offset from which the pattern
        // matches up to its end, which a reverse search anchored there finds.
        var reverse = input with { End = end.Offset, Anchored = Anchored.Yes, Earliest = false };
        var start = Reverse.TrySearchRev(cache.Reverse, reverse);
        if (start.IsGaveUp) return false;
        if (!start.IsMatch) throw new InvalidOperationException("the reverse search must match where the forward search did");
        match = new Match(start.Offset, end.Offset);
        return true;
    }

    private bool IsAnchored(in Input input) =>
        input.Anchored == Anchored.Yes || Forward.Nfa.IsAlwaysStartAnchored;

    /// <summary>
    /// Every match, left to right. Throws if a search gives up, which a
    /// <see cref="DfaConfig"/> without a minimum clear count never does.
    /// </summary>
    public IEnumerable<Match> FindAll(Cache cache, Input input)
    {
        var searcher = new Searcher(input);
        while (searcher.Advance([NonUserCode] (i) => TrySearch(cache, i, out var m) ? m : throw new InvalidOperationException("the lazy DFA gave up")) is { } m)
        {
            yield return m;
        }
    }

    [NonUserCode]
    public sealed class Cache
    {
        public DFA.Cache Forward { get; }
        public DFA.Cache Reverse { get; }

        internal Cache(Regex re)
        {
            Forward = re.Forward.CreateCache();
            Reverse = re.Reverse.CreateCache();
        }
    }
}
