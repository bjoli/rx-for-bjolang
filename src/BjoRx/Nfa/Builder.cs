// Ported from regex-automata/src/nfa/thompson/builder.rs (rust-lang/regex).
// MIT OR Apache-2.0; see THIRD-PARTY.md.

using BjoRx.Syntax;

namespace BjoRx.Nfa;

/// <summary>Raised when an NFA cannot be built, for instance because it would be too big.</summary>
public sealed class BuildException(string message) : Exception(message);

/// <summary>
/// Assembles an NFA from states that are patched together as they are made.
/// </summary>
///
/// <remarks>
/// The states here include kinds the finished NFA does not have. <c>Empty</c>
/// is an unconditional jump, which the compiler uses freely as a join point and
/// <see cref="Build"/> removes. <c>UnionReverse</c> is a union whose
/// alternatives are added in reverse order of preference, which is how a lazy
/// repetition is easiest to patch. A capture has a start and an end state here
/// and becomes two Capture states with their slots.
/// </remarks>
public sealed class Builder
{
    private enum Kind : byte
    {
        Empty,
        ByteRange,
        Sparse,
        Look,
        CaptureStart,
        CaptureEnd,
        Union,
        UnionReverse,
        Fail,
        Match,
    }

    private sealed class BState
    {
        public Kind Kind;
        public int Next;
        public Transition Trans;
        public Transition[] Transitions = [];
        public Look Look;
        public int GroupIndex;
        public List<int> Alternates = [];
    }

    private readonly List<BState> _states = [];
    private readonly List<string?> _captures = [];
    private bool _patternStarted;
    private int _patternStart = -1;
    private long _memory;

    public bool Utf8 { get; set; } = true;

    public bool Reverse { get; set; }

    /// <summary>A limit on the builder's memory, in approximate bytes, or null for none.</summary>
    public long? SizeLimit { get; set; }

    public int Count => _states.Count;

    public void Clear()
    {
        _states.Clear();
        _captures.Clear();
        _patternStarted = false;
        _patternStart = -1;
        _memory = 0;
    }

    /// <summary>Begins the one pattern this NFA is for.</summary>
    public void StartPattern()
    {
        if (_patternStarted) throw new InvalidOperationException("a pattern is already started");
        _patternStarted = true;
    }

    public void FinishPattern(int start)
    {
        if (!_patternStarted) throw new InvalidOperationException("no pattern was started");
        _patternStarted = false;
        _patternStart = start;
    }

    public int AddEmpty() => Add(new BState { Kind = Kind.Empty });

    public int AddRange(Transition t) => Add(new BState { Kind = Kind.ByteRange, Trans = t });

    public int AddSparse(Transition[] transitions) =>
        Add(new BState { Kind = Kind.Sparse, Transitions = transitions }, transitions.Length * 8);

    public int AddLook(int next, Look look) => Add(new BState { Kind = Kind.Look, Next = next, Look = look });

    public int AddUnion() => Add(new BState { Kind = Kind.Union });

    public int AddUnion(List<int> alternates) =>
        Add(new BState { Kind = Kind.Union, Alternates = alternates }, alternates.Count * 4);

    public int AddUnionReverse() => Add(new BState { Kind = Kind.UnionReverse });

    public int AddFail() => Add(new BState { Kind = Kind.Fail });

    public int AddMatch() => Add(new BState { Kind = Kind.Match });

    /// <summary>
    /// The start of capture group <paramref name="groupIndex"/>. Groups may
    /// arrive out of order or not at all — the first group in <c>(a){0}(a)</c>
    /// is simplified away — so missing ones are padded with unnamed groups
    /// that never match, and the numbering stays what the pattern said.
    /// </summary>
    public int AddCaptureStart(int next, int groupIndex, string? name)
    {
        if (groupIndex == 0 && name is not null) throw new BuildException("group 0 cannot have a name");
        if (groupIndex < _captures.Count)
        {
            // The group was already added, which happens when a repetition
            // compiles its sub-expression more than once.
        }
        else
        {
            while (_captures.Count < groupIndex) _captures.Add(null);
            _captures.Add(name);
        }
        return Add(new BState { Kind = Kind.CaptureStart, Next = next, GroupIndex = groupIndex });
    }

    public int AddCaptureEnd(int next, int groupIndex) =>
        Add(new BState { Kind = Kind.CaptureEnd, Next = next, GroupIndex = groupIndex });

    /// <summary>
    /// Points <paramref name="from"/> at <paramref name="to"/>. For a union
    /// this adds an alternative; for a state with one successor it sets it.
    /// </summary>
    public void Patch(int from, int to)
    {
        var s = _states[from];
        switch (s.Kind)
        {
            case Kind.Empty or Kind.Look or Kind.CaptureStart or Kind.CaptureEnd:
                s.Next = to;
                break;
            case Kind.ByteRange:
                s.Trans = s.Trans with { Next = to };
                break;
            case Kind.Sparse:
                throw new InvalidOperationException("cannot patch from a sparse NFA state");
            case Kind.Union or Kind.UnionReverse:
                s.Alternates.Add(to);
                Grow(4);
                break;
            case Kind.Fail or Kind.Match:
                break;
        }
    }

    private int Add(BState state, int extra = 0)
    {
        int id = _states.Count;
        _states.Add(state);
        Grow(32 + extra);
        return id;
    }

    private void Grow(int bytes)
    {
        _memory += bytes;
        if (SizeLimit is { } limit && _memory > limit)
        {
            throw new BuildException($"compiled regex exceeds size limit of {limit} bytes");
        }
    }

    /// <summary>Where an unconditional jump goes, or -1 if the state is not one.</summary>
    private int Goto(int id)
    {
        var s = _states[id];
        return s.Kind switch
        {
            Kind.Empty => s.Next,
            Kind.Union or Kind.UnionReverse when s.Alternates.Count == 1 => s.Alternates[0],
            _ => -1,
        };
    }

    /// <summary>
    /// The finished NFA. Empty states, and unions of one, are removed by
    /// pointing everything that led to them at what they lead to.
    /// </summary>
    public NFA Build(int startAnchored, int startUnanchored)
    {
        if (_patternStarted) throw new InvalidOperationException("must call FinishPattern first");
        var groupInfo = new GroupInfo(_captures);

        var states = new List<State>();
        var byteClassSet = new ByteClassSet();
        var lookSetAny = LookSet.Empty;
        bool hasCapture = false;
        var remap = new int[_states.Count];
        var empties = new List<(int Id, int Next)>();

        int AddFinal(State s)
        {
            switch (s.Kind)
            {
                case StateKind.ByteRange:
                    byteClassSet.SetRange(s.Trans.Start, s.Trans.End);
                    break;
                case StateKind.Sparse:
                    foreach (var t in s.Transitions) byteClassSet.SetRange(t.Start, t.End);
                    break;
                case StateKind.Look:
                    // \A and \z depend on no byte, so they add no class boundary.
                    lookSetAny = lookSetAny.Insert(s.Look);
                    break;
                case StateKind.Capture:
                    hasCapture = true;
                    break;
            }
            states.Add(s);
            return states.Count - 1;
        }

        for (int sid = 0; sid < _states.Count; sid++)
        {
            var s = _states[sid];
            switch (s.Kind)
            {
                case Kind.Empty:
                    empties.Add((sid, s.Next));
                    break;
                case Kind.ByteRange:
                    remap[sid] = AddFinal(State.ByteRange(s.Trans));
                    break;
                case Kind.Sparse:
                    remap[sid] = s.Transitions.Length switch
                    {
                        0 => AddFinal(State.Fail()),
                        1 => AddFinal(State.ByteRange(s.Transitions[0])),
                        _ => AddFinal(State.Sparse(s.Transitions.ToArray())),
                    };
                    break;
                case Kind.Look:
                    remap[sid] = AddFinal(State.LookAround(s.Look, s.Next));
                    break;
                case Kind.CaptureStart:
                    remap[sid] = AddFinal(State.Capture(s.Next, s.GroupIndex, 2 * s.GroupIndex));
                    break;
                case Kind.CaptureEnd:
                    remap[sid] = AddFinal(State.Capture(s.Next, s.GroupIndex, 2 * s.GroupIndex + 1));
                    break;
                case Kind.Union or Kind.UnionReverse:
                {
                    var alts = s.Kind == Kind.UnionReverse
                        ? Enumerable.Reverse(s.Alternates).ToArray()
                        : s.Alternates.ToArray();
                    if (alts.Length == 0)
                    {
                        remap[sid] = AddFinal(State.Fail());
                    }
                    else if (alts.Length == 1)
                    {
                        empties.Add((sid, alts[0]));
                    }
                    else if (alts.Length == 2)
                    {
                        remap[sid] = AddFinal(State.BinaryUnion(alts[0], alts[1]));
                    }
                    else
                    {
                        remap[sid] = AddFinal(State.Union(alts));
                    }
                    break;
                }
                case Kind.Fail:
                    remap[sid] = AddFinal(State.Fail());
                    break;
                case Kind.Match:
                    remap[sid] = AddFinal(State.Match());
                    break;
            }
        }

        // An empty state can lead to another, so follow each chain to its end,
        // and remap every state on it at once. Marking them keeps a long chain,
        // as in a{0}{50000}, from being walked once per state in it.
        var remapped = new bool[_states.Count];
        foreach (var (emptyId, emptyNext) in empties)
        {
            if (remapped[emptyId]) continue;
            int target = emptyNext;
            while (Goto(target) is var next && next >= 0) target = next;
            remap[emptyId] = remap[target];
            remapped[emptyId] = true;
            int walk = emptyNext;
            while (Goto(walk) is var next2 && next2 >= 0)
            {
                remap[walk] = remap[target];
                remapped[walk] = true;
                walk = next2;
            }
        }

        var final = states.Select(s => s.Remap(remap)).ToArray();
        int anchored = remap[startAnchored];
        int unanchored = remap[startUnanchored];
        var (hasEmpty, prefixAny) = Analyze(final, remap[_patternStart]);
        return new NFA(final, anchored, unanchored, Reverse, Utf8, groupInfo, byteClassSet.ByteClasses(),
            lookSetAny, prefixAny, hasEmpty, hasCapture);
    }

    /// <summary>
    /// Walks the epsilon closure of the pattern's start: whether it reaches a
    /// match state without consuming input, and which assertions it passes.
    /// </summary>
    private static (bool HasEmpty, LookSet PrefixAny) Analyze(State[] states, int start)
    {
        bool hasEmpty = false;
        var prefixAny = LookSet.Empty;
        var seen = new bool[states.Length];
        var stack = new Stack<int>();
        stack.Push(start);
        while (stack.TryPop(out int sid))
        {
            if (seen[sid]) continue;
            seen[sid] = true;
            var s = states[sid];
            switch (s.Kind)
            {
                case StateKind.Match:
                    hasEmpty = true;
                    break;
                case StateKind.Look:
                    prefixAny = prefixAny.Insert(s.Look);
                    stack.Push(s.Next);
                    break;
                case StateKind.Union:
                    foreach (int a in s.Alternates) stack.Push(a);
                    break;
                case StateKind.BinaryUnion:
                    stack.Push(s.Alt2);
                    stack.Push(s.Alt1);
                    break;
                case StateKind.Capture:
                    stack.Push(s.Next);
                    break;
            }
        }
        return (hasEmpty, prefixAny);
    }
}
