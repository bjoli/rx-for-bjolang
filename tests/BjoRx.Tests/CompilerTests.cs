using BjoRx.Nfa;
using BjoRx.Tests.Harness;

namespace BjoRx.Tests;

/// <summary>
/// The compiler tests from regex-automata/src/nfa/thompson/compiler.rs. States
/// are compared by their printed form.
/// </summary>
public static class CompilerTests
{
    private static NFA Build(string pattern, CompilerConfig? config = null) =>
        Compiler.Compile(TestParser.Parse(pattern),
            config ?? new CompilerConfig { WhichCaptures = WhichCaptures.None, UnanchoredPrefix = false });

    private static string Byte(char b, int next) => new Transition((byte)b, (byte)b, next).ToString();
    private static string Byte(byte b, int next) => new Transition(b, b, next).ToString();
    private static string Range(byte s, byte e, int next) => new Transition(s, e, next).ToString();
    private static string Sparse(params (byte, byte, int)[] ts) =>
        State.Sparse(ts.Select(t => new Transition(t.Item1, t.Item2, t.Item3)).ToArray()).ToString();
    private static string BinUnion(int a, int b) => State.BinaryUnion(a, b).ToString();
    private static string Union(params int[] alts) => State.Union(alts).ToString();
    private static string LookS(BjoRx.Syntax.Look look, int next) => State.LookAround(look, next).ToString();
    private static string Cap(int next, int group, int slot) => State.Capture(next, group, slot).ToString();
    private static string Match => State.Match().ToString();
    private static string Fail => State.Fail().ToString();

    private static void States(NFA nfa, string what, params string[] expected) =>
        Check.SequenceEqual(expected, nfa.States.Select(s => s.ToString()), what + "\n" + nfa);

    [Test]
    public static void UnanchoredPrefix()
    {
        var nfa = Build("a", new CompilerConfig { WhichCaptures = WhichCaptures.None });
        States(nfa, "a", BinUnion(2, 1), Range(0, 255, 0), Byte('a', 3), Match);
    }

    [Test]
    public static void NoUnanchoredPrefixWithStartAnchor()
    {
        var nfa = Build("^a", new CompilerConfig { WhichCaptures = WhichCaptures.None });
        States(nfa, "^a", LookS(BjoRx.Syntax.Look.Start, 1), Byte('a', 2), Match);
    }

    [Test]
    public static void UnanchoredPrefixWithEndAnchor()
    {
        var nfa = Build("a$", new CompilerConfig { WhichCaptures = WhichCaptures.None });
        States(nfa, "a$", BinUnion(2, 1), Range(0, 255, 0), Byte('a', 3), LookS(BjoRx.Syntax.Look.End, 4), Match);
    }

    [Test]
    public static void ReverseUnanchoredPrefixWithStartAnchor()
    {
        var nfa = Build("^a", new CompilerConfig { WhichCaptures = WhichCaptures.None, Reverse = true });
        // Anchors are flipped in a reverse automaton.
        States(nfa, "rev ^a", BinUnion(2, 1), Range(0, 255, 0), Byte('a', 3), LookS(BjoRx.Syntax.Look.End, 4), Match);
    }

    [Test]
    public static void ReverseNoUnanchoredPrefixWithEndAnchor()
    {
        var nfa = Build("a$", new CompilerConfig { WhichCaptures = WhichCaptures.None, Reverse = true });
        States(nfa, "rev a$", LookS(BjoRx.Syntax.Look.Start, 1), Byte('a', 2), Match);
    }

    [Test]
    public static void Empty() => States(Build(""), "empty", Match);

    [Test]
    public static void Literal()
    {
        States(Build("a"), "a", Byte('a', 1), Match);
        States(Build("ab"), "ab", Byte('a', 1), Byte('b', 2), Match);
        States(Build("☃"), "snowman", Byte(0xE2, 1), Byte(0x98, 2), Byte(0x83, 3), Match);
    }

    [Test]
    public static void ClassAscii()
    {
        States(Build("[a-z]"), "[a-z]", Range((byte)'a', (byte)'z', 1), Match);
        States(Build("[x-za-c]"), "[x-za-c]", Sparse(((byte)'a', (byte)'c', 1), ((byte)'x', (byte)'z', 1)), Match);
    }

    [Test]
    public static void ClassUnicode()
    {
        States(Build(@"[\u03B1-\u03B4]"), "greek", Range(0xB1, 0xB4, 2), Byte(0xCE, 0), Match);
        States(Build(@"[\u03B1-\u03B4\u{1F919}-\u{1F91E}]"), "greek and emoji",
            Range(0xB1, 0xB4, 5),
            Range(0x99, 0x9E, 5),
            Byte(0xA4, 1),
            Byte(0x9F, 2),
            Sparse((0xCE, 0xCE, 0), (0xF0, 0xF0, 3)),
            Match);
        States(Build("[a-z☃]"), "ascii and snowman",
            Byte(0x83, 3),
            Byte(0x98, 0),
            Sparse(((byte)'a', (byte)'z', 3), (0xE2, 0xE2, 1)),
            Match);
    }

    [Test]
    public static void Repetition()
    {
        States(Build("a?"), "a?", BinUnion(1, 2), Byte('a', 2), Match);
        States(Build("a??"), "a??", BinUnion(2, 1), Byte('a', 2), Match);
    }

    [Test]
    public static void Group()
    {
        States(Build("ab+"), "ab+", Byte('a', 1), Byte('b', 2), BinUnion(1, 3), Match);
        States(Build("(ab)"), "(ab)", Byte('a', 1), Byte('b', 2), Match);
        States(Build("(ab)+"), "(ab)+", Byte('a', 1), Byte('b', 2), BinUnion(0, 3), Match);
    }

    [Test]
    public static void Alternation()
    {
        States(Build("a|b"), "a|b", Range((byte)'a', (byte)'b', 1), Match);
        States(Build("ab|cd"), "ab|cd",
            Byte('b', 3), Byte('d', 3), Sparse(((byte)'a', (byte)'a', 0), ((byte)'c', (byte)'c', 1)), Match);
        States(Build("|b"), "|b", Byte('b', 2), BinUnion(2, 0), Match);
        States(Build("a|"), "a|", Byte('a', 2), BinUnion(0, 2), Match);
    }

    [Test]
    public static void NonBinaryUnion()
    {
        var nfa = Build(@"[\u1000\u2000\u3000]",
            new CompilerConfig { WhichCaptures = WhichCaptures.None, Reverse = true, UnanchoredPrefix = false });
        States(nfa, "reverse three-way",
            Union(3, 6, 9),
            Byte(0xE1, 10),
            Byte(0x80, 1),
            Byte(0x80, 2),
            Byte(0xE2, 10),
            Byte(0x80, 4),
            Byte(0x80, 5),
            Byte(0xE3, 10),
            Byte(0x80, 7),
            Byte(0x80, 8),
            Match);
    }

    [Test]
    public static void EmptyClass()
    {
        var nfa = Compiler.Compile(BjoRx.Syntax.Hir.Class(BjoRx.Syntax.ScalarClass.Empty()),
            new CompilerConfig { WhichCaptures = WhichCaptures.None, UnanchoredPrefix = false });
        States(nfa, "empty class", Fail, Match);
    }

    [Test]
    public static void CapturesAll()
    {
        var nfa = Build("a(b)c", new CompilerConfig { UnanchoredPrefix = false, WhichCaptures = WhichCaptures.All });
        States(nfa, "all",
            Cap(1, 0, 0), Byte('a', 2), Cap(3, 1, 2), Byte('b', 4), Cap(5, 1, 3), Byte('c', 6), Cap(7, 0, 1), Match);
        Check.Equal(2, nfa.GroupInfo.GroupLength, "groups");
    }

    [Test]
    public static void CapturesImplicit()
    {
        var nfa = Build("a(b)c", new CompilerConfig { UnanchoredPrefix = false, WhichCaptures = WhichCaptures.Implicit });
        States(nfa, "implicit", Cap(1, 0, 0), Byte('a', 2), Byte('b', 3), Byte('c', 4), Cap(5, 0, 1), Match);
        Check.Equal(1, nfa.GroupInfo.GroupLength, "groups");
    }

    [Test]
    public static void CapturesNone()
    {
        var nfa = Build("a(b)c");
        States(nfa, "none", Byte('a', 1), Byte('b', 2), Byte('c', 3), Match);
        Check.Equal(0, nfa.GroupInfo.GroupLength, "groups");
    }

    // --- Beyond upstream's unit tests -------------------------------------------

    [Test]
    public static void MissingGroupsArePadded()
    {
        var nfa = Build("(a){0}(?P<x>a)", new CompilerConfig { UnanchoredPrefix = false });
        Check.Equal(3, nfa.GroupInfo.GroupLength, "groups 0, 1 and 2");
        Check.Equal(null, nfa.GroupInfo.Name(1), "the removed group");
        Check.Equal(2, nfa.GroupInfo.IndexOf("x"), "named group keeps its number");
    }

    [Test]
    public static void LiteralTrieSharesPrefixes()
    {
        var nfa = Build("foo|foobar|fox");
        int byteStates = nfa.States.Count(s => s.Kind is StateKind.ByteRange or StateKind.Sparse);
        Check.True(byteStates < 3 + 6 + 3, $"expected shared prefixes, got\n{nfa}");
    }

    [Test]
    public static void BigClassesStaySmall()
    {
        // Every scalar except a few: the minimizing compiler should need only
        // a handful of states, forwards and backwards.
        var forward = Build("[^abc]");
        var reverse = Build("[^abc]", new CompilerConfig { WhichCaptures = WhichCaptures.None, Reverse = true, UnanchoredPrefix = false });
        Check.True(forward.States.Length < 20, $"forward [^abc] has {forward.States.Length} states\n{forward}");
        Check.True(reverse.States.Length < 40, $"reverse [^abc] has {reverse.States.Length} states\n{reverse}");
    }

    [Test]
    public static void ByteClassesSplitAtRangeEdges()
    {
        var nfa = Build("[a-c]x");
        var classes = nfa.ByteClasses;
        Check.Equal(classes.Get((byte)'a'), classes.Get((byte)'c'), "a and c together");
        Check.True(classes.Get((byte)'a') != classes.Get((byte)'`'), "` apart from a");
        Check.True(classes.Get((byte)'x') != classes.Get((byte)'y'), "x apart from y");
        Check.Equal(classes.Get(0), classes.Get((byte)'`'), "everything below a together");
    }

    [Test]
    public static void ReverseRequiresNoCaptures()
    {
        Check.Throws<BuildException>(
            () => Build("a", new CompilerConfig { Reverse = true, WhichCaptures = WhichCaptures.All }),
            "reverse with captures");
    }

    [Test]
    public static void HugeRepetitionHitsTheSizeLimit()
    {
        Check.Throws<BuildException>(
            () => Build("[^a]{1000}{1000}", new CompilerConfig { NfaSizeLimit = 1 << 20 }),
            "size limit");
    }
}
