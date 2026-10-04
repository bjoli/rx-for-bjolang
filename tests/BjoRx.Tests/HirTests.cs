using BjoRx.Syntax;

namespace BjoRx.Tests;

/// <summary>The smart constructors and properties, from the examples in regex-syntax's docs.</summary>
public static class HirTests
{
    private static Hir Lit(string s) => Hir.Literal(s);

    private static Hir Cls(char a, char b) => Hir.Class(new ScalarClass([new ScalarRange(a, b)]));

    private static Hir Star(Hir sub, bool greedy = true) => Hir.Repetition(0, null, greedy, sub);

    private static Hir Plus(Hir sub) => Hir.Repetition(1, null, true, sub);

    private static Hir Opt(Hir sub) => Hir.Repetition(0, 1, true, sub);

    private static Hir Cap(uint i, Hir sub) => Hir.Capture(i, null, sub);

    [Test]
    public static void LiteralsConcatenateIntoOne()
    {
        var bytes = new[] { Hir.Literal([0xE2]), Hir.Literal([0x98]), Hir.Literal([0x83]) };
        Check.True(bytes.All(h => !h.Properties.IsUtf8), "each byte alone is not UTF-8");
        var concat = Hir.Concat(bytes);
        Check.True(concat.Properties.IsUtf8, "the three together are");
        Check.Equal(Lit("☃"), concat, "one literal");
    }

    [Test]
    public static void NestedConcatenationsFlatten()
    {
        var hir = Hir.Concat(
            Hir.Concat(Lit("a"), Lit("b"), Lit("c")),
            Hir.Concat(Lit("x"), Lit("y"), Lit("z")));
        Check.Equal(Lit("abcxyz"), hir, "flattened");
    }

    [Test]
    public static void EmptyPartsDisappear()
    {
        Check.Equal(Hir.Empty(), Hir.Concat(), "empty concat");
        Check.Equal(Lit("a"), Hir.Concat(Hir.Empty(), Lit("a"), Hir.Empty()), "empties dropped");
        Check.Equal(Hir.Empty(), Hir.Literal([]), "empty literal");
    }

    [Test]
    public static void AlternationOfScalarsIsAClass()
    {
        var hir = Hir.Alternation(Lit("a"), Lit("b"), Lit("c"), Lit("d"), Lit("e"), Lit("f"));
        Check.Equal(Cls('a', 'f'), hir, "a-f");
        Check.Equal(Cls('a', 'z'), Hir.Alternation(Cls('a', 'm'), Cls('n', 'z')), "classes merge");
    }

    [Test]
    public static void AlternationLiftsCommonPrefix()
    {
        var hir = Hir.Alternation(
            Hir.Concat(Lit("abc"), Cls('A', 'Z')),
            Hir.Concat(Lit("abc"), Cls('a', 'z')));
        var expected = Hir.Concat(Lit("abc"), Hir.Alternation(Cls('A', 'Z'), Cls('a', 'z')));
        Check.Equal(expected, hir, "lifted");
        // `Hir.Alternation` merges two classes into one, so the lifted form
        // is abc[A-Za-z].
        Check.Equal(Hir.Concat(Lit("abc"), Hir.Class(new ScalarClass([new('A', 'Z'), new('a', 'z')]))), hir, "merged");
    }

    [Test]
    public static void AlternationEdgeCases()
    {
        Check.Equal(Hir.Fail(), Hir.Alternation(), "empty alternation fails");
        Check.Equal(Lit("ab"), Hir.Alternation(Lit("ab")), "one branch");
        var nested = Hir.Alternation(Lit("ab"), Hir.Alternation(Lit("cd"), Lit("ef")));
        Check.True(nested is HirAlternation { Subs.Count: 3 }, "nested alternations flatten");
    }

    [Test]
    public static void RepetitionSimplifies()
    {
        Check.Equal(Hir.Empty(), Hir.Repetition(0, 0, true, Lit("a")), "a{0}");
        Check.Equal(Hir.Empty(), Hir.Repetition(0, 0, true, Hir.Fail()), "fail{0}");
        Check.Equal(Lit("a"), Hir.Repetition(1, 1, true, Lit("a")), "a{1}");
        var emptyStar = Star(Hir.Look(Look.Start));
        Check.True(emptyStar is HirRepetition { Min: 0, Max: 1 }, $"\\A* is at most once, got {emptyStar}");
        Check.Throws<ArgumentException>(() => Hir.Repetition(3, 2, true, Lit("a")), "max below min");
    }

    [Test]
    public static void ClassOfOneScalarIsALiteral()
    {
        Check.Equal(Lit("x"), Cls('x', 'x'), "single");
        Check.Equal(Hir.Fail(), Hir.Class(ScalarClass.Empty()), "empty is fail");
    }

    [Test]
    public static void LengthProperties()
    {
        // ab?c?
        var hir1 = Hir.Concat(Lit("a"), Opt(Lit("b")), Opt(Lit("c")));
        Check.Equal(1, hir1.Properties.MinimumLength, "ab?c? min");
        Check.Equal(3, hir1.Properties.MaximumLength, "ab?c? max");
        // [a&&b]
        var hir2 = Hir.Fail();
        Check.Equal(null, hir2.Properties.MinimumLength, "fail min");
        Check.Equal(null, hir2.Properties.MaximumLength, "fail max");
        // wxy?z?
        var hir3 = Hir.Concat(Lit("wx"), Opt(Lit("y")), Opt(Lit("z")));
        Check.Equal(2, hir3.Properties.MinimumLength, "wxy?z? min");
        Check.Equal(4, hir3.Properties.MaximumLength, "wxy?z? max");

        var unioned = Properties.Union([hir1.Properties, hir2.Properties, hir3.Properties]);
        Check.Equal(null, unioned.MinimumLength, "poisoned min");
        Check.Equal(null, unioned.MaximumLength, "poisoned max");

        var aplus = Plus(Lit("a"));
        Check.Equal(1, aplus.Properties.MinimumLength, "a+ min");
        Check.Equal(null, aplus.Properties.MaximumLength, "a+ max");
        var unioned2 = Properties.Union([hir1.Properties, aplus.Properties, hir3.Properties]);
        Check.Equal(1, unioned2.MinimumLength, "unbounded min");
        Check.Equal(null, unioned2.MaximumLength, "unbounded max");
    }

    [Test]
    public static void CaptureCounts()
    {
        int? Static(Hir h) => h.Properties.StaticExplicitCapturesLength;
        var a = Lit("a");
        var b = Lit("b");
        Check.Equal(0, Lit("a").Properties.ExplicitCapturesLength, "a");
        Check.Equal(1, Cap(1, a).Properties.ExplicitCapturesLength, "(a)");

        Check.Equal(0, Static(a), "a");
        Check.Equal(1, Static(Cap(1, a)), "(a)");
        Check.Equal(1, Static(Hir.Alternation(Cap(1, a), Cap(2, b))), "(a)|(b)");
        Check.Equal(
            2,
            Static(Hir.Alternation(Hir.Concat(Cap(1, a), Cap(2, b)), Hir.Concat(Cap(3, Lit("c")), Cap(4, Lit("d"))))),
            "(a)(b)|(c)(d)");
        Check.Equal(null, Static(Hir.Alternation(Cap(1, a), b)), "(a)|b");
        Check.Equal(null, Static(Hir.Alternation(a, Cap(1, b))), "a|(b)");
        Check.Equal(null, Static(Star(Cap(1, b))), "(b)*");
        Check.Equal(1, Static(Plus(Cap(1, b))), "(b)+");
    }

    [Test]
    public static void Anchors()
    {
        var start = Hir.Look(Look.Start);
        var end = Hir.Look(Look.End);
        var anchored = Hir.Concat(start, Plus(Lit("a")), end);
        Check.True(anchored.Properties.LookSetPrefix.Contains(Look.Start), "prefix \\A");
        Check.True(anchored.Properties.LookSetSuffix.Contains(Look.End), "suffix \\z");

        var maybe = Hir.Alternation(Hir.Concat(start, Lit("a")), Lit("b"));
        Check.True(!maybe.Properties.LookSetPrefix.Contains(Look.Start), "not every branch is anchored");
        Check.True(maybe.Properties.LookSetPrefixAny.Contains(Look.Start), "but one may be");

        var optional = Hir.Concat(Opt(start), Lit("a"));
        Check.True(!optional.Properties.LookSetPrefix.Contains(Look.Start), "an optional anchor does not anchor");

        Check.Equal("A", LookSet.Singleton(Look.Start).ToString(), "debug");
        Check.Equal("∅", LookSet.Empty.ToString(), "empty debug");
        Check.SequenceEqual([Look.Start, Look.End], LookSet.Full.Iterate(), "iterate");
    }

    [Test]
    public static void LiteralProperties()
    {
        Check.True(Lit("foo").Properties.IsLiteral, "foo");
        Check.True(!Plus(Lit("f")).Properties.IsLiteral, "f+");
        Check.True(!Cap(1, Lit("foo")).Properties.IsLiteral, "(foo)");
        Check.True(!Hir.Empty().Properties.IsLiteral, "empty");
        Check.True(Hir.Alternation(Lit("foo"), Lit("bar")).Properties.IsAlternationLiteral, "foo|bar");
        Check.True(!Hir.Alternation(Lit("foo"), Plus(Lit("bar"))).Properties.IsAlternationLiteral, "foo|bar+");
    }

    [Test]
    public static void DeepNestingDoesNotOverflow()
    {
        Hir h = Lit("a");
        for (int i = 0; i < 5000; i++) h = Cap((uint)i + 1, h);
        Check.Equal(5000, h.Properties.ExplicitCapturesLength, "deep captures");
    }
}
