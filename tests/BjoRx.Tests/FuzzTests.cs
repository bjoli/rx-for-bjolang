using System.Text;
using System.Text.RegularExpressions;
using BjoRx.Nfa;
using BjoRx.Search;
using BjoRx.Syntax;
using BjoRx.Tests.Harness;

namespace BjoRx.Tests;

/// <summary>
/// Random patterns and haystacks, run through the engine and the .NET
/// oracle. Set BJORX_FUZZ_CASES to run more than the default.
/// </summary>
public static class FuzzTests
{
    // A small alphabet, so that random patterns and haystacks meet often,
    // with one scalar of every UTF-8 length.
    private static readonly uint[] Alphabet = ['a', 'b', 'c', '\n', 0xE9, 0x2603, 0x1F600];

    internal sealed class Gen(Random rng)
    {
        private uint _nextGroup = 1;

        public Hir Pattern(int depth)
        {
            if (depth <= 0) return Atom();
            return rng.Next(10) switch
            {
                0 or 1 => Atom(),
                2 or 3 => Hir.Concat(Enumerable.Range(0, rng.Next(2, 4)).Select(_ => Pattern(depth - 1)).ToList()),
                4 => Hir.Alternation(Enumerable.Range(0, rng.Next(2, 4)).Select(_ => Pattern(depth - 1)).ToList()),
                5 or 6 => Repeat(Pattern(depth - 1)),
                7 => Hir.Capture(_nextGroup++, null, Pattern(depth - 1)),
                8 => Hir.Look(rng.Next(2) == 0 ? Look.Start : Look.End),
                _ => Hir.Empty(),
            };
        }

        private Hir Atom()
        {
            if (rng.Next(3) == 0) return Hir.Scalar(Alphabet[rng.Next(Alphabet.Length)]);
            var ranges = new List<ScalarRange>();
            for (int i = rng.Next(1, 3); i > 0; i--)
            {
                uint a = Alphabet[rng.Next(Alphabet.Length)];
                uint b = rng.Next(3) == 0 ? Alphabet[rng.Next(Alphabet.Length)] : a;
                ranges.Add(new ScalarRange(a, b));
            }
            var cls = new ScalarClass(ranges);
            if (rng.Next(4) == 0) cls.Negate();
            return Hir.Class(cls);
        }

        private Hir Repeat(Hir sub)
        {
            bool greedy = rng.Next(3) != 0;
            return rng.Next(5) switch
            {
                0 => Hir.Repetition(0, null, greedy, sub),
                1 => Hir.Repetition(1, null, greedy, sub),
                2 => Hir.Repetition(0, 1, greedy, sub),
                _ => Hir.Repetition((uint)rng.Next(0, 3) is var min ? min : 0, min + (uint)rng.Next(0, 3), greedy, sub),
            };
        }

        public byte[] Haystack()
        {
            var sb = new StringBuilder();
            for (int i = rng.Next(0, 9); i > 0; i--) sb.Append(char.ConvertFromUtf32((int)Alphabet[rng.Next(Alphabet.Length)]));
            return Encoding.UTF8.GetBytes(sb.ToString());
        }
    }

    /// <summary>True if some repetition in <paramref name="hir"/> satisfies <paramref name="p"/>.</summary>
    private static bool AnyRepetition(Hir hir, Func<HirRepetition, bool> p) => hir switch
    {
        HirRepetition r => p(r) || AnyRepetition(r.Sub, p),
        HirCapture c => AnyRepetition(c.Sub, p),
        HirConcat c => c.Subs.Any(s => AnyRepetition(s, p)),
        HirAlternation a => a.Subs.Any(s => AnyRepetition(s, p)),
        _ => false,
    };

    /// <summary>
    /// A loop whose body can match empty. Backtracking engines end a loop at
    /// an empty iteration; a Thompson NFA, which upstream and this engine are,
    /// just drops the path. So .NET can pick a different match — and in the
    /// simplest case, (a*)* on "a", the same match with one more empty
    /// iteration that overwrites the group.
    /// </summary>
    private static bool HasEmptyIteration(Hir hir) =>
        AnyRepetition(hir, r => r.Max is null or > 1 && r.Sub.Properties.MinimumLength == 0);

    /// <summary>
    /// A loop of a loop, as in (?:x{2,4})+. .NET's optimizer merges the two
    /// into one, x{2,} here: the same language but not the same leftmost-first
    /// match. Python, like the crate, gives 0..4 on five x's; .NET gives 0..5.
    /// </summary>
    private static bool HasNestedLoop(Hir hir) =>
        AnyRepetition(hir, r => r.Max is null or > 1 && Unwrap(r.Sub) is HirRepetition);

    private static Hir Unwrap(Hir hir) => hir is HirCapture c ? Unwrap(c.Sub) : hir;

    /// <summary>Every span the engine reports is on scalar boundaries.</summary>
    private static void CheckBoundaries(Hir hir, byte[] hay, Captures caps, List<string> failures)
    {
        for (int g = 0; g < caps.GroupLength; g++)
        {
            if (caps.Get(g) is { } m && (!Input.IsCharBoundary(hay, m.Start) || !Input.IsCharBoundary(hay, m.End)))
            {
                failures.Add($"/{hir}/ on {Encoding.UTF8.GetString(hay)}: group {g} at {m} splits a scalar");
            }
        }
    }

    [Test]
    public static void PikeVMAgreesWithDotNet()
    {
        int cases = int.TryParse(Environment.GetEnvironmentVariable("BJORX_FUZZ_CASES"), out int n) ? n : 20_000;
        var rng = new Random(20261004);
        int compared = 0, timeouts = 0, broken = 0, emptyIteration = 0, nestedLoop = 0;
        var failures = new List<string>();
        string lastCase = "";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var slow = System.Diagnostics.Stopwatch.StartNew();
        for (int k = 0; k < cases && failures.Count < 10; k++)
        {
            if (slow.ElapsedMilliseconds > 1000 && k > 0)
            {
                Console.WriteLine($"    slow case {k - 1}: {slow.ElapsedMilliseconds} ms: {lastCase}");
            }
            slow.Restart();
            if (k % 50_000 == 0 && k > 0) Console.WriteLine($"    {k} cases, {clock.Elapsed.TotalSeconds:F0} s");
            var gen = new Gen(rng);
            var hir = gen.Pattern(rng.Next(1, 5));
            bool anchored = rng.Next(5) == 0;
            lastCase = $"/{hir}/";
            DotNetOracle oracle;
            PikeVM vm;
            try
            {
                oracle = new DotNetOracle(hir, anchored);
                vm = new PikeVM(Compiler.Compile(hir));
            }
            catch (BuildException)
            {
                continue;
            }
            var cache = vm.CreateCache();
            // .NET is not asked about the shapes where its answer differs as
            // known: it would not be compared, and on these shapes its
            // backtracking can also take many seconds per haystack, timeout or not.
            bool emptyIterationShape = HasEmptyIteration(hir);
            bool nestedLoopShape = !emptyIterationShape && HasNestedLoop(hir);
            for (int h = 0; h < 4; h++)
            {
                var hay = gen.Haystack();
                var input = new Input(hay) { Anchored = anchored ? Anchored.Yes : Anchored.No };
                if (emptyIterationShape || nestedLoopShape)
                {
                    foreach (var c in vm.CapturesAll(cache, input)) CheckBoundaries(hir, hay, c, failures);
                    if (emptyIterationShape) emptyIteration++;
                    else nestedLoop++;
                    continue;
                }
                List<ExpectedMatch> expected;
                try
                {
                    expected = oracle.FindAll(hay, null);
                }
                catch (RegexMatchTimeoutException)
                {
                    timeouts++;
                    continue;
                }
                catch (OracleBrokenException)
                {
                    broken++;
                    continue;
                }
                var actual = vm.CapturesAll(cache, input)
                    .Select(c => new ExpectedMatch(Enumerable.Range(0, expected.FirstOrDefault()?.Groups.Count ?? c.GroupLength)
                        .Select(g => c.Get(g) is { } m ? new Span(m.Start, m.End) : (Span?)null).ToList()))
                    .ToList();
                compared++;

                foreach (var c in vm.CapturesAll(cache, input)) CheckBoundaries(hir, hay, c, failures);

                var exp = expected.Select(m => m.ToString()).ToList();
                var act = actual.Select(m => m.ToString()).ToList();
                if (exp.SequenceEqual(act)) continue;
                failures.Add($"/{hir}/{(anchored ? " anchored" : "")} on \"{Encoding.UTF8.GetString(hay).Replace("\n", "\\n")}\"\n" +
                             $"      .NET  [{string.Join(" ", exp)}]\n      BjoRx [{string.Join(" ", act)}]\n      .NET pattern {oracle.Pattern}");
            }
        }
        foreach (var f in failures) Console.WriteLine("    " + f);
        Console.WriteLine($"  fuzz: {compared} haystacks compared with .NET; not compared, because .NET differs as known: " +
                          $"{emptyIteration} (empty iteration) and {nestedLoop} (nested loops); {timeouts} .NET timeouts, " +
                          $"{broken} impossible .NET answers");
        Check.Equal(0, failures.Count, "disagreements");
    }
}
