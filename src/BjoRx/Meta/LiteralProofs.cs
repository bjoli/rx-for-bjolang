// Ported from regex-automata/src/meta/prefix.rs, meta/reverse_suffix.rs and
// the proof in meta/reverse_inner.rs (rust-lang/regex). MIT OR Apache-2.0;
// see THIRD-PARTY.md.

using System.Text;
using BjoRx.Syntax;

namespace BjoRx.Meta;

/// <summary>
/// Cheap proofs that the first literal candidate a reverse strategy confirms
/// is the match the regex engine would report.
/// </summary>
///
/// <remarks>
/// ReverseSuffix and ReverseInner look for a literal every match contains,
/// then search backwards for the start and forwards for the end. A match
/// that starts earlier but whose literal comes later would be missed, so
/// those strategies are used only when one of these proofs holds. Every
/// proof is conservative: false means "could not show it", not "unsafe".
/// </remarks>
internal static class LiteralProofs
{
    /// <summary>
    /// For ReverseSuffix: no match ends before an occurrence of
    /// <paramref name="suffix"/> that a reverse scan from it would miss.
    /// </summary>
    public static bool SuffixHasNoEarlierMatch(Hir hir, byte[] suffix)
    {
        if (suffix.Length == 0) return false;
        if (StripLiteralSuffix(hir, suffix) is not { } prefix) return false;
        return HasFixedLength(prefix)
            || HasDisjointClassSeparator(prefix, [suffix])
            || !CanContainLiteral(prefix, suffix);
    }

    /// <summary>
    /// For ReverseInner: no match is confirmed from an occurrence of one of
    /// <paramref name="literals"/> before an earlier match.
    /// </summary>
    public static bool InnerHasNoEarlierMatch(Hir prefix, IReadOnlyList<byte[]> literals)
    {
        if (literals.Count == 0 || literals.Any(l => l.Length == 0)) return false;
        // With one literal, an occurrence crossing into the prefix overlaps
        // another occurrence, which needs the prefix to consume every
        // distinct byte of the literal.
        if (literals.Count == 1 && !CanContainLiteral(prefix, literals[0])) return true;
        return HasFixedLength(prefix) || HasDisjointClassSeparator(prefix, literals);
    }

    /// <summary>
    /// Whether <paramref name="hir"/> can match a string containing every
    /// byte of <paramref name="literal"/>. Low precision: true means maybe.
    /// </summary>
    public static bool CanContainLiteral(Hir hir, byte[] literal) =>
        literal.Length == 0 || literal.All(b => CanConsumeByte(hir, b));

    public static bool HasFixedLength(Hir hir) =>
        hir.Properties.MinimumLength is { } min && hir.Properties.MaximumLength == min;

    /// <summary>
    /// Whether the first or last consuming part of the top-level
    /// concatenation <paramref name="prefix"/> is a required class (or a
    /// repetition of one, at least once) that shares no character with the
    /// other parts nor with any literal. Such a separator cannot slide across
    /// either, so a reverse search cannot produce a later start from an
    /// earlier candidate. <c>[a-z]+[ \n]+</c> before <c>Holmes</c> is one.
    /// </summary>
    public static bool HasDisjointClassSeparator(Hir prefix, IReadOnlyList<byte[]> literals)
    {
        if (Uncapture(prefix) is not HirConcat { Subs.Count: >= 2 } concat) return false;
        var hirs = concat.Subs;
        int first = -1, last = -1;
        for (int i = 0; i < hirs.Count; i++)
        {
            if (MatchesEmptyOnly(hirs[i])) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) return false;
        return HasDisjointClassSeparatorAt(hirs, last, literals)
            || (first != last && HasDisjointClassSeparatorAt(hirs, first, literals));
    }

    private static bool HasDisjointClassSeparatorAt(IReadOnlyList<Hir> hirs, int separator, IReadOnlyList<byte[]> literals)
    {
        if (RequiredClass(hirs[separator]) is not { } cls) return false;
        if (!literals.All(l => ClassIsDisjointFromLiteral(cls, l))) return false;
        for (int i = 0; i < hirs.Count; i++)
        {
            if (i != separator && !IsDisjointFromClass(hirs[i], cls)) return false;
        }
        return true;
    }

    private static bool MatchesEmptyOnly(Hir hir) => hir.Properties.MaximumLength == 0;

    private static ScalarClass? RequiredClass(Hir hir) => Uncapture(hir) switch
    {
        HirClass c => c.Set,
        HirRepetition { Min: > 0 } r when Uncapture(r.Sub) is HirClass c => c.Set,
        _ => null,
    };

    private static bool IsDisjointFromClass(Hir hir, ScalarClass separator) => hir switch
    {
        HirEmpty or HirLook => true,
        HirLiteral lit => ClassIsDisjointFromLiteral(separator, lit.Bytes),
        HirClass c => ClassesAreDisjoint(c.Set, separator),
        HirRepetition r => IsDisjointFromClass(r.Sub, separator),
        HirCapture c => IsDisjointFromClass(c.Sub, separator),
        HirConcat c => c.Subs.All(s => IsDisjointFromClass(s, separator)),
        HirAlternation a => a.Subs.All(s => IsDisjointFromClass(s, separator)),
        _ => false,
    };

    private static Hir Uncapture(Hir hir)
    {
        while (hir is HirCapture c) hir = c.Sub;
        return hir;
    }

    private static bool ClassesAreDisjoint(ScalarClass a, ScalarClass b)
    {
        var intersection = a.Clone();
        intersection.Intersect(b);
        return intersection.IsEmpty;
    }

    /// <summary>False when the literal is not valid UTF-8, since it cannot be read as scalars.</summary>
    private static bool ClassIsDisjointFromLiteral(ScalarClass cls, byte[] literal)
    {
        var rest = (ReadOnlySpan<byte>)literal;
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf8(rest, out var rune, out int n) != System.Buffers.OperationStatus.Done) return false;
            if (cls.Contains((uint)rune.Value)) return false;
            rest = rest[n..];
        }
        return true;
    }

    private static bool CanConsumeByte(Hir hir, byte b) => hir switch
    {
        HirEmpty or HirLook => false,
        HirLiteral lit => lit.Bytes.AsSpan().Contains(b),
        // Literals are not checked by scalar, so a class is assumed to
        // consume any non-ASCII byte.
        HirClass c => b > 0x7F || c.Set.Contains(b),
        HirRepetition r => CanConsumeByte(r.Sub, b),
        HirCapture c => CanConsumeByte(c.Sub, b),
        HirConcat c => c.Subs.Any(s => CanConsumeByte(s, b)),
        HirAlternation a => a.Subs.Any(s => CanConsumeByte(s, b)),
        _ => true,
    };

    /// <summary>
    /// <paramref name="hir"/> with <paramref name="suffix"/> removed from its
    /// end, or null when the structure does not make that straightforward,
    /// even if every match does end with it.
    /// </summary>
    public static Hir? StripLiteralSuffix(Hir hir, byte[] suffix) =>
        StripLiteralSuffixAt(hir, suffix, suffix.Length) is ({ } prefix, 0) ? prefix : null;

    /// <summary>
    /// Strips <c>suffix[after..cursor]</c> from the end of <paramref name="hir"/>
    /// and returns what remains with <c>after</c>. A non-zero <c>after</c>
    /// comes back only when what remains consumes nothing, so a concatenation
    /// can go on stripping from the part before.
    /// </summary>
    private static (Hir Hir, int Cursor)? StripLiteralSuffixAt(Hir hir, byte[] suffix, int cursor)
    {
        if (cursor == 0) return (hir, 0);
        switch (hir)
        {
            case HirLiteral lit:
            {
                var bytes = lit.Bytes;
                int len = 0;
                while (len < bytes.Length && len < cursor && bytes[bytes.Length - len - 1] == suffix[cursor - len - 1]) len++;
                if (len == 0 || (len < bytes.Length && len < cursor)) return null;
                return (Hir.Literal(bytes.AsSpan(0, bytes.Length - len)), cursor - len);
            }
            case HirCapture cap:
                return StripLiteralSuffixAt(cap.Sub, suffix, cursor);
            case HirConcat concat:
            {
                var parts = concat.Subs.ToList();
                for (int i = parts.Count - 1; i >= 0; i--)
                {
                    if (StripLiteralSuffixAt(parts[i], suffix, cursor) is not var (stripped, after)) return null;
                    if (after == cursor) return null;
                    parts[i] = stripped;
                    cursor = after;
                    if (cursor == 0) break;
                }
                return (Hir.Concat(parts), cursor);
            }
            case HirRepetition rep:
                return StripRepetitionLiteralSuffix(rep, suffix, cursor);
            default:
                return null;
        }
    }

    /// <summary>Strips whole copies of the repeated part, each a required one.</summary>
    private static (Hir, int)? StripRepetitionLiteralSuffix(HirRepetition rep, byte[] suffix, int cursor)
    {
        uint min = rep.Min;
        uint? max = rep.Max;
        var tail = new List<Hir>();
        while (cursor > 0)
        {
            if (min == 0) return null;
            if (StripLiteralSuffixAt(rep.Sub, suffix, cursor) is not var (stripped, after)) return null;
            if (after == cursor) return null;
            min--;
            max = max is { } m ? (m == 0 ? 0 : m - 1) : null;
            if (stripped is not HirEmpty) tail.Add(stripped);
            cursor = after;
        }
        var rest = Hir.Repetition(min, max, rep.Greedy, rep.Sub);
        var prefix = new List<Hir>(tail.Count + 1);
        if (rest is not HirEmpty) prefix.Add(rest);
        tail.Reverse();
        prefix.AddRange(tail);
        return (Hir.Concat(prefix), 0);
    }
}
