using BjoRx.Nfa;
using BjoRx.Search;
using BjoRx.Tests.Harness;

namespace BjoRx.Tests;

public static class PikeVMTests
{
    /// <summary>Every match of <paramref name="t"/>, as the expectations are written.</summary>
    internal static List<string> RunUpstream(UpstreamTest t, Func<UpstreamTest, IEnumerable<ExpectedMatch>> engine) =>
        engine(t).Select(m => m.ToString()).ToList();

    internal static IEnumerable<ExpectedMatch> PikeVMMatches(UpstreamTest t)
    {
        var vm = new PikeVM(Compiler.Compile(t.Hir));
        var cache = vm.CreateCache();
        var input = new Input(t.Haystack) { Anchored = t.Anchored ? Anchored.Yes : Anchored.No };
        IEnumerable<ExpectedMatch> all = t.HasCaptures
            ? vm.CapturesAll(cache, input).Select(c =>
                new ExpectedMatch(Enumerable.Range(0, c.GroupLength)
                    .Select(g => c.Get(g) is { } m ? new Span(m.Start, m.End) : (Span?)null).ToList()))
            : vm.FindAll(cache, input).Select(m => new ExpectedMatch([new Span(m.Start, m.End)]));
        return t.MatchLimit is { } limit ? all.Take(limit) : all;
    }

    [Test]
    public static void UpstreamTestsPass()
    {
        var loaded = UpstreamTests.Load();
        var failures = new List<string>();
        foreach (var t in loaded.Accepted)
        {
            List<string> actual;
            try
            {
                actual = RunUpstream(t, PikeVMMatches);
            }
            catch (Exception e)
            {
                failures.Add($"{t.FullName}: /{t.Pattern}/ threw {e.GetType().Name}: {e.Message}");
                continue;
            }
            var expected = t.Matches.Select(m => m.ToString()).ToList();
            if (!expected.SequenceEqual(actual))
            {
                failures.Add($"{t.FullName}: /{t.Pattern}/ on {Convert.ToHexString(t.Haystack)}\n" +
                             $"      expected [{string.Join(" ", expected)}]\n      got      [{string.Join(" ", actual)}]");
            }
        }
        foreach (var f in failures.Take(40)) Console.WriteLine("    " + f);
        Console.WriteLine($"  PikeVM: {loaded.Accepted.Count - failures.Count}/{loaded.Accepted.Count} upstream tests pass");
        Check.Equal(0, failures.Count, "upstream failures");
    }

    private static PikeVM Vm(string pattern) => new(Compiler.Compile(TestParser.Parse(pattern)));

    private static List<Match> All(string pattern, string haystack) =>
        Vm(pattern) is var vm ? vm.FindAll(vm.CreateCache(), new Input(System.Text.Encoding.UTF8.GetBytes(haystack))).ToList() : [];

    [Test]
    public static void EmptyMatchesNeverSplitAScalar()
    {
        // a* on "☃" matches at 0 and 3, not inside the snowman's three bytes.
        Check.SequenceEqual([new Match(0, 0), new Match(3, 3)], All("a*", "☃"), "a* on snowman");
        Check.SequenceEqual([new Match(0, 0), new Match(1, 1), new Match(4, 4), new Match(5, 5)], All("", "a☃z"), "empty on a☃z");
    }

    [Test]
    public static void LeftmostFirstPreference()
    {
        Check.SequenceEqual([new Match(0, 1)], All("a|ab", "ab"), "first alternative wins");
        Check.SequenceEqual([new Match(0, 2)], All("ab|a", "ab"), "first alternative wins, longer");
        Check.SequenceEqual([new Match(0, 1), new Match(1, 2)], All("a+?", "aa"), "lazy");
        Check.SequenceEqual([new Match(0, 6)], All("zapper|z|zap", "zapper"), "trie keeps order");
        Check.SequenceEqual([new Match(0, 3)], All("zap|z|zapper", "zapper"), "trie keeps order, middle length first");
        Check.SequenceEqual([new Match(0, 1)], All("z|zapper|zap", "zapper"), "trie keeps order, short first");
    }

    [Test]
    public static void CapturesReportGroups()
    {
        var vm = Vm(@"(?P<y>\d)?(a)(b)?".Replace(@"\d", "[0-9]"));
        var caps = new Captures(vm.Nfa.GroupInfo.GroupLength);
        Check.True(vm.Captures(vm.CreateCache(), new Input("xab"u8.ToArray()), caps), "matches");
        Check.Equal("[1..3, -, 1..2, 2..3]", caps.ToString(), "groups");
        Check.Equal(1, vm.Nfa.GroupInfo.IndexOf("y"), "name");
    }

    [Test]
    public static void SpansAndAnchors()
    {
        var vm = Vm(@"\Aab");
        var cache = vm.CreateCache();
        var hay = "abab"u8.ToArray();
        Check.Equal(new Match(0, 2), vm.Find(cache, new Input(hay)), "at start");
        Check.Equal(null, vm.Find(cache, new Input(hay).WithSpan(2, 4)), "\\A is the haystack's start, not the span's");
        var end = Vm(@"ab\z");
        Check.Equal(null, end.Find(end.CreateCache(), new Input(hay).WithSpan(0, 2)), "\\z is the haystack's end");
        Check.Equal(new Match(2, 4), end.Find(end.CreateCache(), new Input(hay)), "at end");
    }

    [Test]
    public static void InvalidUtf8IsNeverMatchedByAClass()
    {
        // [^a] matches scalars, so a lone continuation byte is not one.
        var vm = Vm("[^a]");
        Check.Equal(null, vm.Find(vm.CreateCache(), new Input(new byte[] { 0x80, 0xFF })), "invalid bytes");
        Check.Equal(new Match(1, 4), vm.Find(vm.CreateCache(), new Input(new byte[] { 0x80, 0xE2, 0x98, 0x83 })), "skips to the snowman");
    }
}
