using BjoRx.Syntax;
using BjoRx.Tests.Harness;

namespace BjoRx.Tests;

public static class HarnessTests
{
    private static ScalarClass C(params (uint, uint)[] ranges) =>
        new(ranges.Select(r => new ScalarRange(r.Item1, r.Item2)));

    private static Hir P(string pattern) => TestParser.Parse(pattern);

    [Test]
    public static void ParsesLiteralsAndEscapes()
    {
        Check.Equal(Hir.Literal("abc"), P("abc"), "abc");
        Check.Equal(Hir.Literal("a.b"), P(@"a\.b"), "escaped dot");
        Check.Equal(Hir.Literal("\n\t☃"), P(@"\n\t\x{2603}"), "escapes");
        Check.Equal(Hir.Literal("é"), P(@"\xE9"), "\\xE9 is the scalar, not the byte");
        Check.Equal(Hir.Literal("😀"), P("😀"), "astral literal");
        Check.Equal(Hir.Empty(), P(""), "empty");
    }

    [Test]
    public static void ParsesClasses()
    {
        Check.Equal(Hir.Class(C(('a', 'z'))), P("[a-z]"), "range");
        Check.Equal(Hir.Class(C(('-', '-'), ('a', 'a'))), P("[a-]"), "trailing dash");
        Check.Equal(Hir.Class(C((']', ']'), ('a', 'a'))), P("[]a]"), "leading bracket");
        var notA = C(('a', 'a'));
        notA.Negate();
        Check.Equal(Hir.Class(notA), P("[^a]"), "negated");
        Check.Equal(Hir.Class(C(('a', 'c'), ('x', 'z'))), P("[a-c[x-z]]"), "nested");
        Check.Equal(Hir.Class(C(('d', 'f'))), P("[a-f&&d-z]"), "intersection");
        Check.Equal(Hir.Class(C(('a', 'c'))), P("[a-f--d-z]"), "difference");
        Check.Equal(Hir.Class(C(('0', '9'))), P("[[:digit:]]"), "posix");
        var dot = C((0, 0x10FFFF));
        dot.Difference(C(('\n', '\n')));
        Check.Equal(Hir.Class(dot), P("."), "dot");
        Check.Equal(Hir.Class(C((0, 0x10FFFF))), P("(?s)."), "dot with s");
        var crlfDot = dot.Clone();
        crlfDot.Difference(C(('\r', '\r')));
        Check.Equal(Hir.Class(crlfDot), P("(?R)."), "dot in CRLF mode");
    }

    [Test]
    public static void ParsesStructure()
    {
        var a = Hir.Literal("a");
        var b = Hir.Literal("b");
        Check.Equal(Hir.Capture(1, null, a), P("(a)"), "group");
        Check.Equal(Hir.Capture(1, "x", a), P("(?P<x>a)"), "named");
        Check.Equal(Hir.Capture(1, "x", a), P("(?<x>a)"), "named, short");
        Check.Equal(a, P("(?:a)"), "non-capturing");
        Check.Equal(
            Hir.Concat(Hir.Capture(1, null, Hir.Capture(2, null, a)), Hir.Capture(3, null, b)),
            P("((a))(b)"),
            "numbered in order of opening");
        Check.Equal(Hir.Alternation(Hir.Literal("ab"), Hir.Literal("cd")), P("ab|cd"), "alternation");
        Check.Equal(Hir.Alternation(a, Hir.Empty()), P("a|"), "empty branch");
        Check.Equal(Hir.Concat(Hir.Look(Look.Start), a, Hir.Look(Look.End)), P("^a$"), "^ and $ are \\A and \\z");
        Check.Equal(Hir.Concat(Hir.Look(Look.Start), a, Hir.Look(Look.End)), P(@"\Aa\z"), "\\A and \\z");
    }

    [Test]
    public static void ParsesRepetitions()
    {
        var a = Hir.Literal("a");
        Check.Equal(Hir.Repetition(0, null, true, a), P("a*"), "*");
        Check.Equal(Hir.Repetition(1, null, false, a), P("a+?"), "+?");
        Check.Equal(Hir.Repetition(2, 5, true, a), P("a{2,5}"), "{2,5}");
        Check.Equal(Hir.Repetition(2, null, true, a), P("a{2,}"), "{2,}");
        Check.Equal(Hir.Repetition(3, 3, true, a), P("a{3}"), "{3}");
        Check.Equal(Hir.Repetition(0, null, false, a), P("(?U)a*"), "swap greed");
        Check.Equal(Hir.Repetition(0, null, true, a), P("(?U)a*?"), "swap greed back");
        Check.Equal(Hir.Concat(Hir.Literal("a"), Hir.Repetition(0, null, true, Hir.Literal("b"))), P("ab*"), "binds to one scalar");
    }

    [Test]
    public static void RejectsWhatItCannotExpress()
    {
        Check.Throws<NotSupportedException>(() => P("(?i)a"), "case-insensitive");
        Check.Throws<NotSupportedException>(() => P("(?m)^a"), "multi-line");
        Check.Throws<NotSupportedException>(() => P(@"\bfoo"), "word boundary");
        Check.Throws<NotSupportedException>(() => P(@"\w+"), "Unicode class");
        Check.Throws<NotSupportedException>(() => P(@"[\d]"), "Unicode class in a class");
        Check.Throws<NotSupportedException>(() => P("(?-u)a"), "byte mode");
    }

    [Test]
    public static void RejectsBadPatterns()
    {
        Check.Throws<FormatException>(() => P("*a"), "leading *");
        Check.Throws<FormatException>(() => P("a**"), "doubled repetition");
        Check.Throws<FormatException>(() => P("(a"), "unclosed group");
        Check.Throws<FormatException>(() => P("a)"), "unopened group");
        Check.Throws<FormatException>(() => P("[a"), "unclosed class");
        Check.Throws<FormatException>(() => P("[z-a]"), "backwards range");
        Check.Throws<FormatException>(() => P("a{3,2}"), "backwards repetition");
        Check.Throws<FormatException>(() => P("(?=a)"), "look-ahead");
        Check.Throws<FormatException>(() => P("(?P<x>a)(?P<x>b)"), "duplicate name");
    }

    [Test]
    public static void TomlReadsTheShapesTheDataUses()
    {
        string path = Path.Combine(Path.GetTempPath(), "bjorx-toml-test.toml");
        File.WriteAllText(path, """
            # a comment
            [[test]]
            name = "one"
            regex = '''a'b'''
            haystack = "x\ny\u00e9"
            matches = [
              [0, 1],  # trailing comment
              { id = 0, span = [2, 3] },
            ]
            anchored = true

            [[test]]
            name = "two"
            regex = 'z'
            matches = [[[0, 1], []]]
            """);
        var doc = Toml.ReadArraysOfTables(path);
        var tests = doc["test"];
        Check.Equal(2, tests.Count, "tables");
        Check.Equal("a'b", (string)tests[0]["regex"], "triple-quoted literal");
        Check.Equal("x\nyé", (string)tests[0]["haystack"], "basic string escapes");
        Check.Equal(true, (bool)tests[0]["anchored"], "bool");
        Check.Equal(2, ((List<object>)tests[0]["matches"]).Count, "array with comments and trailing comma");
        Check.Equal(0L, (long)((Dictionary<string, object>)((List<object>)tests[0]["matches"])[1])["id"], "inline table");
    }

    [Test]
    public static void LoadsUpstreamTests()
    {
        var loaded = UpstreamTests.Load();
        Console.WriteLine($"  upstream tests: {loaded.Accepted.Count} runnable, {loaded.Skipped.Count} skipped");
        foreach (var group in loaded.Skipped.GroupBy(s => ReasonKind(s.Reason)).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"    {group.Count(),4}  {group.Key}");
        }
        var parserBugs = loaded.Skipped.Where(s => s.Reason.StartsWith("test parser rejected", StringComparison.Ordinal)).ToList();
        foreach (var (test, reason) in parserBugs) Console.WriteLine($"    rejected: {test}: {reason}");
        Check.True(loaded.Accepted.Count >= 400, $"only {loaded.Accepted.Count} runnable tests");
        Check.Equal(0, parserBugs.Count, "tests the parser should accept but rejected");
    }

    /// <summary>
    /// The .NET oracle against the upstream expectations. It checks the
    /// harness (parser, TOML, iteration) as much as the oracle, and every
    /// disagreement must be in <see cref="KnownOracleDifferences"/> with its reason.
    /// </summary>
    [Test]
    public static void DotNetOracleAgreesWithUpstream()
    {
        var loaded = UpstreamTests.Load();
        int ran = 0, invalidUtf8 = 0;
        var disagreements = new List<string>();
        foreach (var t in loaded.Accepted)
        {
            if (!System.Text.Unicode.Utf8.IsValid(t.Haystack))
            {
                invalidUtf8++;
                continue;
            }
            ran++;
            var oracle = new DotNetOracle(t.Hir, t.Anchored);
            var got = oracle.FindAll(t.Haystack, t.MatchLimit);
            // Expectations without groups are compared on the overall span only.
            var gotCompared = t.HasCaptures ? got : got.Select(m => new ExpectedMatch([m.Groups[0]])).ToList();
            var expected = t.Matches.Select(m => m.ToString()).ToList();
            var actual = gotCompared.Select(m => m.ToString()).ToList();
            if (!expected.SequenceEqual(actual))
            {
                disagreements.Add(t.FullName);
                if (!KnownOracleDifferences.ContainsKey(t.FullName))
                {
                    Console.WriteLine($"    {t.FullName}: /{t.Pattern}/ on {Show(t.Haystack)}");
                    Console.WriteLine($"      expected [{string.Join(" ", expected)}], .NET gave [{string.Join(" ", actual)}]");
                    Console.WriteLine($"      .NET pattern: {oracle.Pattern}");
                }
            }
        }
        Console.WriteLine($"  .NET oracle: {ran} compared, {invalidUtf8} skipped for invalid UTF-8, {disagreements.Count} disagree");
        var unexplained = disagreements.Where(d => !KnownOracleDifferences.ContainsKey(d)).ToList();
        var stale = KnownOracleDifferences.Keys.Where(k => !disagreements.Contains(k)).ToList();
        Check.Equal(0, unexplained.Count, "unexplained disagreements");
        Check.Equal(0, stale.Count, "known differences that no longer differ: " + string.Join(", ", stale));
    }

    /// <summary>
    /// Upstream tests on which .NET's answer differs from the crate's, and why.
    /// The engine follows the crate, so these are the cases where the oracle
    /// cannot be used.
    /// </summary>
    private static readonly Dictionary<string, string> KnownOracleDifferences = BuildKnownOracleDifferences();

    private static Dictionary<string, string> BuildKnownOracleDifferences()
    {
        // In (a*)* on "a", .NET goes round the outer loop once more on the
        // empty string and leaves group 1 at 1..1; the crate does not take an
        // iteration that matches nothing and leaves it at 0..1.
        const string emptyIteration =
            "a repeated group that can match empty: .NET takes one more, empty, iteration and overwrites the group";
        int[] nullsubexpr = [3, 5, 6, 7, 9, 10, 20, 22, 23, 24, 26, 27, 28, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 42, 65, 66, 69, 70];
        var known = new Dictionary<string, string>();
        foreach (int n in nullsubexpr) known[$"fowler/nullsubexpr/nullsubexpr{n}"] = emptyIteration;
        for (int n = 90; n <= 97; n++) known[$"fowler/repetition/repetition-expensive{n}"] = emptyIteration;
        return known;
    }

    private static string Show(byte[] haystack) =>
        "\"" + System.Text.Encoding.UTF8.GetString(haystack).Replace("\n", "\\n") + "\"";

    private static string ReasonKind(string reason) =>
        reason.StartsWith("test parser rejected", StringComparison.Ordinal) ? "test parser rejected" : reason;
}
