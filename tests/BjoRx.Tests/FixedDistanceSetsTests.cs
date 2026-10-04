using System.Text;
using BjoRx.Nfa;
using BjoRx.Search;
using BjoRx.Syntax;
using BjoRx.Tests.Harness;
using HybridRegex = BjoRx.Hybrid.Regex;

namespace BjoRx.Tests;

public static class FixedDistanceSetsTests
{
    private static NFA Nfa(string pattern) => Compiler.Compile(TestParser.Parse(pattern));

    private static string Sets(string pattern) => string.Join(" ", FixedDistanceSets.Analyze(Nfa(pattern)));

    [Test]
    public static void AnalyzesBytesAtEachDistance()
    {
        Check.Equal("0:[41-5A] 1:[30-39] 2:[30-39]", Sets("[A-Z][0-9]{2}"), "classes");
        Check.Equal("0:[F0] 1:[9F] 2:[98-99] 3:[80-BF]", Sets("[😀-🙏]"), "UTF-8 of an astral range");
        // A match can end after one byte, so distance 1 need not exist.
        Check.Equal("0:[61-62]", Sets("a|bc"), "stops where a match can end");
        Check.Equal("", Sets("a*"), "matches empty");
        Check.Equal("0:[61] 1:[62] 2:[63]", Sets(@"\Aabc\z"), "assertions are passed");
        Check.Equal(FixedDistanceSets.MaxDistance, FixedDistanceSets.Analyze(Nfa("[a-z]{20}")).Count, "bounded");
    }

    [Test]
    public static void PicksTheRarestSet()
    {
        string? Pick(string pattern) => FixedDistanceSets.FromNfa(Nfa(pattern))?.ToString();
        Check.Equal("fixed-distance sets 1:[30-39] then 2:[30-39], 3:[30-39]", Pick("[A-Z][0-9]{5}[a-z]"), "digits over letters");
        Check.Equal("fixed-distance sets 4:[2D] then 7:[2D], 0:[30-39]", Pick("[0-9]{4}-[0-9]{2}-[0-9]{2}"), "the dash");
        Check.Equal(null, Pick("[a-z]+"), "lowercase letters are too common");
        Check.True(FixedDistanceSets.FromNfa(Nfa("[0-9]{4}-[0-9]{2}"))!.IsFast, "one rare byte is fast");
        Check.True(!FixedDistanceSets.FromNfa(Nfa("a[a-z]"))!.IsFast, "a common byte is not");
    }

    /// <summary>
    /// The prefilter never rejects where a match starts, and Find returns
    /// the first position that Prefix accepts.
    /// </summary>
    [Test]
    public static void NeverMissesAMatch()
    {
        int cases = int.TryParse(Environment.GetEnvironmentVariable("BJORX_FUZZ_CASES"), out int n) ? n : 20_000;
        var rng = new Random(17);
        int checkedMatches = 0, prefilters = 0;
        for (int k = 0; k < cases; k++)
        {
            var gen = new FuzzTests.Gen(rng);
            var hir = gen.Pattern(rng.Next(1, 5));
            NFA nfa;
            try
            {
                nfa = Compiler.Compile(hir);
            }
            catch (BuildException)
            {
                continue;
            }
            if (FixedDistanceSets.FromNfa(nfa) is not { } pre) continue;
            prefilters++;
            var vm = new PikeVM(nfa);
            var cache = vm.CreateCache();
            for (int h = 0; h < 4; h++)
            {
                var hay = gen.Haystack();
                int a = rng.Next(hay.Length + 1), b = rng.Next(hay.Length + 1);
                var input = new Input(hay).WithSpan(Math.Min(a, b), Math.Max(a, b));
                foreach (var m in vm.FindAll(cache, input))
                {
                    checkedMatches++;
                    Check.True(pre.Prefix(hay, m.Start, input.End) is not null, $"/{hir}/ ({pre}) rejects the match at {m} in {Convert.ToHexString(hay)}");
                }
                int? naive = null;
                for (int at = input.Start; at <= input.End && naive is null; at++)
                {
                    if (pre.Prefix(hay, at, input.End) is not null) naive = at;
                }
                Check.Equal(naive, pre.Find(hay, input.Start, input.End)?.Start, $"/{hir}/ ({pre}) find in {Convert.ToHexString(hay)} {input.Start}..{input.End}");
            }
        }
        Console.WriteLine($"  fixed-distance sets: {prefilters} prefilters, {checkedMatches} matches never rejected");
    }

    [Test]
    public static void FixedLengthSkipsTheReverseSearch()
    {
        Check.Equal(10, HybridRegex.FixedLengthOf(TestParser.Parse("[0-9]{4}-[0-9]{2}-[0-9]{2}")), "dates");
        Check.Equal(4, HybridRegex.FixedLengthOf(TestParser.Parse("[😀-🙏]")), "bytes, not scalars");
        Check.Equal(null, HybridRegex.FixedLengthOf(TestParser.Parse("[aé]")), "one or two bytes");
        var re = HybridRegex.Create(TestParser.Parse("[a-c][0-9]"));
        var cache = re.CreateCache();
        var found = re.FindAll(cache, new Input(Encoding.UTF8.GetBytes("xa1 b2 zc33"))).ToList();
        Check.SequenceEqual([new Match(1, 3), new Match(4, 6), new Match(8, 10)], found, "matches");
        // Only the reverse DFA's two sentinel states exist: it never ran.
        Check.Equal(2, cache.Reverse.States.Count, "reverse states");
    }
}
