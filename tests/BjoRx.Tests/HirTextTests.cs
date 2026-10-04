using BjoRx.Syntax;
using BjoRx.Tests.Harness;

namespace BjoRx.Tests;

public static class HirTextTests
{
    // The text has no group names, so a tree read back has none either.
    private static Hir WithoutNames(Hir hir) => hir switch
    {
        HirCapture c => Hir.Capture(c.Index, null, WithoutNames(c.Sub)),
        HirRepetition r => Hir.Repetition(r.Min, r.Max, r.Greedy, WithoutNames(r.Sub)),
        HirConcat c => Hir.Concat(c.Subs.Select(WithoutNames)),
        HirAlternation a => Hir.Alternation(a.Subs.Select(WithoutNames)),
        _ => hir,
    };

    [Test]
    public static void UpstreamPatternsRoundTrip()
    {
        var failures = new List<string>();
        foreach (var t in UpstreamTests.Load().Accepted)
        {
            string text = HirText.Write(t.Hir);
            Hir back = HirText.Parse(text);
            if (!back.Equals(WithoutNames(t.Hir)) || HirText.Write(back) != text)
            {
                failures.Add($"{t.FullName}: {t.Pattern} -> {text}");
            }
        }
        Check.True(failures.Count == 0, string.Join("\n", failures.Take(10)));
    }

    [Test]
    public static void Forms()
    {
        Check.Equal("(l 61 1F600)", HirText.Write(TestParser.Parse("a\\x{1F600}")), "literal");
        Check.Equal("(c 30-39 5F)", HirText.Write(TestParser.Parse("[0-9_]")), "class");
        Check.Equal("(r 1 - g (c 61-7A))", HirText.Write(TestParser.Parse("[a-z]+")), "greedy, unbounded");
        Check.Equal("(r 2 5 l (l 61))", HirText.Write(TestParser.Parse("a{2,5}?")), "lazy, bounded");
        Check.Equal("(s (A) (g 1 (a (l 61) (l 62 63))) (z))", HirText.Write(TestParser.Parse("\\A(a|bc)\\z")), "groups and anchors");
        Check.Equal(Hir.Fail(), HirText.Parse("(c)"), "an empty class never matches");
        Check.Equal(Hir.Literal("ab"), HirText.Parse("(s (l 61) (e) (l 62))"), "read through the smart constructors");
        Check.Equal(Hir.Literal("x"), HirText.Parse("  ( l  78 )  "), "spaces anywhere between tokens");
    }

    [Test]
    public static void Malformed()
    {
        string[] bad =
        [
            "", "(", "(q)", "(l D800)", "(l 110000)", "(l xyz)", "(c 39-30x)", "(r 2 1 g (e))",
            "(r 1 - x (e))", "(g 0 (e))", "(s (e)", "(e) (e)", "(r - 1 g (e))",
        ];
        foreach (var text in bad)
        {
            Check.Throws<FormatException>(() => HirText.Parse(text), $"'{text}'");
        }
    }
}
