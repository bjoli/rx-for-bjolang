using System.Text;
using BjoRx.Syntax;

namespace BjoRx.Tests.Harness;

/// <summary>A span of bytes, start inclusive and end exclusive.</summary>
public readonly record struct Span(int Start, int End)
{
    public override string ToString() => $"{Start}..{End}";
}

/// <summary>
/// One expected match: the overall span, then each group's span or null when
/// the group took no part. A test without groups in its expectations has only
/// the overall span.
/// </summary>
public sealed record ExpectedMatch(IReadOnlyList<Span?> Groups)
{
    public Span Overall => Groups[0]!.Value;

    public override string ToString() =>
        Groups.Count == 1 ? Overall.ToString() : "[" + string.Join(", ", Groups.Select(g => g?.ToString() ?? "-")) + "]";
}

/// <summary>A test from the upstream TOML data that this engine can run.</summary>
public sealed record UpstreamTest(
    string File,
    string Name,
    string Pattern,
    Hir Hir,
    byte[] Haystack,
    IReadOnlyList<ExpectedMatch> Matches,
    bool Anchored,
    int? MatchLimit)
{
    /// <summary>Whether the expectations include group spans.</summary>
    public bool HasCaptures => Matches.Any(m => m.Groups.Count > 1);

    public string FullName => $"{File}/{Name}";
}

/// <summary>
/// Reads <c>testdata/</c>, applying regex-test's defaults, and sorts every
/// test into the ones this engine runs and the ones it skips, with the reason.
/// </summary>
public static class UpstreamTests
{
    public sealed record Loaded(List<UpstreamTest> Accepted, List<(string Test, string Reason)> Skipped);

    private static Loaded? _cache;

    public static string TestDataDirectory => Path.Combine(AppContext.BaseDirectory, "testdata");

    public static Loaded Load() => _cache ??= LoadUncached();

    private static Loaded LoadUncached()
    {
        var accepted = new List<UpstreamTest>();
        var skipped = new List<(string, string)>();
        var files = Directory.EnumerateFiles(TestDataDirectory, "*.toml", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal);
        foreach (var path in files)
        {
            string file = Path.GetRelativePath(TestDataDirectory, path).Replace('\\', '/').Replace(".toml", "");
            var doc = Toml.ReadArraysOfTables(path);
            if (!doc.TryGetValue("test", out var tests)) continue;
            foreach (var t in tests)
            {
                string name = $"{file}/{(string)t["name"]}";
                string? reason = Unsupported(t);
                if (reason is null)
                {
                    try
                    {
                        accepted.Add(Build(file, t));
                        continue;
                    }
                    catch (NotSupportedException e)
                    {
                        reason = e.Message;
                    }
                }
                skipped.Add((name, reason));
            }
        }
        return new Loaded(accepted, skipped);
    }

    /// <summary>Why a test's options put it outside this engine, or null.</summary>
    private static string? Unsupported(Dictionary<string, object> t)
    {
        if (t["regex"] is not string) return "regex sets";
        if (Get(t, "compiles", true) is false) return "compiles = false";
        if (Get(t, "unicode", true) is false) return "unicode = false";
        if (Get(t, "utf8", true) is false) return "utf8 = false";
        if (Get(t, "case-insensitive", false) is true) return "case-insensitive";
        if (Get(t, "search-kind", "leftmost") is not "leftmost") return $"search-kind = {t["search-kind"]}";
        if (Get(t, "match-kind", "leftmost-first") is not "leftmost-first") return $"match-kind = {t["match-kind"]}";
        if (t.ContainsKey("line-terminator")) return "line-terminator";
        if (t.ContainsKey("bounds")) return "bounds";
        return null;
    }

    private static object Get(Dictionary<string, object> t, string key, object fallback) =>
        t.TryGetValue(key, out var v) ? v : fallback;

    private static UpstreamTest Build(string file, Dictionary<string, object> t)
    {
        string pattern = (string)t["regex"];
        Hir hir;
        try
        {
            hir = TestParser.Parse(pattern);
        }
        catch (FormatException e)
        {
            // A test that expects to compile must parse; a failure here is a
            // bug in the test parser, reported as a skip so it shows up.
            throw new NotSupportedException($"test parser rejected it: {e.Message}");
        }

        string haystackText = (string)t["haystack"];
        byte[] haystack = Get(t, "unescape", false) is true
            ? Unescape(haystackText)
            : Encoding.UTF8.GetBytes(haystackText);

        var matches = ((List<object>)t["matches"]).Select(ParseMatch).ToList();
        int? limit = t.TryGetValue("match-limit", out var l) ? (int)(long)l : null;
        return new UpstreamTest(file, (string)t["name"], pattern, hir, haystack, matches,
            Get(t, "anchored", false) is true, limit);
    }

    /// <summary>
    /// A match is <c>[s, e]</c>, a list of groups <c>[[s, e], [], …]</c>, or
    /// <c>{ id = 0, span = [s, e] }</c>; the id only matters for regex sets.
    /// </summary>
    private static ExpectedMatch ParseMatch(object m)
    {
        if (m is Dictionary<string, object> table)
        {
            var key = table.ContainsKey("span") ? "span" : "offsets";
            return new ExpectedMatch([ParseSpan(table[key])]);
        }
        var list = (List<object>)m;
        if (list.Count == 2 && list[0] is long)
        {
            return new ExpectedMatch([ParseSpan(list)]);
        }
        return new ExpectedMatch(list.Select(g => ((List<object>)g).Count == 0 ? (Span?)null : ParseSpan(g)).ToList());
    }

    private static Span ParseSpan(object o)
    {
        var l = (List<object>)o;
        return new Span((int)(long)l[0], (int)(long)l[1]);
    }

    /// <summary>
    /// regex-test's unescaping: <c>\xNN</c> is that byte, which may make the
    /// haystack invalid UTF-8; <c>\n</c>, <c>\r</c>, <c>\t</c>, <c>\\</c> and
    /// a few others are what they are in Rust.
    /// </summary>
    private static byte[] Unescape(string s)
    {
        var bytes = new List<byte>();
        var utf8 = Encoding.UTF8;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length)
            {
                int len = char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? 2 : 1;
                bytes.AddRange(utf8.GetBytes(s.Substring(i, len)));
                i += len - 1;
                continue;
            }
            char e = s[++i];
            switch (e)
            {
                case 'x':
                    bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                    i += 2;
                    break;
                case 'n': bytes.Add((byte)'\n'); break;
                case 'r': bytes.Add((byte)'\r'); break;
                case 't': bytes.Add((byte)'\t'); break;
                case '0': bytes.Add(0); break;
                case '\\': bytes.Add((byte)'\\'); break;
                case '\'': bytes.Add((byte)'\''); break;
                case '"': bytes.Add((byte)'"'); break;
                default: throw new NotSupportedException($"haystack escape \\{e}");
            }
        }
        return bytes.ToArray();
    }
}
