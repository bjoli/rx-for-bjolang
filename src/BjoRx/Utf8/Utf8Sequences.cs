// Ported from regex-syntax/src/utf8.rs (rust-lang/regex). MIT OR Apache-2.0;
// see THIRD-PARTY.md.

using System.Text;

namespace BjoRx.Utf8;

/// <summary>A single inclusive range of UTF-8 bytes.</summary>
[NonUserCode]
public readonly record struct Utf8Range(byte Start, byte End)
{
    public bool Matches(byte b) => Start <= b && b <= End;

    public override string ToString() => Start == End ? $"[{Start:X}]" : $"[{Start:X}-{End:X}]";
}

/// <summary>
/// A sequence of one to four byte ranges. A byte string matches it when each
/// successive byte is in the corresponding range: <c>[C2-DF][80-BF]</c> does
/// not match <c>DD 61</c>, because 61 is below 80.
/// </summary>
[NonUserCode]
public readonly struct Utf8Sequence : IEquatable<Utf8Sequence>
{
    private readonly Utf8Range _r0, _r1, _r2, _r3;

    /// <summary>The number of byte ranges, from 1 to 4.</summary>
    public int Length { get; }

    public Utf8Sequence(ReadOnlySpan<Utf8Range> ranges)
    {
        if (ranges.Length is < 1 or > 4)
        {
            throw new ArgumentException("a UTF-8 sequence has one to four ranges", nameof(ranges));
        }
        Length = ranges.Length;
        _r0 = ranges[0];
        if (ranges.Length > 1) _r1 = ranges[1];
        if (ranges.Length > 2) _r2 = ranges[2];
        if (ranges.Length > 3) _r3 = ranges[3];
    }

    /// <summary>
    /// The sequence for the encoded endpoints of a scalar range whose two ends
    /// encode to the same number of bytes.
    /// </summary>
    internal static Utf8Sequence FromEncodedRange(ReadOnlySpan<byte> start, ReadOnlySpan<byte> end)
    {
        if (start.Length != end.Length)
        {
            throw new InvalidOperationException("the endpoints of a UTF-8 sequence encode to different lengths");
        }
        Span<Utf8Range> ranges = stackalloc Utf8Range[start.Length];
        for (int i = 0; i < start.Length; i++) ranges[i] = new Utf8Range(start[i], end[i]);
        return new Utf8Sequence(ranges);
    }

    public Utf8Range this[int i] => (uint)i < (uint)Length
        ? i switch { 0 => _r0, 1 => _r1, 2 => _r2, _ => _r3 }
        : throw new IndexOutOfRangeException();

    /// <summary>
    /// The ranges in the opposite order, for building an automaton that
    /// matches classes backwards: <c>[D0-D3][80-BF]</c> becomes
    /// <c>[80-BF][D0-D3]</c>.
    /// </summary>
    public Utf8Sequence Reversed()
    {
        Span<Utf8Range> ranges = stackalloc Utf8Range[Length];
        for (int i = 0; i < Length; i++) ranges[Length - 1 - i] = this[i];
        return new Utf8Sequence(ranges);
    }

    /// <summary>True if a prefix of <paramref name="bytes"/> matches this sequence.</summary>
    public bool Matches(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Length) return false;
        for (int i = 0; i < Length; i++)
        {
            if (!this[i].Matches(bytes[i])) return false;
        }
        return true;
    }

    // The unused fields are always default, so comparing all four is exact.
    public bool Equals(Utf8Sequence other) =>
        Length == other.Length && _r0 == other._r0 && _r1 == other._r1 && _r2 == other._r2 && _r3 == other._r3;

    public override bool Equals(object? obj) => obj is Utf8Sequence other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Length, _r0, _r1, _r2, _r3);

    public static bool operator ==(Utf8Sequence a, Utf8Sequence b) => a.Equals(b);

    public static bool operator !=(Utf8Sequence a, Utf8Sequence b) => !a.Equals(b);

    public override string ToString()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Length; i++) sb.Append(this[i]);
        return sb.ToString();
    }
}

/// <summary>
/// The sequences of byte ranges that together match exactly the UTF-8
/// encodings of a range of scalar values.
/// </summary>
///
/// <remarks>
/// <para>
/// A byte string encodes a scalar in the range if and only if it matches
/// exactly one of the sequences, and no two sequences match the same bytes.
/// Surrogates are never matched, even when the range spans them, and neither
/// is any other invalid UTF-8. For the whole of Unicode the sequences are:
/// </para>
/// <code>
/// [0-7F]
/// [C2-DF][80-BF]
/// [E0][A0-BF][80-BF]
/// [E1-EC][80-BF][80-BF]
/// [ED][80-9F][80-BF]
/// [EE-EF][80-BF][80-BF]
/// [F0][90-BF][80-BF][80-BF]
/// [F1-F3][80-BF][80-BF][80-BF]
/// [F4][80-8F][80-BF][80-BF]
/// </code>
/// <para>
/// The technique is Russ Cox's, from RE2, who had it from Ken Thompson's
/// <c>grep</c>; Lucene's <c>UTF32ToUTF8</c> does the same.
/// </para>
/// </remarks>
[NonUserCode]
public sealed class Utf8Sequences
{
    private const int MaxUtf8Bytes = 4;

    private readonly Stack<(uint Start, uint End)> _rangeStack = new();

    public Utf8Sequences(uint start, uint end)
    {
        Reset(start, end);
    }

    /// <summary>All the sequences for <paramref name="start"/>..=<paramref name="end"/>.</summary>
    public static List<Utf8Sequence> For(uint start, uint end)
    {
        var seqs = new Utf8Sequences(start, end);
        var result = new List<Utf8Sequence>();
        while (seqs.TryNext(out var seq)) result.Add(seq);
        return result;
    }

    /// <summary>Starts over with a new range, keeping the allocated stack.</summary>
    public void Reset(uint start, uint end)
    {
        if (end > 0x10FFFF) throw new ArgumentOutOfRangeException(nameof(end), "above U+10FFFF");
        _rangeStack.Clear();
        _rangeStack.Push((start, end));
    }

    public bool TryNext(out Utf8Sequence sequence)
    {
        while (_rangeStack.TryPop(out var range))
        {
            var (start, end) = range;
            while (true)
            {
                // Split off the surrogate block; either half may then be empty.
                if (start < 0xE000 && end > 0xD7FF)
                {
                    _rangeStack.Push((0xE000, end));
                    end = 0xD7FF;
                    continue;
                }
                if (start > end) break;

                // Split where the encoded length changes.
                bool split = false;
                for (int i = 1; i < MaxUtf8Bytes; i++)
                {
                    uint max = MaxScalarValue(i);
                    if (start <= max && max < end)
                    {
                        _rangeStack.Push((max + 1, end));
                        end = max;
                        split = true;
                        break;
                    }
                }
                if (split) continue;

                if (end <= 0x7F)
                {
                    sequence = new Utf8Sequence([new Utf8Range((byte)start, (byte)end)]);
                    return true;
                }

                // Split until every continuation byte below the first that
                // differs covers its whole 80-BF range.
                for (int i = 1; i < MaxUtf8Bytes; i++)
                {
                    uint m = (1u << (6 * i)) - 1;
                    if ((start & ~m) != (end & ~m))
                    {
                        if ((start & m) != 0)
                        {
                            _rangeStack.Push(((start | m) + 1, end));
                            end = start | m;
                            split = true;
                            break;
                        }
                        if ((end & m) != m)
                        {
                            _rangeStack.Push((end & ~m, end));
                            end = (end & ~m) - 1;
                            split = true;
                            break;
                        }
                    }
                }
                if (split) continue;

                Span<byte> s = stackalloc byte[MaxUtf8Bytes];
                Span<byte> e = stackalloc byte[MaxUtf8Bytes];
                int ns = new Rune(start).EncodeToUtf8(s);
                int ne = new Rune(end).EncodeToUtf8(e);
                sequence = Utf8Sequence.FromEncodedRange(s[..ns], e[..ne]);
                return true;
            }
        }
        sequence = default;
        return false;
    }

    private static uint MaxScalarValue(int nbytes) => nbytes switch
    {
        1 => 0x007F,
        2 => 0x07FF,
        3 => 0xFFFF,
        4 => 0x10FFFF,
        _ => throw new ArgumentOutOfRangeException(nameof(nbytes)),
    };
}
