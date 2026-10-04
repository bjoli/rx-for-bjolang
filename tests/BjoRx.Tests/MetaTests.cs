using System.Text;
using BjoRx.Meta;
using BjoRx.Nfa;
using BjoRx.Search;
using BjoRx.Tests.Harness;
using MetaRegex = BjoRx.Meta.Regex;

namespace BjoRx.Tests;

public static class MetaTests
{
    private static readonly (string Name, RegexConfig Config)[] Configs =
    [
        ("default", new RegexConfig()),
        ("no prefilter", new RegexConfig { AutoPrefilter = false }),
        ("PikeVM only", new RegexConfig { Hybrid = false }),
        // Small enough that the lazy DFA clears its cache and gives up often,
        // but above the minimum for most patterns.
        ("small cache", new RegexConfig { HybridCacheCapacity = 20_000 }),
    ];

    private static ExpectedMatch ToExpected(Captures c, int groups) =>
        new(Enumerable.Range(0, groups).Select(g => c.Get(g) is { } m ? new Span(m.Start, m.End) : (Span?)null).ToList());

    private static IEnumerable<ExpectedMatch> Matches(MetaRegex re, UpstreamTest t)
    {
        var input = new Input(t.Haystack) { Anchored = t.Anchored ? Anchored.Yes : Anchored.No };
        IEnumerable<ExpectedMatch> all = t.HasCaptures
            ? re.CapturesAll(input).Select(c => ToExpected(c, c.GroupLength))
            : re.FindAll(input).Select(m => new ExpectedMatch([new Span(m.Start, m.End)]));
        return t.MatchLimit is { } limit ? all.Take(limit) : all;
    }

    [Test]
    public static void UpstreamTestsPass()
    {
        var loaded = UpstreamTests.Load();
        var failures = new List<string>();
        foreach (var (name, config) in Configs)
        {
            var strategies = new Dictionary<string, int>();
            foreach (var t in loaded.Accepted)
            {
                List<string> actual;
                try
                {
                    var re = MetaRegex.Create(t.Hir, config);
                    strategies[re.StrategyName] = strategies.GetValueOrDefault(re.StrategyName) + 1;
                    actual = Matches(re, t).Select(m => m.ToString()).ToList();
                }
                catch (Exception e)
                {
                    failures.Add($"[{name}] {t.FullName}: /{t.Pattern}/ threw {e.GetType().Name}: {e.Message}");
                    continue;
                }
                var expected = t.Matches.Select(m => m.ToString()).ToList();
                if (!expected.SequenceEqual(actual))
                {
                    failures.Add($"[{name}] {t.FullName}: /{t.Pattern}/ on {Convert.ToHexString(t.Haystack)}\n" +
                                 $"      expected [{string.Join(" ", expected)}]\n      got      [{string.Join(" ", actual)}]");
                }
            }
            Console.WriteLine($"  meta ({name}): " + string.Join(", ", strategies.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}")));
        }
        foreach (var f in failures.Take(40)) Console.WriteLine("    " + f);
        Console.WriteLine($"  meta: {failures.Count} failures over {Configs.Length} configs × {loaded.Accepted.Count} upstream tests");
        Check.Equal(0, failures.Count, "upstream failures");
    }

    private static MetaRegex Re(string pattern, RegexConfig? config = null) => MetaRegex.Create(TestParser.Parse(pattern), config);

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Test]
    public static void ChoosesAStrategy()
    {
        Check.Equal("prefilter memmem", Re("foo").StrategyName, "a literal");
        Check.Equal("prefilter firstbytes", Re("foo|bar").StrategyName, "literals");
        Check.Equal("prefilter memchr3", Re("[abc]").StrategyName, "a small class");
        Check.Equal("core", Re("foo(bar)").StrategyName, "a group to report");
        Check.Equal("reverse anchored", Re(@"foo\z").StrategyName, "an assertion keeps the literal from being the whole regex");
        Check.Equal("core", Re(@"foo\z|bar").StrategyName, "not every match ends at the end");
        Check.Equal("core", Re("[a-z]+ing").StrategyName, "no prefix");
        Check.Equal("reverse anchored", Re(@"[a-z]+\z").StrategyName, "anchored at the end");
        Check.Equal("core", Re(@"\A[a-z]+\z").StrategyName, "anchored at both ends");
        Check.Equal("core (PikeVM)", Re("^.{1,2500}").StrategyName, "too big for the lazy DFA");
    }

    [Test]
    public static void ImpossibleSearchesAreCutShort()
    {
        var hay = Utf8("abcabc");
        Check.Equal(null, Re(@"\Aabc").Find(new Input(hay).WithSpan(3, 6)), "\\A after the start");
        Check.Equal(null, Re(@"abc\z").Find(new Input(hay).WithSpan(0, 3)), "\\z before the end");
        Check.Equal(null, Re("abcd").Find(new Input(hay)), "too short");
        Check.Equal(null, Re(@"\Aa(?:b|c)\z").Find(new Input(hay)), "too long");
        Check.Equal(new Match(3, 6), Re(@"[a-c]+\z").Find(new Input(hay).WithSpan(3, 6)), "reverse anchored in a span");
    }

    [Test]
    public static void CapturesFindGroupsInTheMatch()
    {
        var re = Re("([a-z]+)([0-9]+)?;");
        var caps = re.CreateCaptures();
        Check.True(re.Captures(new Input(Utf8("12 abc; xy9;")), caps), "matches");
        Check.Equal("[3..7, 3..6, -]", caps.ToString(), "groups");
        var all = re.CapturesAll(new Input(Utf8("12 abc; xy9;"))).Select(c => c.ToString()).ToList();
        Check.SequenceEqual(["[3..7, 3..6, -]", "[8..12, 8..10, 10..11]"], all, "all");
    }

    [Test]
    public static void WarmSearchesDoNotAllocate()
    {
        var hay = Utf8(string.Concat(Enumerable.Repeat("foo bar ☃☃ baz12 x123456 ", 50)));
        foreach (var pattern in new[] { "baz[0-9]+", "[a-z]+([0-9]{2,4})|☃+", @"[0-9]+ \z", "x12" })
        {
            var re = Re(pattern);
            var cache = re.CreateCache();
            var slots = new int[2 * re.GroupInfo.GroupLength];
            void Search(Span<int> s)
            {
                var input = new Input(hay);
                while (re.SearchSlots(cache, input, s))
                {
                    input = input with { Start = s[1] == s[0] ? s[1] + 1 : s[1] };
                }
                re.Find(input);
                re.IsMatch(input);
            }
            Search(slots);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++) Search(slots);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check.Equal(0L, allocated, $"bytes allocated by warm searches for /{pattern}/ ({re.StrategyName})");
        }
    }

    [Test]
    public static void FallsBackWhenTheLazyDfaGivesUp()
    {
        // A DFA state for each of the last 13 bits seen, 8192 in all: the
        // lazy DFA clears its small cache, gives up, and the PikeVM answers.
        var hir = TestParser.Parse("1[01]{12}2");
        var re = MetaRegex.Create(hir, new RegexConfig { HybridCacheCapacity = 40_000 });
        Check.Equal("core", re.StrategyName, "the lazy DFA is built");
        var rng = new Random(3);
        var hay = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 20_000).Select(_ => "010101010101012"[rng.Next(15)])));
        var cache = re.CreateCache();
        var vm = new PikeVM(Compiler.Compile(hir));
        var expected = vm.FindAll(vm.CreateCache(), new Input(hay)).ToList();
        var actual = new List<Match>();
        var searcher = new Searcher(new Input(hay));
        while (searcher.Advance(i => re.Find(cache, i)) is { } m) actual.Add(m);
        Check.SequenceEqual(expected, actual, "matches");
        Check.Equal(3, cache.Hybrid!.Forward.ClearCount, "the cache was cleared up to the limit");

        // The same lazy DFA as the meta regex's, searched alone, does give up.
        var dfaConfig = new Hybrid.DfaConfig { CacheCapacity = 40_000, MinimumCacheClearCount = 3, MinimumBytesPerState = 10 };
        var hybrid = Hybrid.Regex.FromNfas(
            Compiler.Compile(hir),
            Compiler.Compile(hir, new CompilerConfig { Reverse = true, WhichCaptures = WhichCaptures.None }),
            dfaConfig);
        var hcache = hybrid.CreateCache();
        int gaveUp = 0;
        var input = new Input(hay);
        while (true)
        {
            if (!hybrid.TrySearch(hcache, input, out var m))
            {
                gaveUp++;
                m = vm.Find(vm.CreateCache(), input);
            }
            if (m is not { } found) break;
            input = input with { Start = found.End };
        }
        Check.True(gaveUp > 0, "the lazy DFA gave up");
    }

    [Test]
    public static void SharedBetweenThreads()
    {
        var re = Re("([a-z]+)([0-9]+)");
        var hay = Utf8(string.Concat(Enumerable.Range(0, 200).Select(i => $"w{i} x{i}y ")));
        var expected = re.CapturesAll(new Input(hay)).Select(c => c.ToString()).ToList();
        var errors = 0;
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            // Nested use on one thread: the inner search must not get the
            // cache the outer iteration holds.
            foreach (var c in re.CapturesAll(new Input(hay)))
            {
                if (!re.IsMatch(new Input(hay).WithSpan(c.Overall!.Value.Start, c.Overall.Value.End))) Interlocked.Increment(ref errors);
            }
            var got = re.CapturesAll(new Input(hay)).Select(c => c.ToString()).ToList();
            if (!got.SequenceEqual(expected)) Interlocked.Increment(ref errors);
        });
        Check.Equal(0, errors, "threads that saw different matches");
    }

    /// <summary>
    /// Random patterns, haystacks and spans through the meta regex, checked
    /// against the PikeVM alone, groups included.
    /// </summary>
    [Test]
    public static void AgreesWithThePikeVM()
    {
        int cases = int.TryParse(Environment.GetEnvironmentVariable("BJORX_FUZZ_CASES"), out int n) ? n : 20_000;
        var rng = new Random(11);
        var failures = new List<string>();
        int compared = 0;
        var strategies = new Dictionary<string, int>();
        for (int k = 0; k < cases && failures.Count < 10; k++)
        {
            var gen = new FuzzTests.Gen(rng);
            var hir = rng.Next(4) == 0 ? LiteralPattern(rng) : gen.Pattern(rng.Next(1, 5));
            var (configName, config) = Configs[k % Configs.Length];
            MetaRegex re;
            PikeVM vm;
            try
            {
                re = MetaRegex.Create(hir, config);
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
                var hay = gen.Haystack();
                var input = new Input(hay) { Anchored = rng.Next(5) == 0 ? Anchored.Yes : Anchored.No };
                if (rng.Next(3) == 0)
                {
                    int a = rng.Next(hay.Length + 1), b = rng.Next(hay.Length + 1);
                    input = input.WithSpan(Math.Min(a, b), Math.Max(a, b));
                }
                int groups = re.GroupInfo.GroupLength;
                var expected = vm.CapturesAll(vmCache, input).Select(c => ToExpected(c, groups).ToString()).ToList();
                var actual = re.CapturesAll(input).Select(c => ToExpected(c, groups).ToString()).ToList();
                var found = re.FindAll(input).Select(m => new ExpectedMatch([new Span(m.Start, m.End)]).Overall.ToString()).ToList();
                var expectedSpans = vm.FindAll(vmCache, input).Select(m => m.ToString()).ToList();
                compared++;
                if (expected.SequenceEqual(actual) && expectedSpans.SequenceEqual(found)) continue;
                failures.Add($"[{configName}, {re.StrategyName}] /{hir}/{(input.Anchored == Anchored.Yes ? " anchored" : "")} span {input.Start}..{input.End} " +
                             $"on \"{Encoding.UTF8.GetString(hay).Replace("\n", "\\n")}\" ({Convert.ToHexString(hay)})\n" +
                             $"      PikeVM [{string.Join(" ", expected)}] / [{string.Join(" ", expectedSpans)}]\n" +
                             $"      meta   [{string.Join(" ", actual)}] / [{string.Join(" ", found)}]");
            }
        }
        foreach (var f in failures) Console.WriteLine("    " + f);
        Console.WriteLine($"  meta fuzz: {compared} haystacks compared with the PikeVM; strategies: " +
                          string.Join(", ", strategies.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}")));
        Check.Equal(0, failures.Count, "disagreements");
    }

    /// <summary>An alternation of short literals, so that the literal strategies get fuzzed too.</summary>
    private static Syntax.Hir LiteralPattern(Random rng)
    {
        const string alphabet = "abc☃";
        var lits = Enumerable.Range(0, rng.Next(1, 4))
            .Select(_ => Syntax.Hir.Literal(string.Concat(Enumerable.Range(0, rng.Next(1, 4)).Select(_ => alphabet[rng.Next(alphabet.Length)]))))
            .ToList();
        return Syntax.Hir.Alternation(lits);
    }
}
