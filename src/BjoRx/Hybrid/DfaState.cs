// Ported from regex-automata/src/util/determinize/state.rs (rust-lang/regex).
// MIT OR Apache-2.0; see THIRD-PARTY.md.

using System.Buffers.Binary;
using BjoRx.Syntax;

namespace BjoRx.Hybrid;

/// <summary>
/// A DFA state during determinization: an ordered set of NFA states, plus
/// whether it matches and which assertions were true when it was made. Kept
/// as bytes, so that equal states are equal byte strings and the cache can
/// look a candidate up before deciding to keep it.
/// </summary>
///
/// <remarks>
/// <para>The layout:</para>
/// <list type="bullet">
/// <item>byte 0: flags, bit 0 set for a match state;</item>
/// <item>bytes 1–2: the assertions known to hold (<c>look_have</c>);</item>
/// <item>bytes 3–4: the assertions some NFA state in the set waits on (<c>look_need</c>);</item>
/// <item>then the NFA state ids in order, each as a zigzag varint of its
/// difference from the one before, which keeps nearby ids to a byte.</item>
/// </list>
/// <para>
/// Upstream also stores pattern ids and the word-boundary and CRLF flags; with
/// one pattern and only <c>\A</c>/<c>\z</c> none of them can vary.
/// </para>
/// </remarks>
[NonUserCode]
internal static class DfaState
{
    public const int HeaderLength = 5;

    /// <summary>The state with no NFA states: the dead state, and the unknown sentinel.</summary>
    public static byte[] Dead() => new byte[HeaderLength];

    public static bool IsMatch(ReadOnlySpan<byte> state) => (state[0] & 1) != 0;

    public static LookSet LookHave(ReadOnlySpan<byte> state) => new(BinaryPrimitives.ReadUInt16LittleEndian(state[1..]));

    public static LookSet LookNeed(ReadOnlySpan<byte> state) => new(BinaryPrimitives.ReadUInt16LittleEndian(state[3..]));

    public static NfaIds NfaStateIds(ReadOnlySpan<byte> state) => new(state[HeaderLength..]);

    /// <summary>Iterates the NFA state ids of a state, in order.</summary>
    [NonUserCode]
    public ref struct NfaIds(ReadOnlySpan<byte> encoded)
    {
        private ReadOnlySpan<byte> _rest = encoded;
        private int _current;

        public readonly int Current => _current;

        public bool MoveNext()
        {
            if (_rest.IsEmpty) return false;
            _current += ReadVarI32(ref _rest);
            return true;
        }

        public readonly NfaIds GetEnumerator() => this;
    }

    public static void WriteVarI32(StateBuilder into, int n)
    {
        uint un = (uint)n << 1;
        if (n < 0) un = ~un;
        while (un >= 0x80)
        {
            into.Push((byte)(un | 0x80));
            un >>= 7;
        }
        into.Push((byte)un);
    }

    private static int ReadVarI32(ref ReadOnlySpan<byte> data)
    {
        uint un = 0;
        int shift = 0;
        int i = 0;
        while (true)
        {
            byte b = data[i++];
            un |= (uint)(b & 0x7F) << shift;
            if (b < 0x80) break;
            shift += 7;
        }
        data = data[i..];
        int n = (int)(un >> 1);
        return (un & 1) != 0 ? ~n : n;
    }
}

/// <summary>
/// Builds a <see cref="DfaState"/> in a reused buffer. Upstream has three
/// builder types for its three phases; here they are one class used in that
/// order: <see cref="Clear"/> (empty), <see cref="StartMatches"/> (record a
/// match and assertions), <see cref="StartNfa"/> (add NFA states).
/// </summary>
[NonUserCode]
internal sealed class StateBuilder
{
    private byte[] _buffer = new byte[64];
    private int _length;
    private int _prevNfaStateId;

    public int Capacity => _buffer.Length;

    public ReadOnlySpan<byte> AsSpan() => _buffer.AsSpan(0, _length);

    public byte[] ToState() => AsSpan().ToArray();

    public void Clear() => _length = 0;

    public void StartMatches()
    {
        _length = 0;
        for (int i = 0; i < DfaState.HeaderLength; i++) Push(0);
    }

    public void StartNfa() => _prevNfaStateId = 0;

    public bool IsMatch => DfaState.IsMatch(AsSpan());

    /// <summary>Upstream's <c>add_match_pattern_id</c>, for the only pattern.</summary>
    public void SetIsMatch() => _buffer[0] |= 1;

    public LookSet LookHave
    {
        get => DfaState.LookHave(AsSpan());
        set => BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(1), value.Bits);
    }

    public LookSet LookNeed
    {
        get => DfaState.LookNeed(AsSpan());
        set => BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(3), value.Bits);
    }

    public void AddNfaStateId(int sid)
    {
        DfaState.WriteVarI32(this, sid - _prevNfaStateId);
        _prevNfaStateId = sid;
    }

    internal void Push(byte b)
    {
        if (_length == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
        _buffer[_length++] = b;
    }
}

/// <summary>
/// Compares states by content, and lets a dictionary keyed by states be
/// searched with a builder's span, so that a lookup does not allocate.
/// </summary>
[NonUserCode]
internal sealed class DfaStateComparer : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
{
    public static readonly DfaStateComparer Instance = new();

    public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

    public int GetHashCode(byte[] obj) => GetHashCode((ReadOnlySpan<byte>)obj);

    public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);

    public int GetHashCode(ReadOnlySpan<byte> alternate)
    {
        var hash = new HashCode();
        hash.AddBytes(alternate);
        return hash.ToHashCode();
    }

    public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();
}
