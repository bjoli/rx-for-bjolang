// Ported from regex-automata/src/nfa/thompson/compiler.rs (rust-lang/regex).
// MIT OR Apache-2.0; see THIRD-PARTY.md.

using BjoRx.Syntax;
using BjoRx.Utf8;

namespace BjoRx.Nfa;

/// <summary>Which capture groups the NFA records.</summary>
public enum WhichCaptures
{
    /// <summary>Every group.</summary>
    All,
    /// <summary>Only group 0, the whole match.</summary>
    Implicit,
    /// <summary>None. A reverse NFA must use this.</summary>
    None,
}

[NonUserCode]
public sealed record CompilerConfig
{
    /// <summary>Match the pattern backwards, as a reverse search needs to find where a match starts.</summary>
    public bool Reverse { get; init; }

    /// <summary>Never match invalid UTF-8, and keep empty matches on scalar boundaries.</summary>
    public bool Utf8 { get; init; } = true;

    public WhichCaptures WhichCaptures { get; init; } = WhichCaptures.All;

    /// <summary>
    /// Give the NFA an unanchored start state. Off only in tests, to see the
    /// states of the pattern alone.
    /// </summary>
    public bool UnanchoredPrefix { get; init; } = true;

    /// <summary>A limit on the NFA's size, in approximate bytes, or null for none.</summary>
    public long? NfaSizeLimit { get; init; } = 10 * (1 << 20);
}

/// <summary>Compiles an <see cref="Hir"/> to a Thompson <see cref="NFA"/>.</summary>
///
/// <remarks>
/// Each piece compiles to a fragment with a start and an end state, and the
/// fragments are patched together. The NFA has a single match state, behind a
/// capture of group 0 around the whole pattern. Not ported: upstream's literal
/// trie for alternations of literals, which only makes the NFA smaller.
/// </remarks>
[NonUserCode]
public sealed class Compiler
{
    [NonUserCode]
    private readonly record struct Ref(int Start, int End);

    private readonly CompilerConfig _config;
    private readonly Builder _builder = new();
    private readonly Utf8BoundedMap _utf8Compiled = new(10_000);
    private readonly Utf8SuffixMap _utf8Suffix = new(1000);

    public Compiler(CompilerConfig? config = null)
    {
        _config = config ?? new CompilerConfig();
    }

    public static NFA Compile(Hir hir, CompilerConfig? config = null) => new Compiler(config).Build(hir);

    public NFA Build(Hir hir)
    {
        if (_config.Reverse && _config.WhichCaptures != WhichCaptures.None)
        {
            throw new BuildException("a reverse NFA cannot have capture states");
        }
        _builder.Clear();
        _builder.Utf8 = _config.Utf8;
        _builder.Reverse = _config.Reverse;
        _builder.SizeLimit = _config.NfaSizeLimit;

        // Every match being anchored makes the unanchored prefix pointless; the
        // two start states are then the same.
        var props = hir.Properties;
        bool allAnchored = _config.Reverse
            ? props.LookSetSuffix.Contains(Look.End)
            : props.LookSetPrefix.Contains(Look.Start);
        bool anchored = !_config.UnanchoredPrefix || allAnchored;
        // The prefix is (?s-u:.)*?: any byte, lazily, valid UTF-8 or not.
        var unanchoredPrefix = anchored
            ? CompileEmpty()
            : CompileAtLeast(CompileAnyByte, minimumLengthPositive: true, greedy: false, 0);

        _builder.StartPattern();
        var one = CompileCapture(0, null, hir);
        int match = _builder.AddMatch();
        _builder.Patch(one.End, match);
        _builder.FinishPattern(one.Start);

        _builder.Patch(unanchoredPrefix.End, one.Start);
        return _builder.Build(one.Start, unanchoredPrefix.Start);
    }

    private Ref C(Hir hir) => hir switch
    {
        HirEmpty => CompileEmpty(),
        HirLiteral lit => CompileLiteral(lit.Bytes),
        HirClass cls => CompileUnicodeClass(cls.Set),
        HirLook look => CompileLook(look.Assertion),
        HirRepetition rep => CompileRepetition(rep),
        HirCapture cap => CompileCapture(cap.Index, cap.Name, cap.Sub),
        HirConcat concat => CompileConcat(concat.Subs.Count, [NonUserCode] (i) => C(concat.Subs[i])),
        HirAlternation alt => CompileAlternationOf(alt.Subs),
        _ => throw new NotSupportedException(hir.GetType().Name),
    };

    /// <summary>
    /// <paramref name="count"/> parts in sequence, compiled one at a time by
    /// <paramref name="part"/>; last first for a reverse NFA.
    /// </summary>
    private Ref CompileConcat(int count, Func<int, Ref> part)
    {
        if (count == 0) return CompileEmpty();
        Ref Part(int k) => part(_config.Reverse ? count - 1 - k : k);
        var result = Part(0);
        for (int k = 1; k < count; k++)
        {
            var compiled = Part(k);
            _builder.Patch(result.End, compiled.Start);
            result = new Ref(result.Start, compiled.End);
        }
        return result;
    }

    /// <summary>An alternation; one of nothing but literals becomes a trie.</summary>
    private Ref CompileAlternationOf(IReadOnlyList<Hir> branches)
    {
        int literals = branches.Count([NonUserCode] (b) => b is HirLiteral);
        if (literals <= 1 || literals < branches.Count) return CompileAlternation(branches.Select(C));

        var trie = new LiteralTrie(_config.Reverse);
        foreach (var b in branches) trie.Add(((HirLiteral)b).Bytes);
        var (start, end) = trie.Compile(_builder);
        return new Ref(start, end);
    }

    private Ref CompileAlternation(IEnumerable<Ref> branches)
    {
        using var it = branches.GetEnumerator();
        if (!it.MoveNext()) return CompileFail();
        var first = it.Current;
        if (!it.MoveNext()) return first;
        var second = it.Current;

        int union = _builder.AddUnion();
        int end = _builder.AddEmpty();
        _builder.Patch(union, first.Start);
        _builder.Patch(first.End, end);
        _builder.Patch(union, second.Start);
        _builder.Patch(second.End, end);
        while (it.MoveNext())
        {
            var compiled = it.Current;
            _builder.Patch(union, compiled.Start);
            _builder.Patch(compiled.End, end);
        }
        return new Ref(union, end);
    }

    private Ref CompileCapture(uint index, string? name, Hir sub)
    {
        switch (_config.WhichCaptures)
        {
            case WhichCaptures.None:
            case WhichCaptures.Implicit when index > 0:
                return C(sub);
        }
        int start = _builder.AddCaptureStart(0, (int)index, name);
        var inner = C(sub);
        int end = _builder.AddCaptureEnd(0, (int)index);
        _builder.Patch(start, inner.Start);
        _builder.Patch(inner.End, end);
        return new Ref(start, end);
    }

    private Ref CompileRepetition(HirRepetition rep) => (rep.Min, rep.Max) switch
    {
        (0, 1) => CompileZeroOrOne(rep.Sub, rep.Greedy),
        (var min, null) => CompileAtLeast([NonUserCode] () => C(rep.Sub), rep.Sub.Properties.MinimumLength > 0, rep.Greedy, min),
        (var min, { } max) when min == max => CompileExactly([NonUserCode] () => C(rep.Sub), min),
        (var min, { } max) => CompileBounded(rep.Sub, rep.Greedy, min, max),
    };

    /// <summary>
    /// <c>x{min,max}</c> as <c>min</c> copies, then optional copies that each
    /// jump straight to the end when skipped. Nesting them instead, as in
    /// <c>xxx?x?x?</c>, would put every remaining copy in one epsilon closure.
    /// </summary>
    private Ref CompileBounded(Hir expr, bool greedy, uint min, uint max)
    {
        var prefix = CompileExactly([NonUserCode] () => C(expr), min);
        if (min == max) return prefix;

        int empty = _builder.AddEmpty();
        int prevEnd = prefix.End;
        for (uint i = min; i < max; i++)
        {
            int union = greedy ? _builder.AddUnion() : _builder.AddUnionReverse();
            var compiled = C(expr);
            _builder.Patch(prevEnd, union);
            _builder.Patch(union, compiled.Start);
            _builder.Patch(union, empty);
            prevEnd = compiled.End;
        }
        _builder.Patch(prevEnd, empty);
        return new Ref(prefix.Start, empty);
    }

    /// <summary>
    /// <c>x{n,}</c>, where <paramref name="sub"/> compiles a fresh copy of x
    /// and <paramref name="minimumLengthPositive"/> says x cannot match empty.
    /// </summary>
    private Ref CompileAtLeast(Func<Ref> sub, bool minimumLengthPositive, bool greedy, uint n)
    {
        Ref Sub() => sub();
        int NewUnion() => greedy ? _builder.AddUnion() : _builder.AddUnionReverse();

        if (n == 0)
        {
            // When x cannot match empty, x* is one union that loops back to itself.
            if (minimumLengthPositive)
            {
                int union = NewUnion();
                var compiled = Sub();
                _builder.Patch(union, compiled.Start);
                _builder.Patch(compiled.End, union);
                return new Ref(union, union);
            }

            // When x can match empty, that loop gives the wrong preference
            // order under leftmost-first semantics, so x* is compiled as
            // (x+)? instead. See rust-lang/regex#779.
            var inner = Sub();
            int plus = NewUnion();
            _builder.Patch(inner.End, plus);
            _builder.Patch(plus, inner.Start);

            int question = NewUnion();
            int empty = _builder.AddEmpty();
            _builder.Patch(question, inner.Start);
            _builder.Patch(question, empty);
            _builder.Patch(plus, empty);
            return new Ref(question, empty);
        }
        if (n == 1)
        {
            var compiled = Sub();
            int union = NewUnion();
            _builder.Patch(compiled.End, union);
            _builder.Patch(union, compiled.Start);
            return new Ref(compiled.Start, union);
        }

        var prefix = CompileExactly(sub, n - 1);
        var last = Sub();
        int u = NewUnion();
        _builder.Patch(prefix.End, last.Start);
        _builder.Patch(last.End, u);
        _builder.Patch(u, last.Start);
        return new Ref(prefix.Start, u);
    }

    private Ref CompileZeroOrOne(Hir expr, bool greedy)
    {
        int union = greedy ? _builder.AddUnion() : _builder.AddUnionReverse();
        var compiled = C(expr);
        int empty = _builder.AddEmpty();
        _builder.Patch(union, compiled.Start);
        _builder.Patch(union, empty);
        _builder.Patch(compiled.End, empty);
        return new Ref(union, empty);
    }

    private Ref CompileExactly(Func<Ref> sub, uint n) => CompileConcat((int)n, [NonUserCode] (_) => sub());

    /// <summary>Any byte, as upstream compiles a byte class: a sparse state and an empty one after it.</summary>
    private Ref CompileAnyByte()
    {
        int end = _builder.AddEmpty();
        return new Ref(_builder.AddSparse([new Transition(0x00, 0xFF, end)]), end);
    }

    private Ref CompileUnicodeClass(ScalarClass cls)
    {
        // An ASCII class fits one sparse state.
        if (cls.IsAscii)
        {
            int end = _builder.AddEmpty();
            var trans = cls.Ranges.Select([NonUserCode] (r) => new Transition((byte)r.Start, (byte)r.End, end)).ToArray();
            return new Ref(_builder.AddSparse(trans), end);
        }
        if (_config.Reverse) return CompileUnicodeClassReverseWithSuffix(cls);

        // Forward, the sequences come out of Utf8Sequences in sorted order, so
        // they stream straight into a minimizing compiler.
        var utf8c = new Utf8Compiler(_builder, _utf8Compiled);
        var seqs = new Utf8Sequences(0, 0);
        foreach (var range in cls.Ranges)
        {
            seqs.Reset(range.Start, range.End);
            while (seqs.TryNext(out var seq)) utf8c.Add(seq);
        }
        var (start, target) = utf8c.Finish();
        return new Ref(start, target);
    }

    /// <summary>
    /// A class backwards: each sequence reversed, as its own branch, sharing
    /// the states for common suffixes — which, read backwards, are the
    /// sequences' common prefixes.
    /// </summary>
    private Ref CompileUnicodeClassReverseWithSuffix(ScalarClass cls)
    {
        _utf8Suffix.Clear();
        int union = _builder.AddUnion();
        int altEnd = _builder.AddEmpty();
        var seqs = new Utf8Sequences(0, 0);
        foreach (var range in cls.Ranges)
        {
            seqs.Reset(range.Start, range.End);
            while (seqs.TryNext(out var seq))
            {
                int end = altEnd;
                for (int i = 0; i < seq.Length; i++)
                {
                    var brng = seq[i];
                    var key = new Utf8SuffixMap.Key(end, brng.Start, brng.End);
                    int hash = _utf8Suffix.Hash(key);
                    if (_utf8Suffix.Get(key, hash) is { } cached)
                    {
                        end = cached;
                        continue;
                    }
                    var compiled = CompileRange(brng.Start, brng.End);
                    _builder.Patch(compiled.End, end);
                    end = compiled.Start;
                    _utf8Suffix.Set(key, hash, end);
                }
                _builder.Patch(union, end);
            }
        }
        return new Ref(union, altEnd);
    }

    private Ref CompileLook(Look look)
    {
        if (_config.Reverse) look = look.Reversed();
        int id = _builder.AddLook(0, look);
        return new Ref(id, id);
    }

    private Ref CompileLiteral(byte[] bytes) =>
        CompileConcat(bytes.Length, [NonUserCode] (i) => CompileRange(bytes[i], bytes[i]));

    private Ref CompileRange(byte start, byte end)
    {
        int id = _builder.AddRange(new Transition(start, end, 0));
        return new Ref(id, id);
    }

    private Ref CompileEmpty()
    {
        int id = _builder.AddEmpty();
        return new Ref(id, id);
    }

    private Ref CompileFail()
    {
        int id = _builder.AddFail();
        return new Ref(id, id);
    }

    /// <summary>
    /// Builds the automaton for a class from its UTF-8 sequences, which must
    /// arrive in sorted order, sharing every state it has already built that
    /// has the same transitions. That makes it close to minimal: <c>\w</c>
    /// takes a few hundred states instead of a few thousand.
    /// </summary>
    /// <remarks>
    /// The sequences form a trie whose rightmost path is still open. A new
    /// sequence shares a prefix with that path; everything on the path below
    /// the shared prefix can no longer change, so it is frozen into states.
    /// </remarks>
    [NonUserCode]
    private sealed class Utf8Compiler
    {
        [NonUserCode]
        private sealed class Node
        {
            public readonly List<Transition> Trans = [];
            public Utf8Range? Last;

            public void SetLastTransition(int next)
            {
                if (Last is { } last)
                {
                    Trans.Add(new Transition(last.Start, last.End, next));
                    Last = null;
                }
            }
        }

        private readonly Builder _builder;
        private readonly Utf8BoundedMap _compiled;
        private readonly List<Node> _uncompiled = [];
        private readonly int _target;

        public Utf8Compiler(Builder builder, Utf8BoundedMap compiled)
        {
            _builder = builder;
            _compiled = compiled;
            _target = builder.AddEmpty();
            _compiled.Clear();
            _uncompiled.Add(new Node());
        }

        public (int Start, int End) Finish()
        {
            CompileFrom(0);
            if (_uncompiled.Count != 1 || _uncompiled[0].Last is not null)
            {
                throw new InvalidOperationException("UTF-8 compiler left with an unfinished node");
            }
            var root = _uncompiled[0];
            _uncompiled.Clear();
            return (Compile(root.Trans), _target);
        }

        public void Add(Utf8Sequence seq)
        {
            int prefixLength = 0;
            while (prefixLength < seq.Length
                && prefixLength < _uncompiled.Count
                && _uncompiled[prefixLength].Last == seq[prefixLength])
            {
                prefixLength++;
            }
            if (prefixLength >= seq.Length) throw new InvalidOperationException("UTF-8 sequences out of order");
            CompileFrom(prefixLength);
            AddSuffix(seq, prefixLength);
        }

        private void CompileFrom(int from)
        {
            int next = _target;
            while (from + 1 < _uncompiled.Count)
            {
                var node = _uncompiled[^1];
                _uncompiled.RemoveAt(_uncompiled.Count - 1);
                node.SetLastTransition(next);
                next = Compile(node.Trans);
            }
            _uncompiled[^1].SetLastTransition(next);
        }

        private int Compile(List<Transition> node)
        {
            int hash = _compiled.Hash(node);
            if (_compiled.Get(node, hash) is { } id) return id;
            var key = node.ToArray();
            id = _builder.AddSparse(key);
            _compiled.Set(key, hash, id);
            return id;
        }

        private void AddSuffix(Utf8Sequence seq, int from)
        {
            var last = _uncompiled[^1];
            if (last.Last is not null) throw new InvalidOperationException("UTF-8 compiler node already has a last transition");
            last.Last = seq[from];
            for (int i = from + 1; i < seq.Length; i++)
            {
                _uncompiled.Add(new Node { Last = seq[i] });
            }
        }
    }
}
