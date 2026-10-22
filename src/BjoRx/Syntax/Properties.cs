// Ported from the Properties parts of regex-syntax/src/hir/mod.rs
// (rust-lang/regex). MIT OR Apache-2.0; see THIRD-PARTY.md.

namespace BjoRx.Syntax;

/// <summary>
/// Facts about an <see cref="Hir"/>, computed once when it is built from the
/// facts about its parts, so that every query is constant time.
/// </summary>
[NonUserCode]
public sealed record Properties
{
    /// <summary>
    /// The length in bytes of the shortest match, or null when nothing
    /// matches at all.
    /// </summary>
    public int? MinimumLength { get; init; }

    /// <summary>
    /// The length in bytes of the longest match, or null when nothing matches
    /// or there is no bound.
    /// </summary>
    public int? MaximumLength { get; init; }

    /// <summary>Every assertion that occurs anywhere.</summary>
    public LookSet LookSet { get; init; }

    /// <summary>The assertions every match passes before consuming any input.</summary>
    public LookSet LookSetPrefix { get; init; }

    /// <summary>The assertions every match passes after consuming all its input.</summary>
    public LookSet LookSetSuffix { get; init; }

    /// <summary>The assertions some match may pass before consuming any input.</summary>
    public LookSet LookSetPrefixAny { get; init; }

    /// <summary>The assertions some match may pass after consuming all its input.</summary>
    public LookSet LookSetSuffixAny { get; init; }

    /// <summary>
    /// True if every match is valid UTF-8. An empty match counts as valid even
    /// though it could, as bytes, fall inside an encoded scalar; keeping
    /// matches on scalar boundaries is the search routine's job.
    /// </summary>
    public bool IsUtf8 { get; init; }

    /// <summary>The number of capture groups, not counting the whole match.</summary>
    public int ExplicitCapturesLength { get; init; }

    /// <summary>
    /// The number of capture groups that take part in every match, or null if
    /// that depends on the match.
    /// </summary>
    public int? StaticExplicitCapturesLength { get; init; }

    /// <summary>A literal, or a concatenation of only literals.</summary>
    public bool IsLiteral { get; init; }

    /// <summary>A literal, or an alternation of literals.</summary>
    public bool IsAlternationLiteral { get; init; }

    internal static Properties ForEmpty() => new()
    {
        MinimumLength = 0,
        MaximumLength = 0,
        IsUtf8 = true,
        StaticExplicitCapturesLength = 0,
    };

    internal static Properties ForLiteral(byte[] bytes) => new()
    {
        MinimumLength = bytes.Length,
        MaximumLength = bytes.Length,
        IsUtf8 = System.Text.Unicode.Utf8.IsValid(bytes),
        StaticExplicitCapturesLength = 0,
        IsLiteral = true,
        IsAlternationLiteral = true,
    };

    internal static Properties ForClass(ScalarClass cls) => new()
    {
        MinimumLength = cls.MinimumLength,
        MaximumLength = cls.MaximumLength,
        IsUtf8 = true,
        StaticExplicitCapturesLength = 0,
    };

    internal static Properties ForLook(Look look)
    {
        var set = LookSet.Singleton(look);
        return new()
        {
            MinimumLength = 0,
            MaximumLength = 0,
            LookSet = set,
            LookSetPrefix = set,
            LookSetSuffix = set,
            LookSetPrefixAny = set,
            LookSetSuffixAny = set,
            IsUtf8 = true,
            StaticExplicitCapturesLength = 0,
        };
    }

    internal static Properties ForRepetition(uint min, uint? max, Hir sub)
    {
        var p = sub.Properties;
        int? minimumLength = p.MinimumLength is { } childMin ? SaturatingMul(childMin, min) : null;
        int? maximumLength = max is { } repMax && p.MaximumLength is { } childMax
            ? CheckedMul(childMax, repMax)
            : null;

        var props = new Properties
        {
            MinimumLength = minimumLength,
            MaximumLength = maximumLength,
            LookSet = p.LookSet,
            // A repetition that can match nothing does not require its sub's
            // assertions.
            LookSetPrefix = min > 0 ? p.LookSetPrefix : LookSet.Empty,
            LookSetSuffix = min > 0 ? p.LookSetSuffix : LookSet.Empty,
            LookSetPrefixAny = p.LookSetPrefixAny,
            LookSetSuffixAny = p.LookSetSuffixAny,
            IsUtf8 = p.IsUtf8,
            ExplicitCapturesLength = p.ExplicitCapturesLength,
            StaticExplicitCapturesLength = p.StaticExplicitCapturesLength,
        };
        // Repeating zero times takes no groups, so a sub with groups no longer
        // has a fixed count unless the repetition is always zero times.
        if (min == 0 && props.StaticExplicitCapturesLength is > 0)
        {
            props = props with { StaticExplicitCapturesLength = max == 0 ? 0 : null };
        }
        return props;
    }

    internal static Properties ForCapture(Hir sub)
    {
        var p = sub.Properties;
        return p with
        {
            ExplicitCapturesLength = SaturatingAdd(p.ExplicitCapturesLength, 1),
            StaticExplicitCapturesLength = p.StaticExplicitCapturesLength is { } n ? SaturatingAdd(n, 1) : null,
            IsLiteral = false,
            IsAlternationLiteral = false,
        };
    }

    internal static Properties ForConcat(IReadOnlyList<Hir> subs)
    {
        int? minimumLength = 0;
        int? maximumLength = 0;
        var lookSet = LookSet.Empty;
        bool utf8 = true;
        int explicitCaptures = 0;
        int? staticCaptures = 0;
        bool literal = true;
        bool alternationLiteral = true;

        foreach (var x in subs)
        {
            var p = x.Properties;
            lookSet = lookSet.Union(p.LookSet);
            utf8 = utf8 && p.IsUtf8;
            explicitCaptures = SaturatingAdd(explicitCaptures, p.ExplicitCapturesLength);
            staticCaptures = staticCaptures is { } s && p.StaticExplicitCapturesLength is { } t
                ? SaturatingAdd(s, t)
                : null;
            literal = literal && p.IsLiteral;
            alternationLiteral = alternationLiteral && p.IsAlternationLiteral;
            if (minimumLength is { } min)
            {
                minimumLength = p.MinimumLength is { } len ? SaturatingAdd(min, len) : null;
            }
            if (maximumLength is { } max)
            {
                maximumLength = p.MaximumLength is { } len ? CheckedAdd(max, len) : null;
            }
        }

        // The prefix assertions are those of the parts up to and including the
        // first that can consume input; the suffix likewise from the end.
        var prefix = LookSet.Empty;
        var prefixAny = LookSet.Empty;
        foreach (var x in subs)
        {
            prefix = prefix.Union(x.Properties.LookSetPrefix);
            prefixAny = prefixAny.Union(x.Properties.LookSetPrefixAny);
            if (x.Properties.MaximumLength is not 0) break;
        }
        var suffix = LookSet.Empty;
        var suffixAny = LookSet.Empty;
        for (int i = subs.Count - 1; i >= 0; i--)
        {
            var x = subs[i];
            suffix = suffix.Union(x.Properties.LookSetSuffix);
            suffixAny = suffixAny.Union(x.Properties.LookSetSuffixAny);
            if (x.Properties.MaximumLength is not 0) break;
        }

        return new Properties
        {
            MinimumLength = minimumLength,
            MaximumLength = maximumLength,
            LookSet = lookSet,
            LookSetPrefix = prefix,
            LookSetSuffix = suffix,
            LookSetPrefixAny = prefixAny,
            LookSetSuffixAny = suffixAny,
            IsUtf8 = utf8,
            ExplicitCapturesLength = explicitCaptures,
            StaticExplicitCapturesLength = staticCaptures,
            IsLiteral = literal,
            IsAlternationLiteral = alternationLiteral,
        };
    }

    /// <summary>
    /// The properties of an alternation of things with these properties. An
    /// alternation of nothing matches nothing.
    /// </summary>
    public static Properties Union(IReadOnlyList<Properties> alternatives)
    {
        var fix = alternatives.Count == 0 ? LookSet.Empty : LookSet.Full;
        int? minimumLength = null;
        int? maximumLength = null;
        var lookSet = LookSet.Empty;
        var prefix = fix;
        var suffix = fix;
        var prefixAny = LookSet.Empty;
        var suffixAny = LookSet.Empty;
        bool utf8 = true;
        int explicitCaptures = 0;
        int? staticCaptures = alternatives.Count == 0 ? null : alternatives[0].StaticExplicitCapturesLength;
        bool alternationLiteral = true;
        bool minPoisoned = false, maxPoisoned = false;

        foreach (var p in alternatives)
        {
            lookSet = lookSet.Union(p.LookSet);
            prefix = prefix.Intersect(p.LookSetPrefix);
            suffix = suffix.Intersect(p.LookSetSuffix);
            prefixAny = prefixAny.Union(p.LookSetPrefixAny);
            suffixAny = suffixAny.Union(p.LookSetSuffixAny);
            utf8 = utf8 && p.IsUtf8;
            explicitCaptures = SaturatingAdd(explicitCaptures, p.ExplicitCapturesLength);
            if (staticCaptures != p.StaticExplicitCapturesLength) staticCaptures = null;
            alternationLiteral = alternationLiteral && p.IsLiteral;
            if (!minPoisoned)
            {
                if (p.MinimumLength is { } xmin)
                {
                    if (minimumLength is not { } pmin || xmin < pmin) minimumLength = xmin;
                }
                else
                {
                    minimumLength = null;
                    minPoisoned = true;
                }
            }
            if (!maxPoisoned)
            {
                if (p.MaximumLength is { } xmax)
                {
                    if (maximumLength is not { } pmax || xmax > pmax) maximumLength = xmax;
                }
                else
                {
                    maximumLength = null;
                    maxPoisoned = true;
                }
            }
        }

        return new Properties
        {
            MinimumLength = minimumLength,
            MaximumLength = maximumLength,
            LookSet = lookSet,
            LookSetPrefix = prefix,
            LookSetSuffix = suffix,
            LookSetPrefixAny = prefixAny,
            LookSetSuffixAny = suffixAny,
            IsUtf8 = utf8,
            ExplicitCapturesLength = explicitCaptures,
            StaticExplicitCapturesLength = staticCaptures,
            IsAlternationLiteral = alternationLiteral,
        };
    }

    private static int SaturatingAdd(int a, int b) => (int)Math.Min((long)a + b, int.MaxValue);

    private static int SaturatingMul(int a, uint b) => (int)Math.Min((long)a * b, int.MaxValue);

    private static int? CheckedAdd(int a, int b) => (long)a + b is var r && r <= int.MaxValue ? (int)r : null;

    private static int? CheckedMul(int a, uint b) => (long)a * b is var r && r <= int.MaxValue ? (int)r : null;
}
