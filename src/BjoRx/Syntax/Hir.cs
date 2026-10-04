// Ported from regex-syntax/src/hir/mod.rs (rust-lang/regex). MIT OR
// Apache-2.0; see THIRD-PARTY.md.

using System.Text;

namespace BjoRx.Syntax;

/// <summary>
/// A regular expression after parsing: the structure the NFA compiler reads.
/// </summary>
///
/// <remarks>
/// <para>
/// Build one only through the static constructors. They simplify as they go,
/// as upstream's smart constructors do: <c>a{1}</c> is <c>a</c>, nested
/// concatenations are flattened, adjacent literals are merged, an alternation
/// of single scalars becomes a class. The NFA compiler relies on that shape.
/// </para>
/// <para>
/// Only what <c>(std rx)</c> can express is here: classes are always sets of
/// scalars (no byte classes), and the only assertions are <see cref="Look.Start"/>
/// and <see cref="Look.End"/>. A literal is bytes, as upstream, so that adjacent
/// literals can be merged; one built from scalars is always valid UTF-8.
/// </para>
/// </remarks>
public abstract class Hir : IEquatable<Hir>
{
    public Properties Properties { get; }

    private protected Hir(Properties properties)
    {
        Properties = properties;
    }

    // --- Smart constructors -------------------------------------------------

    /// <summary>Matches the empty string, everywhere.</summary>
    public static Hir Empty() => HirEmpty.Instance;

    /// <summary>
    /// Matches nothing. Represented as an empty class, which is what every
    /// way of writing "cannot match" normalizes to.
    /// </summary>
    public static Hir Fail() => new HirClass(ScalarClass.Empty());

    public static Hir Literal(ReadOnlySpan<byte> bytes) =>
        bytes.IsEmpty ? Empty() : new HirLiteral(bytes.ToArray());

    /// <summary>The UTF-8 encoding of <paramref name="text"/> as a literal.</summary>
    public static Hir Literal(string text) => Literal(Encoding.UTF8.GetBytes(text));

    public static Hir Scalar(uint scalar)
    {
        Span<byte> buf = stackalloc byte[4];
        int n = new Rune(scalar).EncodeToUtf8(buf);
        return Literal(buf[..n]);
    }

    public static Hir Class(ScalarClass cls)
    {
        if (cls.IsEmpty) return Fail();
        if (cls.Literal() is { } bytes) return Literal(bytes);
        return new HirClass(cls.Clone());
    }

    public static Hir Look(Look look) => new HirLook(look);

    public static Hir Repetition(uint min, uint? max, bool greedy, Hir sub)
    {
        if (max is { } m && m < min)
        {
            throw new ArgumentException($"repetition {{{min},{m}}} has a maximum below its minimum");
        }
        // A sub that can only match the empty string is matched at most once.
        if (sub.Properties.MaximumLength == 0)
        {
            min = Math.Min(min, 1);
            max = max is { } mx ? Math.Min(mx, 1) : 1;
        }
        // a{0} is empty even when `a` matches nothing, and a{1} is a.
        if (min == 0 && max == 0) return Empty();
        if (min == 1 && max == 1) return sub;
        return new HirRepetition(min, max, greedy, sub);
    }

    /// <summary>
    /// A capturing group. Non-capturing groups have no node: the tree's own
    /// structure already says what they grouped.
    /// </summary>
    public static Hir Capture(uint index, string? name, Hir sub) => new HirCapture(index, name, sub);

    public static Hir Concat(IEnumerable<Hir> subs)
    {
        var result = new List<Hir>();
        List<byte>? priorLiteral = null;

        void Add(Hir h)
        {
            switch (h)
            {
                case HirLiteral lit:
                    (priorLiteral ??= []).AddRange(lit.Bytes);
                    break;
                case HirEmpty:
                    break;
                default:
                    FlushLiteral();
                    result.Add(h);
                    break;
            }
        }

        void FlushLiteral()
        {
            if (priorLiteral is null) return;
            result.Add(Literal(priorLiteral.ToArray()));
            priorLiteral = null;
        }

        foreach (var sub in subs)
        {
            // Only one level: a concatenation is only ever built here, so its
            // own parts are flat already.
            if (sub is HirConcat concat)
            {
                foreach (var sub2 in concat.Subs) Add(sub2);
            }
            else
            {
                Add(sub);
            }
        }
        FlushLiteral();

        return result.Count switch
        {
            0 => Empty(),
            1 => result[0],
            _ => new HirConcat(result),
        };
    }

    public static Hir Concat(params Hir[] subs) => Concat((IEnumerable<Hir>)subs);

    public static Hir Alternation(IEnumerable<Hir> subs)
    {
        var flat = new List<Hir>();
        foreach (var sub in subs)
        {
            if (sub is HirAlternation alt) flat.AddRange(alt.Subs);
            else flat.Add(sub);
        }
        if (flat.Count == 0) return Fail();
        if (flat.Count == 1) return flat[0];

        // 'a|b|c' and alternations of classes are one class.
        if (SingletonScalars(flat) is { } singletons)
        {
            return Class(new ScalarClass(singletons.Select(c => new ScalarRange(c, c))));
        }
        if (UnionOfClasses(flat) is { } cls)
        {
            return Class(cls);
        }
        // Factoring out a common prefix narrows where the alternation branches.
        if (LiftCommonPrefix(flat) is { } lifted)
        {
            return lifted;
        }
        return new HirAlternation(flat);
    }

    public static Hir Alternation(params Hir[] subs) => Alternation((IEnumerable<Hir>)subs);

    /// <summary>
    /// The scalars, when every branch is a literal of exactly one scalar. No
    /// deduplication: the class does that.
    /// </summary>
    private static List<uint>? SingletonScalars(List<Hir> hirs)
    {
        var singletons = new List<uint>();
        foreach (var hir in hirs)
        {
            if (hir is not HirLiteral lit) return null;
            if (Rune.DecodeFromUtf8(lit.Bytes, out var rune, out int consumed) != System.Buffers.OperationStatus.Done)
            {
                return null;
            }
            if (consumed != lit.Bytes.Length) return null;
            singletons.Add((uint)rune.Value);
        }
        return singletons;
    }

    private static ScalarClass? UnionOfClasses(List<Hir> hirs)
    {
        var cls = ScalarClass.Empty();
        foreach (var hir in hirs)
        {
            if (hir is not HirClass c) return null;
            cls.Union(c.Set);
        }
        return cls;
    }

    /// <summary>
    /// <c>ab|ac</c> as <c>a(?:b|c)</c>, when every branch is a concatenation
    /// and they share leading parts. Like upstream it does not look inside
    /// literals, so <c>foofoo|foobar</c> is left alone.
    /// </summary>
    private static Hir? LiftCommonPrefix(List<Hir> hirs)
    {
        if (hirs.Count <= 1) return null;
        if (hirs[0] is not HirConcat first || first.Subs.Count == 0) return null;

        int prefixLength = first.Subs.Count;
        for (int k = 1; k < hirs.Count; k++)
        {
            if (hirs[k] is not HirConcat concat) return null;
            int common = 0;
            while (common < prefixLength && common < concat.Subs.Count && first.Subs[common].Equals(concat.Subs[common]))
            {
                common++;
            }
            prefixLength = common;
            if (prefixLength == 0) return null;
        }

        var suffixes = hirs.Select(h => Concat(((HirConcat)h).Subs.Skip(prefixLength))).ToList();
        var concatenated = first.Subs.Take(prefixLength).ToList();
        concatenated.Add(Alternation(suffixes));
        return Concat(concatenated);
    }

    // --- Equality -----------------------------------------------------------

    public abstract bool Equals(Hir? other);

    public override bool Equals(object? obj) => obj is Hir other && Equals(other);

    public abstract override int GetHashCode();

    public abstract override string ToString();
}

public sealed class HirEmpty : Hir
{
    internal static readonly HirEmpty Instance = new();

    private HirEmpty() : base(Properties.ForEmpty()) { }

    public override bool Equals(Hir? other) => other is HirEmpty;

    public override int GetHashCode() => 0;

    public override string ToString() => "(?:)";
}

/// <summary>A non-empty string of bytes.</summary>
public sealed class HirLiteral : Hir
{
    public byte[] Bytes { get; }

    internal HirLiteral(byte[] bytes) : base(Properties.ForLiteral(bytes))
    {
        Bytes = bytes;
    }

    public override bool Equals(Hir? other) => other is HirLiteral l && Bytes.AsSpan().SequenceEqual(l.Bytes);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }

    public override string ToString() =>
        Properties.IsUtf8 ? Escape(Encoding.UTF8.GetString(Bytes)) : "\\x" + Convert.ToHexString(Bytes);

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (var r in s.EnumerateRunes())
        {
            if (r.Value < 0x80 && "\\.+*?()|[]{}^$#&-~".Contains((char)r.Value)) sb.Append('\\');
            sb.Append(r.ToString());
        }
        return sb.ToString();
    }
}

/// <summary>One scalar from a set. Never empty in a well-formed tree except as <see cref="Hir.Fail"/>.</summary>
public sealed class HirClass : Hir
{
    public ScalarClass Set { get; }

    internal HirClass(ScalarClass cls) : base(Properties.ForClass(cls))
    {
        Set = cls;
    }

    public override bool Equals(Hir? other) => other is HirClass c && Set.Equals(c.Set);

    public override int GetHashCode() => Set.GetHashCode();

    public override string ToString() => Set.ToString();
}

public sealed class HirLook : Hir
{
    public Look Assertion { get; }

    internal HirLook(Look look) : base(Properties.ForLook(look))
    {
        Assertion = look;
    }

    public override bool Equals(Hir? other) => other is HirLook l && Assertion == l.Assertion;

    public override int GetHashCode() => (int)Assertion;

    public override string ToString() => "\\" + Assertion.AsChar();
}

/// <summary><c>sub{min,max}</c>; a null maximum is unbounded.</summary>
public sealed class HirRepetition : Hir
{
    public uint Min { get; }
    public uint? Max { get; }
    public bool Greedy { get; }
    public Hir Sub { get; }

    internal HirRepetition(uint min, uint? max, bool greedy, Hir sub)
        : base(Properties.ForRepetition(min, max, sub))
    {
        Min = min;
        Max = max;
        Greedy = greedy;
        Sub = sub;
    }

    /// <summary>The same repetition of another expression.</summary>
    public Hir With(Hir sub) => Repetition(Min, Max, Greedy, sub);

    public override bool Equals(Hir? other) =>
        other is HirRepetition r && Min == r.Min && Max == r.Max && Greedy == r.Greedy && Sub.Equals(r.Sub);

    public override int GetHashCode() => HashCode.Combine(Min, Max, Greedy, Sub);

    public override string ToString()
    {
        string op = (Min, Max) switch
        {
            (0, null) => "*",
            (1, null) => "+",
            (0, 1) => "?",
            (var n, null) => $"{{{n},}}",
            (var n, var m) when n == m => $"{{{n}}}",
            (var n, var m) => $"{{{n},{m}}}",
        };
        return $"(?:{Sub}){op}{(Greedy ? "" : "?")}";
    }
}

public sealed class HirCapture : Hir
{
    /// <summary>The group's number. Group 0 is the whole match, so this starts at 1.</summary>
    public uint Index { get; }
    public string? Name { get; }
    public Hir Sub { get; }

    internal HirCapture(uint index, string? name, Hir sub) : base(Properties.ForCapture(sub))
    {
        Index = index;
        Name = name;
        Sub = sub;
    }

    public override bool Equals(Hir? other) =>
        other is HirCapture c && Index == c.Index && Name == c.Name && Sub.Equals(c.Sub);

    public override int GetHashCode() => HashCode.Combine(Index, Name, Sub);

    public override string ToString() => Name is null ? $"({Sub})" : $"(?<{Name}>{Sub})";
}

/// <summary>Two or more parts in sequence, none of them a concatenation or empty.</summary>
public sealed class HirConcat : Hir
{
    public IReadOnlyList<Hir> Subs { get; }

    internal HirConcat(List<Hir> subs) : base(Properties.ForConcat(subs))
    {
        Subs = subs;
    }

    public override bool Equals(Hir? other) => other is HirConcat c && Subs.SequenceEqual(c.Subs);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var s in Subs) hash.Add(s);
        return hash.ToHashCode();
    }

    public override string ToString() => string.Concat(Subs.Select(s => s is HirAlternation ? $"(?:{s})" : s.ToString()));
}

/// <summary>Two or more branches, tried in order. None is itself an alternation.</summary>
public sealed class HirAlternation : Hir
{
    public IReadOnlyList<Hir> Subs { get; }

    internal HirAlternation(List<Hir> subs) : base(Properties.Union(subs.Select(s => s.Properties).ToList()))
    {
        Subs = subs;
    }

    public override bool Equals(Hir? other) => other is HirAlternation a && Subs.SequenceEqual(a.Subs);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var s in Subs) hash.Add(s);
        return hash.ToHashCode();
    }

    public override string ToString() => string.Join("|", Subs);
}
