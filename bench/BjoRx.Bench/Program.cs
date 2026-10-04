using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using BjoRx.Search;
using BjoRx.Tests.Harness;
using MetaRegex = BjoRx.Meta.Regex;

namespace BjoRx.Bench;

/// <summary>
/// Times BjoRx's meta regex against .NET's engines on the same text: BjoRx
/// on UTF-8 bytes, .NET on the string. Each case counts every match (and
/// reads the groups when the pattern has some). Arguments filter cases by
/// name. Run in Release: <c>dotnet run -c Release --project bench/BjoRx.Bench</c>.
/// </summary>
public static class Program
{
    private sealed record Case(string Name, string Pattern, string Text, bool Groups = false);

    public static int Main(string[] args)
    {
        var english = Corpus.English(1 << 20);
        var mixed = Corpus.Mixed(1 << 20);
        var emoji = Corpus.Emoji(1 << 20);
        Case[] cases =
        [
            new("literal", "Sherlock Holmes", english),
            new("literal, rare", "zqxj", english),
            new("literal alternation", "Sherlock|Holmes|Watson|Irene|Adler", english),
            new("no match, no literal", "[A-Z][0-9]{5}[a-z]", english),
            new("words ending in ing", "[a-z]+ing", english),
            new("dates", "[0-9]{4}-[0-9]{2}-[0-9]{2}", english),
            new("identifiers", "[A-Za-z_][A-Za-z0-9_]*", english),
            new("numbers", "-?[0-9]+(?:\\.[0-9]+)?(?:[eE][-+]?[0-9]+)?", english),
            new("emails, groups", "([a-z]+)@([a-z]+)\\.com", english, Groups: true),
            // One match, at the end: the reverse search reads only that word.
            new("anchored at the end", "[a-z]+\\z", english + "end"),
            new("letters, non-ASCII", "[a-zA-Zà-ÿα-ωА-я]+", mixed),
            new("one Cyrillic word", "привет", mixed),
            new("emoji", "[😀-🙏]+", emoji),
            new("dates, groups", "([0-9]{4})-([0-9]{2})-([0-9]{2})", english, Groups: true),
        ];
        Console.WriteLine($"{"case",-26} {"BjoRx",16} {".NET NonBacktr.",16} {".NET backtr.",16} {".NET compiled",16}  matches  strategy");
        foreach (var c in cases.Where(c => args.Length == 0 || args.Any(a => c.Name.Contains(a, StringComparison.Ordinal))))
        {
            Run(c);
        }
        return 0;
    }

    private static void Run(Case c)
    {
        var hir = TestParser.Parse(c.Pattern);
        var bytes = Encoding.UTF8.GetBytes(c.Text);
        var re = MetaRegex.Create(hir);
        var cache = re.CreateCache();
        var caps = re.CreateCaptures();
        string dotnet = DotNetOracle.ToDotNetPattern(hir);
        const RegexOptions options = RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant;
        var nonBacktracking = new System.Text.RegularExpressions.Regex(dotnet, options | RegexOptions.NonBacktracking);
        var backtracking = new System.Text.RegularExpressions.Regex(dotnet, options);
        var compiled = new System.Text.RegularExpressions.Regex(dotnet, options | RegexOptions.Compiled);

        int bjoRx() => c.Groups ? CountCaptures(re, cache, caps, bytes) : Count(re, cache, bytes);
        int net(System.Text.RegularExpressions.Regex r) => c.Groups ? CountGroups(r, c.Text) : r.Count(c.Text);

        var results = new (double MBps, int Matches)[]
        {
            Time(bjoRx, bytes.Length),
            Time(() => net(nonBacktracking), bytes.Length),
            Time(() => net(backtracking), bytes.Length),
            Time(() => net(compiled), bytes.Length),
        };
        string counts = results.Select(r => r.Matches).Distinct().Count() == 1
            ? results[0].Matches.ToString()
            : string.Join("/", results.Select(r => r.Matches));
        // A search that reads a few bytes of a megabyte is below the clock's resolution.
        string Rate(double mbps) => double.IsInfinity(mbps) || mbps > 1e6 ? $"{"instant",16}" : $"{mbps,11:F0} MB/s";
        Console.WriteLine($"{c.Name,-26} " + string.Join(" ", results.Select(r => Rate(r.MBps))) + $"  {counts,7}  {re.StrategyName}");
    }

    private static int Count(MetaRegex re, BjoRx.Meta.Cache cache, byte[] bytes)
    {
        int n = 0;
        var searcher = new Searcher(new Input(bytes));
        while (searcher.Advance(i => re.Find(cache, i)) is not null) n++;
        return n;
    }

    private static int CountCaptures(MetaRegex re, BjoRx.Meta.Cache cache, Captures caps, byte[] bytes)
    {
        int n = 0;
        var searcher = new Searcher(new Input(bytes));
        while (searcher.Advance(i => re.Captures(cache, i, caps) ? caps.Overall : null) is not null)
        {
            // Read a group, as a caller would.
            if (caps.Get(1) is not null) n++;
        }
        return n;
    }

    private static int CountGroups(System.Text.RegularExpressions.Regex r, string text)
    {
        int n = 0;
        for (var m = r.Match(text); m.Success; m = m.NextMatch())
        {
            if (m.Groups[1].Success) n++;
        }
        return n;
    }

    /// <summary>Throughput in MB/s, the best of runs over about half a second, after a warm-up.</summary>
    private static (double, int) Time(Func<int> run, int bytes)
    {
        int matches = run();
        var best = TimeSpan.MaxValue;
        var total = Stopwatch.StartNew();
        for (int i = 0; i < 3 || total.ElapsedMilliseconds < 500; i++)
        {
            var sw = Stopwatch.StartNew();
            run();
            if (sw.Elapsed < best) best = sw.Elapsed;
            if (total.ElapsedMilliseconds > 5000) break;
        }
        return (bytes / best.TotalSeconds / 1e6, matches);
    }
}

/// <summary>Generated text, the same on every run.</summary>
internal static class Corpus
{
    private static readonly string[] Words =
    [
        "the", "of", "and", "to", "a", "in", "that", "it", "was", "he", "his", "is", "with", "as", "had",
        "for", "you", "not", "be", "her", "on", "at", "by", "which", "have", "or", "from", "this", "him",
        "but", "all", "she", "they", "were", "my", "are", "me", "one", "their", "so", "an", "said", "them",
        "we", "who", "would", "been", "will", "no", "when", "there", "if", "more", "out", "up", "into",
        "do", "any", "your", "what", "has", "man", "could", "other", "than", "our", "some", "very", "time",
        "upon", "about", "may", "its", "only", "now", "like", "little", "then", "can", "should", "made",
        "did", "us", "such", "great", "before", "must", "two", "these", "see", "know", "over", "much",
        "running", "thinking", "nothing", "evening", "morning", "something", "being", "going", "coming",
        "Sherlock", "Holmes", "Watson", "Baker", "Street", "London", "Irene", "Adler", "Moriarty",
    ];

    public static string English(int length)
    {
        var rng = new Random(1);
        var sb = new StringBuilder(length + 100);
        while (sb.Length < length)
        {
            int k = rng.Next(1000);
            if (k < 3) sb.Append($"{rng.Next(1800, 2100)}-{rng.Next(1, 13):D2}-{rng.Next(1, 29):D2}");
            else if (k < 8) sb.Append(Words[rng.Next(Words.Length)]).Append('@').Append(Words[rng.Next(Words.Length)]).Append(".com");
            else if (k < 20) sb.Append(rng.Next(100000) - 50000).Append('.').Append(rng.Next(100));
            else if (k < 23) sb.Append("Sherlock Holmes");
            else sb.Append(Words[rng.Next(Words.Length)]);
            sb.Append(rng.Next(12) == 0 ? ".\n" : " ");
        }
        return sb.ToString(0, length);
    }

    public static string Mixed(int length)
    {
        string[] words =
        [
            "och", "att", "är", "på", "för", "även", "många", "människor", "tänker", "på", "sjön",
            "και", "το", "είναι", "λόγος", "αλήθεια", "θάλασσα",
            "и", "в", "не", "что", "привет", "мир", "работа", "время",
            "the", "and", "naïve", "café", "jalapeño",
        ];
        var rng = new Random(2);
        var sb = new StringBuilder(length + 100);
        while (sb.Length < length) sb.Append(words[rng.Next(words.Length)]).Append(rng.Next(10) == 0 ? ", " : " ");
        return sb.ToString(0, length);
    }

    public static string Emoji(int length)
    {
        var rng = new Random(3);
        var sb = new StringBuilder(length + 100);
        while (sb.Length < length)
        {
            if (rng.Next(4) == 0)
            {
                for (int i = rng.Next(1, 4); i > 0; i--) sb.Append(char.ConvertFromUtf32(0x1F600 + rng.Next(0x50)));
            }
            else
            {
                sb.Append(Words[rng.Next(Words.Length)]);
            }
            sb.Append(' ');
        }
        // Do not cut a surrogate pair in half.
        int end = char.IsHighSurrogate(sb[length - 1]) ? length - 1 : length;
        return sb.ToString(0, end);
    }
}
