using System.Text;
using BjoRx.Search;
using BjoRx.Tests.Harness;

namespace BjoRx.Tests;

public static class PrefilterTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static string? NameFor(params string[] needles) => Prefilter.Create(needles.Select(B).ToList())?.Name;

    [Test]
    public static void ChoosesAsUpstreamDoes()
    {
        Check.Equal("memchr", NameFor("a"), "one byte");
        Check.Equal("memchr2", NameFor("a", "b"), "two bytes");
        Check.Equal("memchr3", NameFor("a", "b", "c"), "three bytes");
        Check.Equal("byteset", NameFor("a", "b", "c", "d"), "four bytes");
        Check.Equal("memmem", NameFor("abc"), "one literal");
        Check.Equal("firstbytes", NameFor("ab", "c"), "several literals");
        Check.Equal(null, NameFor(), "none");
        Check.Equal(null, NameFor("a", ""), "an empty literal");
    }

    [Test]
    public static void FromPatterns()
    {
        string? Name(string pattern) => Prefilter.FromHirPrefix(TestParser.Parse(pattern))?.Name;
        Check.Equal("memmem", Name("foo[0-9]+"), "literal prefix");
        Check.Equal("firstbytes", Name("foo|bar"), "alternation");
        Check.Equal("memmem", Name("☃+"), "one scalar");
        Check.Equal("memchr", Name("qa|qb"), "a short common prefix of rare bytes becomes its first byte");
        Check.Equal("firstbytes", Name("aa|ab"), "unless the byte is common");
        Check.Equal(null, Name("[a-z]+"), "a class too big to list");
        Check.Equal(null, Name("a*"), "can match empty");
    }

    /// <summary>The leftmost position with a needle, and there the first needle in order.</summary>
    private static Match? Naive(byte[][] needles, byte[] hay, int start, int end, bool anchored)
    {
        for (int at = start; at <= end; at++)
        {
            foreach (var n in needles)
            {
                if (at + n.Length <= end && hay.AsSpan(at, n.Length).SequenceEqual(n)) return new Match(at, at + n.Length);
            }
            if (anchored) break;
        }
        return null;
    }

    [Test]
    public static void AgreesWithANaiveSearch()
    {
        var rng = new Random(7);
        const string alphabet = "abcé";
        string Word(int min, int max) => string.Concat(Enumerable.Range(0, rng.Next(min, max + 1)).Select(_ => alphabet[rng.Next(alphabet.Length)]));
        for (int k = 0; k < 5000; k++)
        {
            var needles = Enumerable.Range(0, rng.Next(1, 6)).Select(_ => B(Word(1, 3))).ToArray();
            var pre = Prefilter.Create(needles)!;
            var hay = B(Word(0, 12));
            int a = rng.Next(hay.Length + 1), b = rng.Next(hay.Length + 1);
            int start = Math.Min(a, b), end = Math.Max(a, b);
            string what = $"{pre.Name} [{string.Join(", ", needles.Select(Encoding.UTF8.GetString))}] in {Convert.ToHexString(hay)} {start}..{end}";
            Check.Equal(Naive(needles, hay, start, end, false), pre.Find(hay, start, end), "find " + what);
            Check.Equal(Naive(needles, hay, start, end, true), pre.Prefix(hay, start, end), "prefix " + what);
        }
    }
}
