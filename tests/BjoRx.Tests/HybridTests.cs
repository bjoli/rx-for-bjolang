using System.Text;
using BjoRx.Hybrid;
using BjoRx.Nfa;
using BjoRx.Search;
using BjoRx.Syntax;
using BjoRx.Tests.Harness;
using HybridRegex = BjoRx.Hybrid.Regex;

namespace BjoRx.Tests;

public static class HybridTests
{
    // Upstream blacklists it too: without NFA shrinking its minimum cache is
    // above the default capacity, so the DFA refuses to build.
    private const string TooBigForDefaultCache = "expensive/regression-many-repeat-no-stack-overflow";

    /// <summary>The smallest cache there is, so that searches clear it all the time.</summary>
    private static readonly DfaConfig Tiny = new() { CacheCapacity = 0, SkipCacheCapacityCheck = true };

    /// <summary>
    /// Every match, as the meta searcher will find them: with the lazy DFA,
    /// and with the PikeVM for any search the DFA gives up on.
    /// </summary>
    private static List<Match> FindAll(HybridRegex re, HybridRegex.Cache cache, PikeVM vm, PikeVM.Cache vmCache, Input input, ref int gaveUp)
    {
        var all = new List<Match>();
        var searcher = new Searcher(input);
        int g = 0;
        while (searcher.Advance(i =>
               {
                   if (re.TrySearch(cache, i, out var m)) return m;
                   g++;
                   return vm.Find(vmCache, i);
               }) is { } found)
        {
            all.Add(found);
        }
        gaveUp += g;
        return all;
    }

    private static void RunUpstream(string name, DfaConfig config, bool expectNoClears = false)
    {
        var loaded = UpstreamTests.Load();
        var failures = new List<string>();
        int gaveUp = 0, ran = 0;
        foreach (var t in loaded.Accepted)
        {
            if (t.FullName == TooBigForDefaultCache && !config.SkipCacheCapacityCheck) continue;
            ran++;
            List<string> actual;
            HybridRegex re;
            try
            {
                re = HybridRegex.Create(t.Hir, config);
                var vm = new PikeVM(Compiler.Compile(t.Hir));
                var cache = re.CreateCache();
                var input = new Input(t.Haystack) { Anchored = t.Anchored ? Anchored.Yes : Anchored.No };
                IEnumerable<Match> all = FindAll(re, cache, vm, vm.CreateCache(), input, ref gaveUp);
                if (t.MatchLimit is { } limit) all = all.Take(limit);
                actual = all.Select(m => m.ToString()).ToList();
                if (expectNoClears && (cache.Forward.ClearCount != 0 || cache.Reverse.ClearCount != 0))
                {
                    failures.Add($"{t.FullName}: /{t.Pattern}/ cleared the cache");
                }
            }
            catch (Exception e)
            {
                failures.Add($"{t.FullName}: /{t.Pattern}/ threw {e.GetType().Name}: {e.Message}");
                continue;
            }
            var expected = t.Matches.Select(m => m.Overall.ToString()).ToList();
            if (!expected.SequenceEqual(actual))
            {
                failures.Add($"{t.FullName}: /{t.Pattern}/ on {Convert.ToHexString(t.Haystack)}\n" +
                             $"      expected [{string.Join(" ", expected)}]\n      got      [{string.Join(" ", actual)}]");
            }
        }
        foreach (var f in failures.Take(40)) Console.WriteLine("    " + f);
        Console.WriteLine($"  lazy DFA ({name}): {ran - failures.Count}/{ran} upstream tests pass" +
                          (gaveUp > 0 ? $", {gaveUp} searches handed to the PikeVM" : ""));
        Check.Equal(0, failures.Count, "upstream failures");
    }

    [Test]
    public static void UpstreamTestsPass() => RunUpstream("default", new DfaConfig(), expectNoClears: true);

    [Test]
    public static void UpstreamTestsPassWithoutByteClasses() => RunUpstream("no byte classes", new DfaConfig { ByteClasses = false });

    [Test]
    public static void UpstreamTestsPassWithStartStatesTagged() => RunUpstream("specialized starts", new DfaConfig { SpecializeStartStates = true });

    [Test]
    public static void UpstreamTestsPassWithATinyCache() => RunUpstream("tiny cache", Tiny);

    [Test]
    public static void UpstreamTestsPassGivingUp() => RunUpstream("tiny cache, giving up", Tiny with { MinimumCacheClearCount = 0 });

    [Test]
    public static void UpstreamTestsPassWithTheMetaHeuristic() =>
        RunUpstream("tiny cache, meta's heuristic", Tiny with { MinimumCacheClearCount = 3, MinimumBytesPerState = 10 });

    private static HybridRegex Re(string pattern, DfaConfig? config = null) => HybridRegex.Create(TestParser.Parse(pattern), config);

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Test]
    public static void ForwardAndReverseHalves()
    {
        var re = Re("a+b");
        var cache = re.CreateCache();
        var input = new Input(Utf8("xxaaab"));
        Check.Equal(DfaResult.Found(6), re.Forward.TrySearchFwd(cache.Forward, input), "forward finds the end");
        Check.Equal(DfaResult.Found(2), re.Reverse.TrySearchRev(cache.Reverse, input with { Anchored = Anchored.Yes }), "reverse finds the start");
        Check.Equal(DfaResult.NoMatch, re.Forward.TrySearchFwd(cache.Forward, new Input(Utf8("aaa"))), "no b");
        var lazy = Re("a+?");
        var lc = lazy.CreateCache();
        Check.Equal(DfaResult.Found(1), lazy.Forward.TrySearchFwd(lc.Forward, new Input(Utf8("aaa"))), "lazy stops at the first");
        var greedy = Re("a+");
        var gc = greedy.CreateCache();
        Check.Equal(DfaResult.Found(1), greedy.Forward.TrySearchFwd(gc.Forward, new Input(Utf8("aaa")) with { Earliest = true }), "earliest stops at the first match seen");
        Check.Equal(DfaResult.Found(3), greedy.Forward.TrySearchFwd(gc.Forward, new Input(Utf8("aaa"))), "greedy goes on");
    }

    [Test]
    public static void AnchorsSeeTheWholeHaystack()
    {
        var hay = Utf8("abab");
        var start = Re(@"\Aab");
        var sc = start.CreateCache();
        Check.True(start.TrySearch(sc, new Input(hay), out var m) && m == new Match(0, 2), "at start");
        Check.True(start.TrySearch(sc, new Input(hay).WithSpan(2, 4), out m) && m is null, "\\A is the haystack's start, not the span's");
        var end = Re(@"ab\z");
        var ec = end.CreateCache();
        Check.True(end.TrySearch(ec, new Input(hay).WithSpan(0, 2), out m) && m is null, "\\z is the haystack's end");
        Check.True(end.TrySearch(ec, new Input(hay), out m) && m == new Match(2, 4), "at end");
        var both = Re(@"\A\z");
        var bc = both.CreateCache();
        Check.True(both.TrySearch(bc, new Input([]), out m) && m == new Match(0, 0), "empty haystack");
        Check.True(both.TrySearch(bc, new Input(hay), out m) && m is null, "non-empty haystack");
    }

    [Test]
    public static void EmptyMatchesNeverSplitAScalar()
    {
        var re = Re("a*");
        var cache = re.CreateCache();
        Check.SequenceEqual([new Match(0, 0), new Match(3, 3)], re.FindAll(cache, new Input(Utf8("☃"))), "a* on snowman");
        var rev = re.Reverse.TrySearchRev(cache.Reverse, new Input(Utf8("☃")).WithSpan(0, 2));
        Check.Equal(DfaResult.Found(0), rev, "a reverse search skips back to a boundary too");
    }

    [Test]
    public static void GivesUpAfterTooManyClears()
    {
        // Upstream's too_many_cache_resets_cause_quit: a pattern that needs
        // many states, a cache that holds few, and no clears allowed.
        var nfa = Compiler.Compile(TestParser.Parse("[aβ]{99}"), new CompilerConfig { WhichCaptures = WhichCaptures.None });
        var dfa = DFA.Create(nfa, Tiny with { MinimumCacheClearCount = 0 });
        var cache = dfa.CreateCache();
        var aaa = new Input(Utf8(new string('a', 101)));
        var first = dfa.TrySearchFwd(cache, aaa);
        Check.True(first.IsGaveUp, $"gives up on a's: {first}");
        // The states built so far stay, so the next search gets as far.
        Check.Equal(first, dfa.TrySearchFwd(cache, aaa), "the same progress again");
        var betas = new Input(Utf8(string.Concat(Enumerable.Repeat("β", 101))));
        var full = dfa.TrySearchFwd(cache, betas);
        Check.True(full.IsGaveUp && full.Offset < first.Offset, $"a full cache gives up on β's at once: {full}");
        dfa.ResetCache(cache);
        var fresh = dfa.TrySearchFwd(cache, betas);
        Check.True(fresh.IsGaveUp && fresh.Offset > full.Offset, $"a reset cache gets further: {fresh}");
    }

    [Test]
    public static void ClearsAndCarriesOn()
    {
        var nfa = Compiler.Compile(TestParser.Parse("[aβ]{99}"), new CompilerConfig { WhichCaptures = WhichCaptures.None });
        var dfa = DFA.Create(nfa, Tiny);
        var cache = dfa.CreateCache();
        Check.Equal(DfaResult.Found(99), dfa.TrySearchFwd(cache, new Input(Utf8(new string('a', 101)))), "found");
        Check.True(cache.ClearCount > 0, "the cache was cleared");
    }

    [Test]
    public static void CapacityBelowTheMinimumIsRejected()
    {
        var nfa = Compiler.Compile(TestParser.Parse("[aβ]{99}"), new CompilerConfig { WhichCaptures = WhichCaptures.None });
        Check.Throws<BuildException>(() => DFA.Create(nfa, new DfaConfig { CacheCapacity = 0 }), "capacity 0");
    }

    [Test]
    public static void SearchesDoNotAllocateOnceWarm()
    {
        var re = Re("[a-z]+[0-9]{2,4}|☃+");
        var cache = re.CreateCache();
        var hay = Utf8(string.Concat(Enumerable.Repeat("foo bar ☃☃ baz12 x123456 ", 50)));
        void Search()
        {
            var input = new Input(hay);
            while (re.TrySearch(cache, input, out var m) && m is { } found)
            {
                input = input with { Start = found.End == found.Start ? found.End + 1 : found.End };
            }
        }
        Search();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++) Search();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check.Equal(0L, allocated, "bytes allocated by warm searches");
    }

    private static List<Match> PikeVMAll(PikeVM vm, PikeVM.Cache cache, Input input) => vm.FindAll(cache, input).ToList();

    /// <summary>
    /// Random patterns, haystacks and spans, with the lazy DFA checked against
    /// the PikeVM: with the default cache, a tiny one that clears all the time,
    /// and a tiny one that gives up (the PikeVM then answers).
    /// </summary>
    [Test]
    public static void AgreesWithThePikeVM()
    {
        int cases = int.TryParse(Environment.GetEnvironmentVariable("BJORX_FUZZ_CASES"), out int n) ? n : 20_000;
        var rng = new Random(5);
        var failures = new List<string>();
        int compared = 0, gaveUp = 0, searches = 0;
        DfaConfig[] configs = [new DfaConfig(), Tiny, Tiny with { MinimumCacheClearCount = 2, MinimumBytesPerState = 1 }];
        for (int k = 0; k < cases && failures.Count < 10; k++)
        {
            var gen = new FuzzTests.Gen(rng);
            var hir = gen.Pattern(rng.Next(1, 5));
            var config = configs[k % configs.Length];
            HybridRegex re;
            PikeVM vm;
            try
            {
                re = HybridRegex.Create(hir, config);
                vm = new PikeVM(Compiler.Compile(hir));
            }
            catch (BuildException)
            {
                continue;
            }
            var cache = re.CreateCache();
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
                var expected = PikeVMAll(vm, vmCache, input);
                var actual = FindAll(re, cache, vm, vmCache, input, ref gaveUp);
                searches += actual.Count + 1;
                compared++;
                if (expected.SequenceEqual(actual)) continue;
                failures.Add($"/{hir}/{(input.Anchored == Anchored.Yes ? " anchored" : "")} span {input.Start}..{input.End} " +
                             $"on \"{Encoding.UTF8.GetString(hay).Replace("\n", "\\n")}\" ({Convert.ToHexString(hay)}), cache {config.CacheCapacity}\n" +
                             $"      PikeVM   [{string.Join(" ", expected)}]\n      lazy DFA [{string.Join(" ", actual)}]");
            }
        }
        foreach (var f in failures) Console.WriteLine("    " + f);
        Console.WriteLine($"  lazy DFA fuzz: {compared} haystacks compared with the PikeVM; of about {searches} searches, {gaveUp} handed over");
        Check.Equal(0, failures.Count, "disagreements");
    }
}
