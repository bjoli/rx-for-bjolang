// Ported from regex-automata/src/meta/reverse_inner.rs (rust-lang/regex).
// MIT OR Apache-2.0; see THIRD-PARTY.md.

using BjoRx.Search;
using BjoRx.Syntax;

namespace BjoRx.Meta;

/// <summary>
/// A prefilter for literals inside a pattern, one of which every match
/// contains, and the part of the pattern before them. ReverseInner searches
/// for the literals, then for the prefix backwards from there.
/// </summary>
///
/// <remarks>
/// Only a top-level concatenation is looked at: for <c>[a-z]+@[a-z]+</c> the
/// literal is <c>@</c> and the prefix <c>[a-z]+</c>. An alternation like
/// <c>[0-9]+@!|[a-z]+%%</c> has literals one of which must match, but no
/// single prefix, so it is not handled.
/// </remarks>
internal sealed class InnerPrefilter
{
    public Hir Prefix { get; }
    public Prefilter Prefilter { get; }

    /// <summary>The literals <see cref="Prefilter"/> searches for, for the proof that the strategy is safe.</summary>
    public IReadOnlyList<byte[]> Literals { get; }

    private InnerPrefilter(Hir prefix, Prefilter prefilter, IReadOnlyList<byte[]> literals)
    {
        Prefix = prefix;
        Prefilter = prefilter;
        Literals = literals;
    }

    public static InnerPrefilter? Create(Hir hir)
    {
        if (TopConcat(hir) is not { } concat) return null;
        // The first part is skipped: had it a good prefix, the regex would
        // have a fast prefilter and not be here.
        for (int i = 1; i < concat.Count; i++)
        {
            // A candidate costs a reverse and a forward search, so the
            // literal search has to be fast to pay for them.
            if (PrefilterWithLiterals(concat[i]) is not var (pre, lits) || !pre.IsFast) continue;
            var suffix = Hir.Concat(concat.Skip(i));
            var prefix = Hir.Concat(concat.Take(i));
            // All of what follows may have better literals than its first
            // part alone; only this once, so the loop stays linear.
            if (PrefilterWithLiterals(suffix) is var (pre2, lits2) && pre2.IsFast)
            {
                (pre, lits) = (pre2, lits2);
            }
            return new InnerPrefilter(prefix, pre, lits);
        }
        return null;
    }

    private static (Prefilter, IReadOnlyList<byte[]>)? PrefilterWithLiterals(Hir hir)
    {
        var prefixes = new Extractor().Extract(hir);
        // Inner literals are never a whole match, whatever the extractor
        // thinks; marking them so keeps the optimizer from favoring a set of
        // exact single bytes, which would make a poor prefilter here.
        prefixes.MakeInexact();
        prefixes.OptimizeForPrefixByPreference();
        if (prefixes.Literals is not { } lits) return null;
        var bytes = lits.Select(l => l.Bytes).ToList();
        return Prefilter.Create(bytes) is { } pre ? (pre, bytes) : null;
    }

    /// <summary>
    /// The parts of the top-level concatenation, looking through capture
    /// groups and with every group inside removed: groups never decide whether
    /// there is a match, and the reverse search does not report them.
    /// </summary>
    private static IReadOnlyList<Hir>? TopConcat(Hir hir)
    {
        while (true)
        {
            switch (hir)
            {
                case HirCapture cap:
                    hir = cap.Sub;
                    break;
                case HirConcat concat:
                    // Flattening may simplify the concatenation away.
                    return Hir.Concat(concat.Subs.Select(Flatten)) is HirConcat flat ? flat.Subs : null;
                default:
                    return null;
            }
        }
    }

    private static Hir Flatten(Hir hir) => hir switch
    {
        HirRepetition r => r.With(Flatten(r.Sub)),
        HirCapture c => Flatten(c.Sub),
        HirAlternation a => Hir.Alternation(a.Subs.Select(Flatten)),
        HirConcat c => Hir.Concat(c.Subs.Select(Flatten)),
        _ => hir,
    };
}
