using BjoRx.Syntax;
using BjoRx.Tests.Harness;

namespace BjoRx.Tests;

/// <summary>
/// The prefix halves of upstream's literal.rs tests, for the patterns the
/// test parser can write (no case folding, \b, \d or byte mode).
/// </summary>
public static class LiteralTests
{
    private static Seq Prefixes(string pattern, Extractor? extractor = null) =>
        (extractor ?? new Extractor()).Extract(TestParser.Parse(pattern));

    private static Literal E(string s) => Literal.Exact(s);
    private static Literal I(string s) => Literal.Inexact(s);
    private static Seq Seq(params Literal[] lits) => BjoRx.Syntax.Seq.Of(lits);
    private static Seq Exact(params string[] lits) => BjoRx.Syntax.Seq.Of(lits.Select(E));
    private static Seq Infinite => BjoRx.Syntax.Seq.Infinite();

    private static void Is(Seq expected, string pattern, Extractor? extractor = null) =>
        Check.Equal(expected, Prefixes(pattern, extractor), $"prefixes of /{pattern}/");

    [Test]
    public static void Literals()
    {
        Is(Exact("a"), "a");
        Is(Exact("aaaaa"), "aaaaa");
        Is(Exact("☃"), "☃");
        Is(Exact("☃☃☃☃☃"), "☃☃☃☃☃");
        const string letters = "ͱͳͷΐάέήίΰαβγδεζηθικλμνξοπρςστυφχψωϊϋ";
        Is(Exact(letters), letters);
    }

    [Test]
    public static void Classes()
    {
        Is(Exact("a", "b", "c"), "[abc]");
        Is(Exact("a1b", "a2b", "a3b"), "a[123]b");
        Is(Exact("δ", "ε"), "[εδ]");
    }

    [Test]
    public static void Looks()
    {
        Is(Exact("ab"), @"a\Ab");
        Is(Exact("ab"), @"a\zb");
        Is(Exact("ab"), "^ab");
        Is(Exact("ab"), "$ab");
        Is(Exact("ab"), "ab^");
        Is(Exact("ab"), "ab$");
        Is(Seq(I("aZ"), E("ab")), "^aZ*b");
    }

    [Test]
    public static void Repetitions()
    {
        Is(Exact("a", ""), "a?");
        Is(Exact("", "a"), "a??");
        Is(Seq(I("a"), E("")), "a*");
        Is(Seq(E(""), I("a")), "a*?");
        Is(Seq(I("a")), "a+");
        Is(Seq(I("a")), "(a+)+");
        Is(Exact("ab"), "aZ{0}b");
        Is(Exact("aZb", "ab"), "aZ?b");
        Is(Exact("ab", "aZb"), "aZ??b");
        Is(Seq(I("aZ"), E("ab")), "aZ*b");
        Is(Seq(E("ab"), I("aZ")), "aZ*?b");
        Is(Seq(I("aZ")), "aZ+b");
        Is(Seq(I("aZ")), "aZ+?b");
        Is(Exact("aZZb"), "aZ{2}b");
        Is(Seq(I("aZZ")), "aZ{2,3}b");
        Is(Exact("abc", ""), "(abc)?");
        Is(Exact("", "abc"), "(abc)??");
        Is(Seq(I("a"), E("b")), "a*b");
        Is(Seq(E("b"), I("a")), "a*?b");
        Is(Seq(I("ab")), "ab+");
        Is(Seq(I("a"), I("b")), "a*b+");
        Is(Seq(I("a"), I("b"), E("c")), "a*b*c");
        Is(Seq(I("a"), I("b"), E("c")), "(a+)?(b+)?c");
        Is(Seq(I("a"), I("b"), E("c")), "(a+|)(b+|)c");
        Is(Seq(I("a"), I("b"), I("c"), E("")), "a*b*c*");
        Is(Seq(I("a"), I("b"), I("c")), "a*b*c+");
        Is(Seq(I("a"), I("b")), "a*b+c");
        Is(Seq(I("a"), I("b")), "a*b+c*");
        Is(Seq(I("ab"), E("a")), "ab*");
        Is(Seq(I("ab"), E("ac")), "ab*c");
        Is(Seq(I("ab")), "ab+c");
        Is(Seq(I("z"), E("azb")), "z*azb");
        Is(Exact("aaa", "aab", "aba", "abb", "baa", "bab", "bba", "bbb"), "[ab]{3}");
        Is(Seq(I("aaa"), I("aab"), I("aba"), I("abb"), I("baa"), I("bab"), I("bba"), I("bbb")), "[ab]{3,4}");
    }

    [Test]
    public static void Concatenations()
    {
        Is(Exact("abcxyz"), "abc()xyz");
        Is(Exact("abcxyz"), "(abc)(xyz)");
        Is(Exact("abcmnoxyz"), "abc()mno()xyz");
        Is(Exact(), "abc[a&&b]xyz");
        Is(Exact("abcxyz"), "abc[a&&b]*xyz");
    }

    [Test]
    public static void Alternations()
    {
        Is(Exact("abc", "mno", "xyz"), "abc|mno|xyz");
        Is(Seq(E("abc"), I("mZ"), E("mo"), E("xyz")), "abc|mZ*o|xyz");
        Is(Exact("abc", "xyz"), "abc|M[a&&b]N|xyz");
        Is(Exact("abc", "MN", "xyz"), "abc|M[a&&b]*N|xyz");
        Is(Exact("aaa", "aaaaa"), "(?:|aa)aaa");
        Is(Seq(I("aaa"), E(""), I("aaaaa"), E("aa")), "(?:|aa)(?:aaa)*");
        Is(Seq(E(""), I("aaa"), E("aa"), I("aaaaa")), "(?:|aa)(?:aaa)*?");
        Is(Seq(E("a"), I("b"), E("")), "a|b*");
        Is(Seq(E("a"), I("b")), "a|b+");
        Is(Seq(I("a"), E("b"), E("c")), "a*b|c");
        Is(Seq(E("a"), E("b"), I("c"), E("")), "a|(?:b|c*)");
        Is(Seq(I("a"), I("b"), E("c"), I("a"), I("ab"), E("c")), "(a|b)*c|(a|ab)*c");
        Is(Exact("abef", "abgh", "cdef", "cdgh"), "(ab|cd)(ef|gh)");
        Is(Exact("abefij", "abefkl", "abghij", "abghkl", "cdefij", "cdefkl", "cdghij", "cdghkl"), "(ab|cd)(ef|gh)(ij|kl)");
        Is(Seq(E("abab")), "(ab){2}");
        Is(Seq(I("abab")), "(ab){2,3}");
        Is(Seq(I("abab")), "(ab){2,}");
    }

    [Test]
    public static void Impossible()
    {
        Is(Exact(), "[a&&b]");
        Is(Exact(), "a[a&&b]");
        Is(Exact(), "[a&&b]b");
        Is(Exact(), "a[a&&b]b");
        Is(Exact("a", "b"), "a|[a&&b]|b");
        Is(Exact("a", "b"), "a|c[a&&b]|b");
        Is(Exact("a", "b"), "a|[a&&b]d|b");
        Is(Exact("a", "b"), "a|c[a&&b]d|b");
        Is(Exact(""), "[a&&b]*");
        Is(Exact("MN"), "M[a&&b]*N");
    }

    [Test]
    public static void Anything()
    {
        Is(Infinite, ".");
        Is(Infinite, "(?s).");
        Is(Infinite, "[A-Za-z]");
        Is(Infinite, "[A-Z]");
        Is(Exact(""), "[A-Z]{0}");
        Is(Infinite, "[A-Z]?");
        Is(Infinite, "[A-Z]*");
        Is(Infinite, "[A-Z]+");
        Is(Seq(I("1")), "1[A-Z]");
        Is(Seq(I("1")), "1[A-Z]2");
        Is(Infinite, "[A-Z]+123");
        Is(Infinite, "[A-Z]+123[A-Z]+");
        Is(Infinite, "1|[A-Z]|3");
        Is(Seq(E("1"), I("2"), E("3")), "1|2[A-Z]|3");
        Is(Infinite, "1|[A-Z]2|3");
        Is(Seq(E("1"), I("2"), E("4")), "1|2[A-Z]3|4");
        Is(Infinite, "(?:|1)[A-Z]2");
        Is(Seq(I("a")), "a.z");
    }

    [Test]
    public static void AnythingWithSmallLimits()
    {
        var small = new Extractor { LimitTotal = 10 };
        Is(Seq(I("aaa"), I("aab"), I("aba"), I("abb"), I("baa"), I("bab"), I("bba"), I("bbb")), "[ab]{3}{3}", small);
        Is(Infinite, "ab|cd|ef|gh|ij|kl|mn|op|qr|st|uv|wx|yz", small);
    }

    [Test]
    public static void Empty()
    {
        Is(Exact(""), "");
        Is(Exact(""), "^");
        Is(Exact(""), "$");
    }

    [Test]
    public static void OddsAndEnds()
    {
        Is(Infinite, ".a");
        Is(Seq(I("a")), "a.");
        Is(Infinite, "a|.");
        Is(Infinite, ".|a");
        const string pat = "M[ou]'?am+[ae]r .*([AEae]l[- ])?[GKQ]h?[aeu]+([dtz][dhz]?)+af[iy]";
        Is(Seq(I("Mo'am"), I("Moam"), I("Mu'am"), I("Muam")), pat);
        Is(Seq(I("fn is_"), I("fn as_")), "fn is_([A-Z]+)|fn as_([A-Z]+)");
        Is(Seq(I("foo")), "foo[A-Z]+bar[A-Z]+quux");
        Is(Infinite, "[A-Z]+bar[A-Z]+");
        Is(Exact("Sherlock Holmes"), "^Sherlock Holmes|Sherlock Holmes$");
    }

    [Test]
    public static void CrazyRepeats()
    {
        Is(Seq(E("")), "(?:){4294967295}");
        Is(Seq(E("")), "(?:){64}{64}{64}{64}{64}{64}");
        Is(Seq(E("")), "x{0}{4294967295}");
        Is(Seq(E("")), "(?:|){4294967295}");
        Is(Seq(E("")), "(?:){8}{8}{8}{8}{8}{8}{8}{8}{8}{8}{8}{8}{8}{8}");
        Is(Seq(I(new string('a', 100))), "a{8}{8}{8}{8}{8}{8}{8}{8}{8}{8}{8}{8}{8}{8}");
    }

    private static Seq Optimized(params Literal[] lits)
    {
        var seq = Seq(lits);
        seq.OptimizeForPrefixByPreference();
        return seq;
    }

    [Test]
    public static void Optimize()
    {
        Check.Equal(Seq(I("foobar")), Optimized(E("foobarfoobar"), E("foobar"), E("foobarzfoobar"), E("foobarfoobar")), "common prefix");
        Check.Equal(Exact("abba", "akka", "abccba"), Optimized(E("abba"), E("akka"), E("abccba")), "few exact literals stay");
        Check.Equal(Seq(E("sam")), Optimized(E("sam"), E("samwise")), "a literal after its prefix goes");
        Check.True(!Optimized(E("foobarfoo"), E("foo"), E(""), E("foozfoo"), E("foofoo")).IsFinite, "an empty literal matches everywhere");
        Check.True(!Optimized(E("foobarfoo"), I("foo"), E(" "), E("foofoo")).IsFinite, "a space is too common");
    }

    // The suffix halves of the same upstream tests. Patterns whose suffixes
    // are the same as their prefixes (single literals, classes, impossible
    // classes, crazy repeats) are not repeated.

    private static readonly Extractor SuffixExtractor = new() { Kind = ExtractKind.Suffix };

    private static void Suffixes(Seq expected, string pattern, Extractor? extractor = null) =>
        Check.Equal(expected, (extractor ?? SuffixExtractor).Extract(TestParser.Parse(pattern)), $"suffixes of /{pattern}/");

    [Test]
    public static void SuffixesOfLooksAndRepetitions()
    {
        Suffixes(Exact("ab"), @"a\zb");
        Suffixes(Seq(I("Zb"), E("ab")), "^aZ*b");
        Suffixes(Exact("a", ""), "a?");
        Suffixes(Exact("", "a"), "a??");
        Suffixes(Seq(I("a"), E("")), "a*");
        Suffixes(Seq(E(""), I("a")), "a*?");
        Suffixes(Seq(I("a")), "a+");
        Suffixes(Seq(I("a")), "(a+)+");
        Suffixes(Exact("ab"), "aZ{0}b");
        Suffixes(Exact("aZb", "ab"), "aZ?b");
        Suffixes(Exact("ab", "aZb"), "aZ??b");
        Suffixes(Seq(I("Zb"), E("ab")), "aZ*b");
        Suffixes(Seq(E("ab"), I("Zb")), "aZ*?b");
        Suffixes(Seq(I("Zb")), "aZ+b");
        Suffixes(Seq(I("Zb")), "aZ+?b");
        Suffixes(Exact("aZZb"), "aZ{2}b");
        Suffixes(Seq(I("ZZb")), "aZ{2,3}b");
        Suffixes(Exact("abc", ""), "(abc)?");
        Suffixes(Exact("", "abc"), "(abc)??");
        Suffixes(Seq(I("ab"), E("b")), "a*b");
        Suffixes(Seq(E("b"), I("ab")), "a*?b");
        Suffixes(Seq(I("b")), "ab+");
        Suffixes(Seq(I("b")), "a*b+");
        Suffixes(Seq(I("bc"), I("ac"), E("c")), "a*b*c");
        Suffixes(Seq(I("bc"), I("ac"), E("c")), "(a+)?(b+)?c");
        Suffixes(Seq(I("bc"), I("ac"), E("c")), "(a+|)(b+|)c");
        Suffixes(Seq(I("c"), I("b"), I("a"), E("")), "a*b*c*");
        Suffixes(Seq(I("c")), "a*b*c+");
        Suffixes(Seq(I("bc")), "a*b+c");
        Suffixes(Seq(I("c"), I("b")), "a*b+c*");
        Suffixes(Seq(I("b"), E("a")), "ab*");
        Suffixes(Seq(I("bc"), E("ac")), "ab*c");
        Suffixes(Seq(I("bc")), "ab+c");
        Suffixes(Seq(I("zazb"), E("azb")), "z*azb");
        Suffixes(Exact("aaa", "aab", "aba", "abb", "baa", "bab", "bba", "bbb"), "[ab]{3}");
        Suffixes(Seq(I("aaa"), I("aab"), I("aba"), I("abb"), I("baa"), I("bab"), I("bba"), I("bbb")), "[ab]{3,4}");
    }

    [Test]
    public static void SuffixesOfConcatenationsAndAlternations()
    {
        Suffixes(Exact("abcxyz"), "abc()xyz");
        Suffixes(Exact("abcxyz"), "abc[a&&b]*xyz");
        Suffixes(Exact(), "abc[a&&b]xyz");
        Suffixes(Exact("abc", "mno", "xyz"), "abc|mno|xyz");
        Suffixes(Seq(E("abc"), I("Zo"), E("mo"), E("xyz")), "abc|mZ*o|xyz");
        Suffixes(Exact("abc", "xyz"), "abc|M[a&&b]N|xyz");
        Suffixes(Exact("abc", "MN", "xyz"), "abc|M[a&&b]*N|xyz");
        Suffixes(Exact("aaa", "aaaaa"), "(?:|aa)aaa");
        Suffixes(Seq(I("aaa"), E(""), E("aa")), "(?:|aa)(?:aaa)*");
        Suffixes(Seq(E(""), I("aaa"), E("aa")), "(?:|aa)(?:aaa)*?");
        Suffixes(Seq(E("a"), I("b"), E("")), "a|b*");
        Suffixes(Seq(E("a"), I("b")), "a|b+");
        Suffixes(Seq(I("ab"), E("b"), E("c")), "a*b|c");
        Suffixes(Seq(E("a"), E("b"), I("c"), E("")), "a|(?:b|c*)");
        Suffixes(Seq(I("ac"), I("bc"), E("c"), I("ac"), I("abc"), E("c")), "(a|b)*c|(a|ab)*c");
        Suffixes(Exact("abef", "abgh", "cdef", "cdgh"), "(ab|cd)(ef|gh)");
        Suffixes(Exact("abefij", "abefkl", "abghij", "abghkl", "cdefij", "cdefkl", "cdghij", "cdghkl"), "(ab|cd)(ef|gh)(ij|kl)");
        Suffixes(Seq(E("abab")), "(ab){2}");
        Suffixes(Seq(I("abab")), "(ab){2,3}");
        Suffixes(Seq(I("abab")), "(ab){2,}");
    }

    [Test]
    public static void SuffixesOfAnything()
    {
        Suffixes(Infinite, ".");
        Suffixes(Infinite, "[A-Z]+");
        Suffixes(Exact(""), "[A-Z]{0}");
        Suffixes(Infinite, "1[A-Z]");
        Suffixes(Seq(I("2")), "1[A-Z]2");
        Suffixes(Seq(I("123")), "[A-Z]+123");
        Suffixes(Infinite, "[A-Z]+123[A-Z]+");
        Suffixes(Infinite, "1|[A-Z]|3");
        Suffixes(Infinite, "1|2[A-Z]|3");
        Suffixes(Seq(E("1"), I("2"), E("3")), "1|[A-Z]2|3");
        Suffixes(Seq(E("1"), I("3"), E("4")), "1|2[A-Z]3|4");
        Suffixes(Seq(I("2")), "(?:|1)[A-Z]2");
        Suffixes(Seq(I("z")), "a.z");
        var small = new Extractor { Kind = ExtractKind.Suffix, LimitTotal = 10 };
        Suffixes(Seq(I("aaa"), I("aab"), I("aba"), I("abb"), I("baa"), I("bab"), I("bba"), I("bbb")), "[ab]{3}{3}", small);
        Suffixes(Infinite, "ab|cd|ef|gh|ij|kl|mn|op|qr|st|uv|wx|yz", small);
        Suffixes(Seq(I("a")), ".a");
        Suffixes(Infinite, "a.");
        const string pat = "M[ou]'?am+[ae]r .*([AEae]l[- ])?[GKQ]h?[aeu]+([dtz][dhz]?)+af[iy]";
        Suffixes(Seq(new[]
        {
            "ddafi", "ddafy", "dhafi", "dhafy", "dzafi", "dzafy", "dafi", "dafy", "tdafi", "tdafy", "thafi", "thafy",
            "tzafi", "tzafy", "tafi", "tafy", "zdafi", "zdafy", "zhafi", "zhafy", "zzafi", "zzafy", "zafi", "zafy",
        }.Select(I).ToArray()), pat);
        Suffixes(Infinite, "fn is_([A-Z]+)|fn as_([A-Z]+)");
        Suffixes(Seq(I("quux")), "foo[A-Z]+bar[A-Z]+quux");
    }

    private static Seq OptimizedSuffixes(params Literal[] lits)
    {
        var seq = Seq(lits);
        seq.OptimizeForSuffixByPreference();
        return seq;
    }

    [Test]
    public static void OptimizeSuffixes()
    {
        Check.Equal(Seq(I("foobar")), OptimizedSuffixes(E("foobarfoobar"), E("foobar"), E("foobarzfoobar"), E("foobarfoobar")), "common suffix");
        Check.Equal(Exact("abba", "akka", "abccba"), OptimizedSuffixes(E("abba"), E("akka"), E("abccba")), "few exact literals stay");
        Check.Equal(Seq(E("sam"), E("samwise")), OptimizedSuffixes(E("sam"), E("samwise")), "no preference trie for suffixes");
        Check.True(!OptimizedSuffixes(E("foobarfoo"), E("foo"), E(""), E("foozfoo"), E("foofoo")).IsFinite, "an empty literal");
    }
}
