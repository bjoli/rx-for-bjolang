using System.Text;
using System.Text.RegularExpressions;
using BjoRx.Syntax;

namespace BjoRx.Tests.Harness;

/// <summary>.NET gave an answer that cannot be right, so there is nothing to compare with.</summary>
public sealed class OracleBrokenException(string message) : Exception(message);

/// <summary>
/// .NET's backtracking <see cref="Regex"/>, made to answer as the Rust crate
/// would, to check this engine against. Its semantics are Perl's, which is
/// what leftmost-first matching is.
/// </summary>
///
/// <remarks>
/// <para>
/// The <see cref="Hir"/> is written out as a .NET pattern in the way
/// <c>lib/std/rx.bjo</c> does it: every scalar as <c>\uXXXX</c>, astral
/// scalars as surrogate pairs, only <c>\A</c> and <c>\z</c> as anchors, and
/// <c>ExplicitCapture</c> with groups named <c>g1</c>, <c>g2</c>…
/// </para>
/// <para>
/// .NET works on UTF-16 and the engine on UTF-8, so the haystack is decoded
/// first (only valid UTF-8 can be) and every offset .NET reports is turned
/// back into a byte offset.
/// </para>
/// </remarks>
public sealed class DotNetOracle
{
    private readonly Regex _regex;
    private readonly bool _anchored;
    private readonly int _groups;

    public string Pattern { get; }

    public DotNetOracle(Hir hir, bool anchored)
    {
        var sb = new StringBuilder();
        if (anchored) sb.Append(@"\G");
        Emit(hir, sb);
        Pattern = sb.ToString();
        _anchored = anchored;
        // A group that simplification removed, like the first in (a){0}(a),
        // still has its number and never matches, as in the crate's NFA builder.
        _groups = (int)MaxCaptureIndex(hir);
        _regex = new Regex(Pattern, RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(200));
    }

    private static uint MaxCaptureIndex(Hir hir) => hir switch
    {
        HirCapture c => Math.Max(c.Index, MaxCaptureIndex(c.Sub)),
        HirRepetition r => MaxCaptureIndex(r.Sub),
        HirConcat c => c.Subs.Select(MaxCaptureIndex).DefaultIfEmpty(0u).Max(),
        HirAlternation a => a.Subs.Select(MaxCaptureIndex).DefaultIfEmpty(0u).Max(),
        _ => 0,
    };

    /// <summary>
    /// Every match, left to right, with the crate's iteration rule: an empty
    /// match that begins where the previous match ended is not reported, and
    /// the search moves on one byte. An empty match inside a scalar's encoding
    /// is never reported, so an unanchored search goes on to the next scalar
    /// and an anchored one, which cannot move, stops.
    /// </summary>
    public List<ExpectedMatch> FindAll(byte[] haystack, int? limit)
    {
        string text = Encoding.UTF8.GetString(haystack);
        var byteOffset = ByteOffsets(text);
        var result = new List<ExpectedMatch>();
        int pos = 0;
        int lastEnd = -1;
        // Each round either reports a match or moves on, so a correct answer
        // takes at most two rounds per position.
        int rounds = 2 * text.Length + 4;
        while (pos <= text.Length && (limit is null || result.Count < limit))
        {
            if (--rounds < 0) throw new OracleBrokenException(".NET's matches do not move forward");
            System.Text.RegularExpressions.Match m;
            try
            {
                m = _regex.Match(text, pos);
            }
            catch (OverflowException e)
            {
                // The interpreter's backtracking stack overflowing its int counters.
                throw new OracleBrokenException(".NET overflowed: " + e.Message);
            }
            if (!m.Success) break;
            // Seen with nested lazy loops: \G(?:(?:x|(?:☃)*?)+?)+ on "😀"
            // gives a match of length 3 in a string of length 2.
            if (m.Index + m.Length > text.Length || m.Index < pos)
            {
                throw new OracleBrokenException($".NET reported {m.Index}..{m.Index + m.Length} searching from {pos} in a string of length {text.Length}");
            }
            if (m.Length == 0 && m.Index == lastEnd)
            {
                if (pos >= text.Length) break;
                int next = NextScalar(text, pos);
                if (_anchored && byteOffset[next] - byteOffset[pos] > 1) break;
                pos = next;
                continue;
            }
            var groups = new List<Span?> { new Span(byteOffset[m.Index], byteOffset[m.Index + m.Length]) };
            for (int g = 1; g <= _groups; g++)
            {
                var group = m.Groups["g" + g];
                if (group.Success && group.Index + group.Length > text.Length)
                {
                    throw new OracleBrokenException($".NET reported group {g} at {group.Index}..{group.Index + group.Length} in a string of length {text.Length}");
                }
                groups.Add(group.Success ? new Span(byteOffset[group.Index], byteOffset[group.Index + group.Length]) : null);
            }
            result.Add(new ExpectedMatch(groups));
            lastEnd = m.Index + m.Length;
            pos = lastEnd;
        }
        return result;
    }

    /// <summary>The UTF-8 offset of every UTF-16 offset, including the end.</summary>
    private static int[] ByteOffsets(string text)
    {
        var offsets = new int[text.Length + 1];
        int bytes = 0;
        for (int i = 0; i < text.Length; i++)
        {
            offsets[i] = bytes;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                offsets[i + 1] = bytes;
                bytes += 4;
                i++;
            }
            else
            {
                bytes += Encoding.UTF8.GetByteCount(text.AsSpan(i, 1));
            }
        }
        offsets[text.Length] = bytes;
        return offsets;
    }

    private static int NextScalar(string text, int i) =>
        i + 1 < text.Length && char.IsHighSurrogate(text[i]) && char.IsLowSurrogate(text[i + 1]) ? i + 2 : i + 1;

    // --- Writing the pattern ----------------------------------------------------

    private static void Emit(Hir hir, StringBuilder sb)
    {
        switch (hir)
        {
            case HirEmpty:
                sb.Append("(?:)");
                break;
            case HirLiteral lit:
                sb.Append("(?:");
                foreach (var r in Encoding.UTF8.GetString(lit.Bytes).EnumerateRunes()) Scalar(r, sb);
                sb.Append(')');
                break;
            case HirClass c:
                EmitClass(c.Set, sb);
                break;
            case HirLook look:
                sb.Append(look.Assertion == Look.Start ? @"\A" : @"\z");
                break;
            case HirRepetition rep:
                sb.Append("(?:");
                Emit(rep.Sub, sb);
                sb.Append(')');
                sb.Append(rep.Max is { } max ? $"{{{rep.Min},{max}}}" : $"{{{rep.Min},}}");
                if (!rep.Greedy) sb.Append('?');
                break;
            case HirCapture cap:
                sb.Append($"(?<g{cap.Index}>");
                Emit(cap.Sub, sb);
                sb.Append(')');
                break;
            case HirConcat concat:
                sb.Append("(?:");
                foreach (var s in concat.Subs) Emit(s, sb);
                sb.Append(')');
                break;
            case HirAlternation alt:
                sb.Append("(?:");
                for (int i = 0; i < alt.Subs.Count; i++)
                {
                    if (i > 0) sb.Append('|');
                    Emit(alt.Subs[i], sb);
                }
                sb.Append(')');
                break;
            default:
                throw new NotSupportedException(hir.GetType().Name);
        }
    }

    private static void Unit(int u, StringBuilder sb) => sb.Append($"\\u{u:X4}");

    private static void Scalar(Rune r, StringBuilder sb)
    {
        Span<char> units = stackalloc char[2];
        int n = r.EncodeToUtf16(units);
        for (int i = 0; i < n; i++) Unit(units[i], sb);
    }

    /// <summary>
    /// A class as an alternation: one .NET class for the BMP part and, for
    /// each astral range, the surrogate pairs that encode it.
    /// </summary>
    private static void EmitClass(ScalarClass set, StringBuilder sb)
    {
        var branches = new List<string>();
        var bmp = new StringBuilder();
        foreach (var range in set.Ranges)
        {
            uint lo = range.Start, hi = range.End;
            if (lo <= 0xFFFF)
            {
                uint bmpHi = Math.Min(hi, 0xFFFF);
                AppendBmp(lo, bmpHi, bmp);
                lo = bmpHi + 1;
            }
            if (lo <= hi && hi > 0xFFFF) AstralBranches(Math.Max(lo, 0x10000), hi, branches);
        }
        if (bmp.Length > 0) branches.Insert(0, $"[{bmp}]");
        sb.Append(branches.Count switch
        {
            0 => "[^\\u0000-\\uFFFF]",
            _ => "(?:" + string.Join("|", branches) + ")",
        });
    }

    /// <summary>A BMP range without its surrogates, which no Bjolang class contains.</summary>
    private static void AppendBmp(uint lo, uint hi, StringBuilder sb)
    {
        if (lo <= 0xD7FF) Range(lo, Math.Min(hi, 0xD7FF), sb);
        if (hi >= 0xE000) Range(Math.Max(lo, 0xE000), hi, sb);
    }

    private static void Range(uint lo, uint hi, StringBuilder sb)
    {
        if (lo > hi) return;
        Unit((int)lo, sb);
        if (hi != lo)
        {
            sb.Append('-');
            Unit((int)hi, sb);
        }
    }

    /// <summary>Surrogate pairs for lo..=hi, split where the high surrogate changes.</summary>
    private static void AstralBranches(uint lo, uint hi, List<string> branches)
    {
        while (lo <= hi)
        {
            uint high = 0xD800 + ((lo - 0x10000) >> 10);
            uint blockEnd = 0x10000 + ((high - 0xD800 + 1) << 10) - 1;
            uint end = Math.Min(hi, blockEnd);
            uint lowLo = 0xDC00 + ((lo - 0x10000) & 0x3FF);
            uint lowHi = 0xDC00 + ((end - 0x10000) & 0x3FF);
            if (lowLo == 0xDC00 && lowHi == 0xDFFF && end < hi)
            {
                // Whole blocks: one branch covers every high surrogate up to the
                // last block that is entirely inside the range.
                uint lastFull = 0xD800 + ((hi + 1 - 0x10000) >> 10) - 1;
                branches.Add($"[\\u{high:X4}-\\u{lastFull:X4}][\\uDC00-\\uDFFF]");
                lo = 0x10000 + ((lastFull - 0xD800 + 1) << 10);
                continue;
            }
            branches.Add($"\\u{high:X4}[\\u{lowLo:X4}-\\u{lowHi:X4}]");
            lo = end + 1;
        }
    }
}
