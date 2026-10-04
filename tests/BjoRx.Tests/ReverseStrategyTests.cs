using System.Text;
using BjoRx.Meta;
using BjoRx.Nfa;
using BjoRx.Search;
using BjoRx.Syntax;
using BjoRx.Tests.Harness;
using MetaRegex = BjoRx.Meta.Regex;

namespace BjoRx.Tests;

/// <summary>
/// The ReverseSuffix and ReverseInner strategies: upstream's tests of which
/// strategy a pattern gets and of suffix stripping, and a fuzzer aimed at
/// these strategies. Upstream writes <c>\w</c>, <c>\d</c>, <c>\s</c>, which
/// the test parser does not take; their ASCII versions stand in, and the
/// tests with <c>\b</c> or byte mode are left out.
/// </summary>
public static class ReverseStrategyTests
{
    private static string Ascii(string pattern) => pattern
        .Replace(@"\w", "[0-9A-Za-z_]")
        .Replace(@"\d", "[0-9]")
        .Replace(@"\s", @"[\t\n\x0B\x0C\r ]")
        .Replace(@"\pL", "A-Za-z");

    private static string StrategyOf(string pattern, RegexConfig? config = null) =>
        MetaRegex.Create(TestParser.Parse(Ascii(pattern)), config).StrategyName;

    private static void Strategy(string expected, string pattern, RegexConfig? config = null) =>
        Check.Equal(expected, StrategyOf(pattern, config), $"strategy for /{pattern}/");

    [Test]
    public static void PrefixLiterals()
    {
        Strategy("prefilter memchr", "a");
        Strategy("prefilter memchr2", "a|b");
        Strategy("prefilter memchr3", "a|b|c");
        Strategy("prefilter memmem", "Sherlock");
        Strategy("prefilter firstbytes", "Samwise|Gandalf|Holmes|Watson");
        Strategy("core", "a+");
        Strategy("core", "");
        Strategy("core", "(a)");
        Strategy("prefilter memchr", "(?:a)");
        Strategy("prefilter memchr", "(){0}a");
    }

    [Test]
    public static void CoreAndReverseAnchored()
    {
        Strategy("core", "[a-z]+");
        Strategy("core", @"\w+Holmes", new RegexConfig { AutoPrefilter = false });
        Strategy("core (PikeVM)", @"\w+Holmes", new RegexConfig { Hybrid = false });
        Strategy("reverse anchored", @"\w+Holmes$");
        Strategy("core", @"^\w+Holmes$");
        Strategy("core (PikeVM)", @"\w+Holmes$", new RegexConfig { Hybrid = false });
    }

    [Test]
    public static void ReverseSuffixChoice()
    {
        Strategy("reverse suffix", @"\d+XYZ");
        Strategy("reverse suffix", "[a-q][^u-z]{13}x");
        // A separator class that shares nothing with the rest or the literal.
        Strategy("reverse suffix", @"\w+\s+Holmes");
        Strategy("reverse suffix", @"\w+\d*\s+Holmes");
        Strategy("reverse suffix", @"(?:\w+|[.-]+)+\s+Holmes");
        Strategy("reverse suffix", @"\w+\sHolmes");
        Strategy("reverse suffix", "[a-z]+[0-9]+Holmes");
        Strategy("reverse suffix", @"(\w+)(\s+)Holmes");
        Strategy("reverse suffix", "[A-Z][0-9]XYZ");
        Strategy("reverse suffix", "[A-Z][0-9]{8}XYZ");
        Strategy("reverse suffix", "[a-z]shing");
        Strategy("reverse suffix", "[a-q][^u-z]{23}x");
        // The separator proofs are conservative.
        Strategy("core", @"\w+\w+Holmes");
        Strategy("core", @"\w+\s*Holmes");
        Strategy("core", "[a-z]+[0-9a]+xyz");
        Strategy("core", "[a-z]+[0-9]+a1");
        Strategy("core", "(?:.abc)?[a]+[b]+c");
        Strategy("core", @"\d+XYZ", new RegexConfig { AutoPrefilter = false });
        Strategy("core", @"^\d+XYZ");
        Strategy("core", "(?:[A-Za-z]ab)?b");
        Strategy("core", @"abc\w+XYZ");
        // An earlier match could end inside a later candidate.
        Strategy("core", ".abb|b");
        Strategy("core", ".bb|b");
        Strategy("core", "(?:[a-wyz]{3}|[a-wyz]).b");
        Strategy("core", "(?:[ac-z]{2}b[ac-z])?[ac-z]b");
        Strategy("core", "(?:[a-z]cb|c)[a-z]*b");
    }

    [Test]
    public static void ReverseInnerChoice()
    {
        Strategy("reverse inner", "(a|aa)b");
        Strategy("reverse inner", @"\w+@\w+");
        Strategy("reverse inner", @"(?P<email>[.\pL]+@(?P<domain>[.\pL]+))");
        // Upstream: reverse inner. Its inner literal shrinks to the one byte
        // @, so BjoRx prefers the fixed-distance sets, as for dates.
        Strategy("core", "[A-Z][0-9]@(foo|bar)");
        Strategy("reverse inner", @"\w+\s+Holmes\s+\w+");
        Strategy("reverse inner", @"\w+\d*\s+Holmes\s+\w+");
        Strategy("reverse inner", @"\w+\s+(?:Holmes|Watson)\s+\w+");
        Strategy("reverse inner", @"\s[A-Za-z]{0,12}ing\s");
        Strategy("reverse inner", @"\s[A-Za-z]*ing\s");
        Strategy("reverse inner", @"(\s)([A-Za-z]{0,12})ing\s");
        Strategy("core", @"\s[\sA-Za-z]{0,12}ing\s");
        Strategy("core", @"\s*[A-Za-z]{0,12}ing\s");
        Strategy("core", @"\w+@\w+", new RegexConfig { AutoPrefilter = false });
        Strategy("core", @"^\w+@\w+");
        Strategy("core", @"abc\w+@\w+");
        Strategy("core", @"\w+");
        Strategy("core", @"a.*@\w+");
        Strategy("core", "(?:[ac-z]{3}|[ac-z])(?:aba|baa)");
        Strategy("core", "(?:.abc)?[a]+[b]+c.*");
        // Not upstream: the inner literal is one byte, and the fixed-distance
        // sets around it (the dashes, digits) make a better search.
        Strategy("core", "[0-9]{4}-[0-9]{2}-[0-9]{2}");
    }

    private static void Strip(string pattern, string suffix, string? expected) =>
        Check.Equal(
            expected is null ? null : TestParser.Parse(expected),
            LiteralProofs.StripLiteralSuffix(TestParser.Parse(pattern), Encoding.UTF8.GetBytes(suffix)),
            $"/{pattern}/ without {suffix}");

    [Test]
    public static void StripsLiteralSuffixes()
    {
        Strip("foobar", "bar", "foo");
        Strip("foobar", "foobar", "");
        Strip("foobar", "", "foobar");
        Strip(".+foo(bar)", "foobar", ".+");
        Strip(".+(?:ab){2}cd", "ababcd", ".+");
        Strip("a(?:)cd", "acd", "");
        Strip("az{0}cd", "acd", "");
        Strip("az{2}cd", "acd", null);
        Strip("az{0,2}cd", "acd", null);
        Strip("x(?:ab){3}", "bab", "xaba");
        Strip("x(?:ab){2,}", "bab", "x(?:ab)*a");
        Strip("foobar", "baz", null);
        Strip("foobar", "xfoobar", null);
        Strip("foo[a-z]", "a", null);
        Strip("foo(xbar)", "foobar", null);
        Strip("x(?:ab)*cd", "abcd", null);
    }

    [Test]
    public static void SuffixProofs()
    {
        bool Fixed(string pattern, string suffix) =>
            LiteralProofs.StripLiteralSuffix(TestParser.Parse(Ascii(pattern)), Encoding.UTF8.GetBytes(suffix)) is { } p
            && LiteralProofs.HasFixedLength(p);
        bool Internal(string pattern, string suffix) =>
            LiteralProofs.StripLiteralSuffix(TestParser.Parse(Ascii(pattern)), Encoding.UTF8.GetBytes(suffix)) is { } p
            && LiteralProofs.CanContainLiteral(p, Encoding.UTF8.GetBytes(suffix));
        Check.True(!Internal(@"\d+XYZ", "XYZ"), "no XYZ in digits");
        Check.True(!Internal("[a-q][^u-z]{13}x", "x"), "no x before");
        Check.True(Fixed("(?:ab|cd)XYZ", "XYZ"), "fixed length");
        Check.True(Internal("[A-Z][0-9]XYZ", "XYZ"), "X, Y, Z are in [A-Z]");
        Check.True(Fixed("[A-Z][0-9]XYZ", "XYZ"), "fixed length, so safe anyway");
        Check.True(!Fixed("a{1,3}yy", "yy"), "variable length");
        Check.True(!Fixed("a*yy", "yy"), "unbounded");
    }

    /// <summary>
    /// Random patterns built around a literal, so that ReverseSuffix and
    /// ReverseInner get picked, checked against the PikeVM with groups, on
    /// haystacks full of the literal's pieces.
    /// </summary>
    [Test]
    public static void AgreeWithThePikeVM()
    {
        int cases = int.TryParse(Environment.GetEnvironmentVariable("BJORX_FUZZ_CASES"), out int n) ? n : 20_000;
        var rng = new Random(13);
        var failures = new List<string>();
        var strategies = new Dictionary<string, int>();
        int compared = 0;
        // Literals from a few scalars that also occur in the classes around
        // them, to make candidates overlap and fail.
        string[] pieces = ["x", "y", "xy", "yx", "xyz", "@", "é", "☃x"];
        uint[] alphabet = ['a', 'x', 'y', 'z', '@', 0xE9, 0x2603];
        Hir Atom()
        {
            var ranges = new List<ScalarRange>();
            for (int i = rng.Next(1, 4); i > 0; i--)
            {
                uint a = alphabet[rng.Next(alphabet.Length)];
                ranges.Add(new ScalarRange(a, rng.Next(3) == 0 ? alphabet[rng.Next(alphabet.Length)] : a));
            }
            var cls = new ScalarClass(ranges);
            if (rng.Next(4) == 0) cls.Negate();
            var atom = Hir.Class(cls);
            return rng.Next(3) switch
            {
                0 => atom,
                1 => Hir.Repetition((uint)rng.Next(0, 2), null, rng.Next(4) != 0, atom),
                _ => Hir.Repetition((uint)rng.Next(0, 3), (uint)rng.Next(3, 6), rng.Next(4) != 0, atom),
            };
        }
        Hir Part() => rng.Next(4) == 0 ? Hir.Capture(1, null, Hir.Concat(Atom(), Atom())) : Hir.Concat(Enumerable.Range(0, rng.Next(1, 3)).Select(_ => Atom()));
        for (int k = 0; k < cases && failures.Count < 10; k++)
        {
            var literal = Hir.Literal(pieces[rng.Next(pieces.Length)] + (rng.Next(2) == 0 ? pieces[rng.Next(pieces.Length)] : ""));
            var hir = rng.Next(2) == 0
                ? Hir.Concat(Part(), literal)
                : Hir.Concat(Part(), literal, Part());
            MetaRegex re;
            PikeVM vm;
            try
            {
                re = MetaRegex.Create(hir, k % 3 == 0 ? new RegexConfig { HybridCacheCapacity = 20_000 } : null);
                vm = new PikeVM(Compiler.Compile(hir));
            }
            catch (BuildException)
            {
                continue;
            }
            strategies[re.StrategyName] = strategies.GetValueOrDefault(re.StrategyName) + 1;
            var vmCache = vm.CreateCache();
            for (int h = 0; h < 4; h++)
            {
                var sb = new StringBuilder();
                for (int i = rng.Next(0, 16); i > 0; i--)
                {
                    sb.Append(rng.Next(3) == 0 ? pieces[rng.Next(pieces.Length)] : char.ConvertFromUtf32((int)alphabet[rng.Next(alphabet.Length)]));
                }
                var hay = Encoding.UTF8.GetBytes(sb.ToString());
                var input = new Input(hay);
                if (rng.Next(4) == 0)
                {
                    int a = rng.Next(hay.Length + 1), b = rng.Next(hay.Length + 1);
                    input = input.WithSpan(Math.Min(a, b), Math.Max(a, b));
                }
                var expected = vm.CapturesAll(vmCache, input).Select(c => c.ToString()).ToList();
                var actual = re.CapturesAll(input).Select(c => c.ToString()).ToList();
                var expectedSpans = vm.FindAll(vmCache, input).Select(m => m.ToString()).ToList();
                var actualSpans = re.FindAll(input).Select(m => m.ToString()).ToList();
                bool isMatch = re.IsMatch(input);
                compared++;
                if (expected.SequenceEqual(actual) && expectedSpans.SequenceEqual(actualSpans) && isMatch == (expectedSpans.Count > 0)) continue;
                failures.Add($"[{re.StrategyName}] /{hir}/ span {input.Start}..{input.End} on \"{sb}\" ({Convert.ToHexString(hay)})\n" +
                             $"      PikeVM [{string.Join(" ", expected)}] / [{string.Join(" ", expectedSpans)}]\n" +
                             $"      meta   [{string.Join(" ", actual)}] / [{string.Join(" ", actualSpans)}], is match {isMatch}");
            }
        }
        foreach (var f in failures) Console.WriteLine("    " + f);
        Console.WriteLine($"  reverse strategies fuzz: {compared} haystacks compared with the PikeVM; strategies: " +
                          string.Join(", ", strategies.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}")));
        Check.Equal(0, failures.Count, "disagreements");
    }
}
