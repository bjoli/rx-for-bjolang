// Ported from regex-automata/src/nfa/thompson/nfa.rs (rust-lang/regex). MIT
// OR Apache-2.0; see THIRD-PARTY.md.

using System.Text;
using BjoRx.Syntax;

namespace BjoRx.Nfa;

/// <summary>A transition on any byte in <c>Start..=End</c> to <c>Next</c>.</summary>
public readonly record struct Transition(byte Start, byte End, int Next)
{
    public bool Matches(byte b) => Start <= b && b <= End;

    public override string ToString() =>
        Start == End ? $"{Escape(Start)} => {Next}" : $"{Escape(Start)}-{Escape(End)} => {Next}";

    internal static string Escape(byte b) =>
        b is >= 0x21 and <= 0x7E && b != (byte)'\\' ? ((char)b).ToString() : $"\\x{b:X2}";
}

public enum StateKind : byte
{
    /// <summary>One transition on a byte range.</summary>
    ByteRange,
    /// <summary>Several transitions on sorted, non-overlapping byte ranges.</summary>
    Sparse,
    /// <summary>An assertion, followed by <see cref="State.Next"/> if it holds.</summary>
    Look,
    /// <summary>Alternatives in order of preference.</summary>
    Union,
    /// <summary>A union of exactly two, which is common enough to have its own state.</summary>
    BinaryUnion,
    /// <summary>Records the current offset in a capture slot, then goes on to <see cref="State.Next"/>.</summary>
    Capture,
    /// <summary>Never matches.</summary>
    Fail,
    Match,
}

/// <summary>
/// One NFA state. A struct with a field for every kind, rather than a class
/// per kind, so that the states sit in one array.
/// </summary>
public readonly struct State
{
    public StateKind Kind { get; private init; }

    /// <summary>The transition of a <see cref="StateKind.ByteRange"/>.</summary>
    public Transition Trans { get; private init; }

    /// <summary>The transitions of a <see cref="StateKind.Sparse"/>.</summary>
    public Transition[] Transitions { get; private init; }

    /// <summary>The alternatives of a <see cref="StateKind.Union"/>, most preferred first.</summary>
    public int[] Alternates { get; private init; }

    /// <summary>The next state of a Look or Capture state.</summary>
    public int Next { get; private init; }

    public Look Look { get; private init; }

    public int Alt1 { get; private init; }
    public int Alt2 { get; private init; }

    /// <summary>The group a Capture state belongs to; 0 is the whole match.</summary>
    public int GroupIndex { get; private init; }

    /// <summary>
    /// The slot a Capture state writes: <c>2 * group</c> for its start and
    /// <c>2 * group + 1</c> for its end.
    /// </summary>
    public int Slot { get; private init; }

    public static State ByteRange(Transition t) => new() { Kind = StateKind.ByteRange, Trans = t };
    public static State Sparse(Transition[] ts) => new() { Kind = StateKind.Sparse, Transitions = ts };
    public static State LookAround(Look look, int next) => new() { Kind = StateKind.Look, Look = look, Next = next };
    public static State Union(int[] alternates) => new() { Kind = StateKind.Union, Alternates = alternates };
    public static State BinaryUnion(int alt1, int alt2) => new() { Kind = StateKind.BinaryUnion, Alt1 = alt1, Alt2 = alt2 };
    public static State Capture(int next, int groupIndex, int slot) =>
        new() { Kind = StateKind.Capture, Next = next, GroupIndex = groupIndex, Slot = slot };
    public static State Fail() => new() { Kind = StateKind.Fail };
    public static State Match() => new() { Kind = StateKind.Match };

    /// <summary>True for a state that moves without consuming input.</summary>
    public bool IsEpsilon => Kind is StateKind.Look or StateKind.Union or StateKind.BinaryUnion or StateKind.Capture;

    internal State Remap(int[] remap) => Kind switch
    {
        StateKind.ByteRange => this with { Trans = Trans with { Next = remap[Trans.Next] } },
        StateKind.Sparse => this with { Transitions = Transitions.Select(t => t with { Next = remap[t.Next] }).ToArray() },
        StateKind.Look or StateKind.Capture => this with { Next = remap[Next] },
        StateKind.Union => this with { Alternates = Alternates.Select(a => remap[a]).ToArray() },
        StateKind.BinaryUnion => this with { Alt1 = remap[Alt1], Alt2 = remap[Alt2] },
        _ => this,
    };

    public override string ToString() => Kind switch
    {
        StateKind.ByteRange => Trans.ToString(),
        StateKind.Sparse => "sparse(" + string.Join(", ", Transitions) + ")",
        StateKind.Look => $"{Look} => {Next}",
        StateKind.Union => "union(" + string.Join(", ", Alternates) + ")",
        StateKind.BinaryUnion => $"binary-union({Alt1}, {Alt2})",
        StateKind.Capture => $"capture(group={GroupIndex}, slot={Slot}) => {Next}",
        StateKind.Fail => "FAIL",
        StateKind.Match => "MATCH",
        _ => Kind.ToString(),
    };
}

/// <summary>
/// The capture groups of a pattern: their names and how many slots they use.
/// Group 0 is the whole match and has no name. An NFA built without capture
/// states has no groups at all.
/// </summary>
public sealed class GroupInfo
{
    private readonly string?[] _names;
    private readonly Dictionary<string, int> _indexOf = new(StringComparer.Ordinal);

    public GroupInfo(IReadOnlyList<string?> names)
    {
        if (names.Count != 0 && names[0] is not null)
        {
            throw new ArgumentException("group 0 must be unnamed", nameof(names));
        }
        _names = names.ToArray();
        for (int i = 1; i < _names.Length; i++)
        {
            if (_names[i] is { } name && !_indexOf.TryAdd(name, i))
            {
                throw new ArgumentException($"duplicate capture group name '{name}'", nameof(names));
            }
        }
    }

    /// <summary>The number of groups, including group 0.</summary>
    public int GroupLength => _names.Length;

    public int SlotLength => 2 * _names.Length;

    public string? Name(int group) => _names[group];

    public int? IndexOf(string name) => _indexOf.TryGetValue(name, out int i) ? i : null;
}

/// <summary>
/// The 256 bytes partitioned into classes whose bytes no state can tell
/// apart. A DFA indexes its transitions by class, plus one class past the
/// last for the end of input.
/// </summary>
public sealed class ByteClasses
{
    private readonly byte[] _classOf;

    internal ByteClasses(byte[] classOf)
    {
        _classOf = classOf;
    }

    /// <summary>Every byte in a class of its own.</summary>
    public static ByteClasses Singletons()
    {
        var classOf = new byte[256];
        for (int b = 0; b < 256; b++) classOf[b] = (byte)b;
        return new ByteClasses(classOf);
    }

    public byte Get(byte b) => _classOf[b];

    /// <summary>The class of a byte (0–255) or of the end of input (256).</summary>
    public int GetByUnit(int unit) => unit < 256 ? _classOf[unit] : Eoi;

    /// <summary>The class of every byte, for search loops that index it directly.</summary>
    internal byte[] Table => _classOf;

    /// <summary>The number of classes, counting the end-of-input class.</summary>
    public int AlphabetLength => _classOf[255] + 2;

    /// <summary>
    /// The log2 of the smallest power of two that is at least
    /// <see cref="AlphabetLength"/>: a DFA state's row is that wide, so that a
    /// state id can be a shifted index.
    /// </summary>
    public int Stride2 => System.Numerics.BitOperations.Log2(System.Numerics.BitOperations.RoundUpToPowerOf2((uint)AlphabetLength));

    /// <summary>The class number of the end-of-input pseudo byte.</summary>
    public int Eoi => _classOf[255] + 1;

    public bool IsSingleton => AlphabetLength == 257;

    /// <summary>One byte from each class, in class order: what a DFA needs to explore every class.</summary>
    public IEnumerable<byte> Representatives()
    {
        int last = -1;
        for (int b = 0; b < 256; b++)
        {
            if (_classOf[b] != last)
            {
                last = _classOf[b];
                yield return (byte)b;
            }
        }
    }
}

/// <summary>
/// Collects the byte ranges an NFA distinguishes, so that the bytes can be
/// put into classes: a range splits the classes at both its ends.
/// </summary>
internal sealed class ByteClassSet
{
    // A set bit for byte b means a new class starts at b + 1.
    private readonly bool[] _boundary = new bool[256];

    public void SetRange(byte start, byte end)
    {
        if (start > 0) _boundary[start - 1] = true;
        _boundary[end] = true;
    }

    public ByteClasses ByteClasses()
    {
        var classOf = new byte[256];
        int cls = 0;
        for (int b = 0; b < 256; b++)
        {
            classOf[b] = (byte)cls;
            if (_boundary[b] && b < 255) cls++;
        }
        return new ByteClasses(classOf);
    }
}

/// <summary>
/// A Thompson NFA over bytes, for one pattern. Built by <see cref="Compiler"/>.
/// </summary>
public sealed class NFA
{
    public State[] States { get; }

    /// <summary>The start state for a search that must match where it starts.</summary>
    public int StartAnchored { get; }

    /// <summary>
    /// The start state for a search that may match anywhere: the anchored
    /// start behind a lazy any-byte loop, or the same as the anchored start
    /// when every match is anchored anyway.
    /// </summary>
    public int StartUnanchored { get; }

    public bool IsReverse { get; }

    /// <summary>True when the NFA was built never to match invalid UTF-8.</summary>
    public bool IsUtf8 { get; }

    public GroupInfo GroupInfo { get; }

    public ByteClasses ByteClasses { get; }

    /// <summary>Every assertion that occurs in the NFA.</summary>
    public LookSet LookSetAny { get; }

    /// <summary>The assertions that may be passed before any input is consumed.</summary>
    public LookSet LookSetPrefixAny { get; }

    /// <summary>True when the empty string matches somewhere along an epsilon path from the start.</summary>
    public bool HasEmpty { get; }

    public bool HasCapture { get; }

    internal NFA(
        State[] states, int startAnchored, int startUnanchored, bool reverse, bool utf8,
        GroupInfo groupInfo, ByteClasses byteClasses, LookSet lookSetAny, LookSet lookSetPrefixAny,
        bool hasEmpty, bool hasCapture)
    {
        States = states;
        StartAnchored = startAnchored;
        StartUnanchored = startUnanchored;
        IsReverse = reverse;
        IsUtf8 = utf8;
        GroupInfo = groupInfo;
        ByteClasses = byteClasses;
        LookSetAny = lookSetAny;
        LookSetPrefixAny = lookSetPrefixAny;
        HasEmpty = hasEmpty;
        HasCapture = hasCapture;
    }

    /// <summary>True when the start states coincide, so every search is anchored.</summary>
    public bool IsAlwaysStartAnchored => StartAnchored == StartUnanchored;

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("thompson::NFA(");
        for (int i = 0; i < States.Length; i++)
        {
            char status = i == StartAnchored ? '^' : i == StartUnanchored ? '>' : ' ';
            sb.AppendLine($"{status}{i:D6}: {States[i]}");
        }
        sb.Append(')');
        return sb.ToString();
    }
}
