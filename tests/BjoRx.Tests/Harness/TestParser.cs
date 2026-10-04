using System.Globalization;
using System.Text;
using BjoRx.Syntax;

namespace BjoRx.Tests.Harness;

/// <summary>
/// Parses the part of Rust's regex syntax that the engine supports into an
/// <see cref="Hir"/>, so that tests can be written as patterns and the
/// upstream test data can be read. It is test code: the engine itself never
/// parses pattern text.
/// </summary>
///
/// <remarks>
/// <para>
/// Supported: literals and escapes (<c>\n</c>, <c>\x7F</c>, <c>\x{10FFFF}</c>,
/// <c>\u…</c>, <c>\U…</c>, escaped metacharacters), <c>.</c>, classes with
/// ranges, negation, nesting, ASCII classes like <c>[[:alpha:]]</c> and the
/// operators <c>&amp;&amp;</c> <c>--</c> <c>~~</c>, groups (<c>(…)</c>,
/// <c>(?:…)</c>, <c>(?P&lt;n&gt;…)</c>, <c>(?&lt;n&gt;…)</c>), alternation,
/// every repetition and its lazy form, <c>\A</c> and <c>\z</c>, and the flags
/// <c>s</c>, <c>U</c> and <c>u</c>. As in Rust, <c>^</c> and <c>$</c> are
/// <c>\A</c> and <c>\z</c> when multi-line mode is off.
/// </para>
/// <para>
/// Anything else the engine cannot express — case-insensitivity, multi-line
/// anchors, word boundaries, Unicode classes like <c>\w</c> or <c>\p{L}</c>,
/// byte mode — raises <see cref="NotSupportedException"/>. A pattern that is
/// wrong raises <see cref="FormatException"/>.
/// </para>
/// </remarks>
public sealed class TestParser
{
    private readonly string _pattern;
    private int _pos;
    private uint _nextCapture = 1;
    private readonly HashSet<string> _names = [];

    private struct Flags
    {
        public bool DotMatchesNewline;
        public bool SwapGreed;
        public bool Crlf;
    }

    private TestParser(string pattern)
    {
        _pattern = pattern;
    }

    public static Hir Parse(string pattern)
    {
        var parser = new TestParser(pattern);
        var flags = new Flags();
        var hir = parser.ParseAlternation(ref flags, inGroup: false);
        if (parser._pos < pattern.Length) throw parser.Error("unopened ')'");
        return hir;
    }

    // --- Structure ------------------------------------------------------------

    private Hir ParseAlternation(ref Flags flags, bool inGroup)
    {
        var branches = new List<Hir> { ParseConcat(ref flags) };
        while (More && Cur == '|')
        {
            _pos++;
            branches.Add(ParseConcat(ref flags));
        }
        if (inGroup)
        {
            if (!More || Cur != ')') throw Error("unclosed group");
            _pos++;
        }
        return Hir.Alternation(branches);
    }

    private Hir ParseConcat(ref Flags flags)
    {
        var parts = new List<Hir>();
        while (More && Cur != '|' && Cur != ')')
        {
            var atom = ParseAtom(ref flags, out bool repeatable);
            if (atom is null)
            {
                if (More && Cur is '*' or '+' or '?') throw Error("repetition operator missing expression");
                continue;
            }
            parts.Add(ParseRepetitions(atom, flags, repeatable));
        }
        return Hir.Concat(parts);
    }

    private Hir ParseRepetitions(Hir atom, Flags flags, bool repeatable)
    {
        bool repeated = false;
        while (More && Cur is '*' or '+' or '?' or '{')
        {
            if (Cur == '{' && !LooksLikeCounted()) break;
            if (!repeatable) throw Error("repetition operator missing expression");
            if (repeated) throw Error("a repetition cannot be repeated directly; group it first");
            uint min;
            uint? max;
            switch (Cur)
            {
                case '*': _pos++; (min, max) = (0, null); break;
                case '+': _pos++; (min, max) = (1, null); break;
                case '?': _pos++; (min, max) = (0, 1); break;
                default: (min, max) = ParseCounted(); break;
            }
            bool lazy = false;
            if (More && Cur == '?')
            {
                _pos++;
                lazy = true;
            }
            atom = Hir.Repetition(min, max, lazy == flags.SwapGreed, atom);
            repeated = true;
        }
        return atom;
    }

    private bool LooksLikeCounted()
    {
        int p = _pos + 1;
        while (p < _pattern.Length && _pattern[p] == ' ') p++;
        return p < _pattern.Length && char.IsAsciiDigit(_pattern[p]);
    }

    private (uint, uint?) ParseCounted()
    {
        _pos++;
        uint min = ParseDecimal();
        uint? max = min;
        if (More && Cur == ',')
        {
            _pos++;
            SkipSpaces();
            max = More && char.IsAsciiDigit(Cur) ? ParseDecimal() : null;
        }
        SkipSpaces();
        if (!More || Cur != '}') throw Error("unclosed counted repetition");
        _pos++;
        if (max < min) throw Error("invalid repetition range: max below min");
        return (min, max);
    }

    private uint ParseDecimal()
    {
        SkipSpaces();
        int start = _pos;
        while (More && char.IsAsciiDigit(Cur)) _pos++;
        if (start == _pos) throw Error("expected a decimal");
        uint n = uint.Parse(_pattern.AsSpan(start, _pos - start), CultureInfo.InvariantCulture);
        SkipSpaces();
        return n;
    }

    private void SkipSpaces()
    {
        while (More && Cur == ' ') _pos++;
    }

    /// <summary>
    /// One atom, or null for something that is not one (a flag setting such
    /// as <c>(?s)</c>). <paramref name="repeatable"/> is false for those.
    /// </summary>
    private Hir? ParseAtom(ref Flags flags, out bool repeatable)
    {
        repeatable = true;
        char c = Cur;
        switch (c)
        {
            case '(':
                return ParseGroup(ref flags, out repeatable);
            case '[':
                return Hir.Class(ParseClass());
            case '.':
                _pos++;
                return Hir.Class(Dot(flags));
            case '^':
                _pos++;
                return Hir.Look(Look.Start);
            case '$':
                _pos++;
                return Hir.Look(Look.End);
            case '\\':
                return ParseEscape();
            case '*' or '+' or '?':
                throw Error("repetition operator missing expression");
            case '{' when LooksLikeCounted():
                throw Error("repetition operator missing expression");
            default:
                return Hir.Scalar(NextScalar());
        }
    }

    private static ScalarClass Dot(Flags flags)
    {
        var cls = new ScalarClass([new ScalarRange(0, 0x10FFFF)]);
        if (!flags.DotMatchesNewline)
        {
            cls.Difference(new ScalarClass([new ScalarRange('\n', '\n')]));
            // In CRLF mode `.` leaves out \r as well.
            if (flags.Crlf) cls.Difference(new ScalarClass([new ScalarRange('\r', '\r')]));
        }
        return cls;
    }

    private Hir? ParseGroup(ref Flags outer, out bool repeatable)
    {
        repeatable = true;
        _pos++;
        if (!Peek("?"))
        {
            uint index = _nextCapture++;
            var inner = outer;
            return Hir.Capture(index, null, ParseAlternation(ref inner, inGroup: true));
        }
        _pos++;
        if (Peek("P<") || (Peek("<") && !Peek("<=") && !Peek("<!")))
        {
            _pos += Cur == 'P' ? 2 : 1;
            int close = _pattern.IndexOf('>', _pos);
            if (close < 0) throw Error("unclosed capture group name");
            string name = _pattern[_pos..close];
            if (name.Length == 0 || !name.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '.' or '[' or ']')
                || char.IsAsciiDigit(name[0]))
            {
                throw Error($"invalid capture group name '{name}'");
            }
            if (!_names.Add(name)) throw Error($"duplicate capture group name '{name}'");
            _pos = close + 1;
            uint index = _nextCapture++;
            var inner = outer;
            return Hir.Capture(index, name, ParseAlternation(ref inner, inGroup: true));
        }
        if (Peek("=") || Peek("!") || Peek("<=") || Peek("<!"))
        {
            throw Error("look-around, including look-ahead and look-behind, is not supported");
        }

        // Flags: (?flags) for the rest of the enclosing group, or (?flags:...).
        var flags = outer;
        bool negate = false;
        bool any = false;
        while (More && Cur != ')' && Cur != ':')
        {
            char f = Cur;
            _pos++;
            switch (f)
            {
                case '-':
                    if (negate) throw Error("repeated negation in flags");
                    negate = true;
                    continue;
                case 's': flags.DotMatchesNewline = !negate; break;
                case 'U': flags.SwapGreed = !negate; break;
                case 'R': flags.Crlf = !negate; break;
                case 'u': if (negate) throw new NotSupportedException("flag -u (byte mode)"); break;
                case 'i' or 'm' or 'x':
                    if (!negate) throw new NotSupportedException($"flag {f}");
                    break;
                default: throw Error($"unrecognized flag '{f}'");
            }
            any = true;
        }
        if (!More) throw Error("unclosed group");
        if (Cur == ')')
        {
            if (!any && !negate) throw Error("empty flag group");
            _pos++;
            outer = flags;
            repeatable = false;
            return null;
        }
        _pos++;
        return ParseAlternation(ref flags, inGroup: true);
    }

    // --- Escapes and scalars -----------------------------------------------------

    private Hir ParseEscape()
    {
        _pos++;
        if (!More) throw Error("incomplete escape sequence");
        char c = Cur;
        switch (c)
        {
            case 'A': _pos++; return Hir.Look(Look.Start);
            case 'z': _pos++; return Hir.Look(Look.End);
            case 'b' or 'B' or '<' or '>':
                throw new NotSupportedException($"word boundary \\{c}");
            case 'd' or 'D' or 's' or 'S' or 'w' or 'W' or 'p' or 'P':
                throw new NotSupportedException($"Unicode class \\{c}");
            default:
                return Hir.Scalar(EscapedScalar());
        }
    }

    /// <summary>The scalar an escape stands for, with <see cref="_pos"/> on the character after the backslash.</summary>
    private uint EscapedScalar()
    {
        char c = Cur;
        _pos++;
        switch (c)
        {
            case 'a': return 0x07;
            case 'f': return 0x0C;
            case 't': return 0x09;
            case 'n': return 0x0A;
            case 'r': return 0x0D;
            case 'v': return 0x0B;
            case 'x': return HexEscape(2);
            case 'u': return HexEscape(4);
            case 'U': return HexEscape(8);
            case >= '0' and <= '9':
                throw new NotSupportedException("backreferences and octal escapes");
        }
        if (char.IsAsciiLetterOrDigit(c)) throw Error($"unrecognized escape \\{c}");
        if (char.IsSurrogate(c)) throw Error("an escape of a non-ASCII character");
        return c;
    }

    private uint HexEscape(int digits)
    {
        string hex;
        if (More && Cur == '{')
        {
            int close = _pattern.IndexOf('}', _pos);
            if (close < 0) throw Error("unclosed hex escape");
            hex = _pattern[(_pos + 1)..close];
            _pos = close + 1;
            if (hex.Length is < 1 or > 8) throw Error("invalid hex escape");
        }
        else
        {
            if (_pos + digits > _pattern.Length) throw Error("incomplete hex escape");
            hex = _pattern.Substring(_pos, digits);
            _pos += digits;
        }
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v) || !Rune.IsValid(v))
        {
            throw Error($"hex escape {hex} is not a Unicode scalar value");
        }
        return v;
    }

    private uint NextScalar()
    {
        if (Rune.DecodeFromUtf16(_pattern.AsSpan(_pos), out var rune, out int used) != System.Buffers.OperationStatus.Done)
        {
            throw Error("invalid UTF-16 in pattern");
        }
        _pos += used;
        return (uint)rune.Value;
    }

    // --- Classes ---------------------------------------------------------------

    /// <summary>A bracketed class, with <see cref="_pos"/> on the '['.</summary>
    private ScalarClass ParseClass()
    {
        _pos++;
        bool negated = false;
        if (More && Cur == '^')
        {
            _pos++;
            negated = true;
        }
        var result = ParseClassUnion(first: true);
        while (true)
        {
            if (!More) throw Error("unclosed character class");
            if (Cur == ']')
            {
                _pos++;
                break;
            }
            string op = _pattern.Substring(_pos, 2);
            _pos += 2;
            var rhs = ParseClassUnion(first: false);
            switch (op)
            {
                case "&&": result.Intersect(rhs); break;
                case "--": result.Difference(rhs); break;
                case "~~": result.SymmetricDifference(rhs); break;
                default: throw Error($"unexpected '{op}' in class");
            }
        }
        if (negated) result.Negate();
        return result;
    }

    private bool AtClassOperator() => Peek("&&") || Peek("--") || Peek("~~");

    private ScalarClass ParseClassUnion(bool first)
    {
        var set = ScalarClass.Empty();
        bool start = true;
        while (true)
        {
            if (!More) throw Error("unclosed character class");
            if (Cur == ']' && !(start && first)) return set;
            if (AtClassOperator() && !start) return set;
            start = false;

            if (Peek("[:") && TryPosixClass(out var posix))
            {
                set.Union(posix);
                continue;
            }
            if (Cur == '[')
            {
                set.Union(ParseClass());
                continue;
            }

            uint lo = ClassScalar();
            if (Peek("-") && !Peek("-]") && !Peek("--"))
            {
                _pos++;
                uint hi = ClassScalar();
                if (hi < lo) throw Error("invalid class range: end before start");
                set.Push(new ScalarRange(lo, hi));
            }
            else
            {
                set.Push(new ScalarRange(lo, lo));
            }
        }
    }

    private uint ClassScalar()
    {
        if (Cur != '\\') return NextScalar();
        _pos++;
        if (!More) throw Error("incomplete escape sequence");
        char c = Cur;
        if (c is 'd' or 'D' or 's' or 'S' or 'w' or 'W' or 'p' or 'P')
        {
            throw new NotSupportedException($"Unicode class \\{c}");
        }
        if (c is 'b' or 'B' or 'A' or 'z') throw Error($"\\{c} in a class");
        return EscapedScalar();
    }

    private static readonly Dictionary<string, (char, char)[]> Posix = new()
    {
        ["alnum"] = [('0', '9'), ('A', 'Z'), ('a', 'z')],
        ["alpha"] = [('A', 'Z'), ('a', 'z')],
        ["ascii"] = [('\x00', '\x7F')],
        ["blank"] = [('\t', '\t'), (' ', ' ')],
        ["cntrl"] = [('\x00', '\x1F'), ('\x7F', '\x7F')],
        ["digit"] = [('0', '9')],
        ["graph"] = [('!', '~')],
        ["lower"] = [('a', 'z')],
        ["print"] = [(' ', '~')],
        ["punct"] = [('!', '/'), (':', '@'), ('[', '`'), ('{', '~')],
        ["space"] = [('\t', '\r'), (' ', ' ')],
        ["upper"] = [('A', 'Z')],
        ["word"] = [('0', '9'), ('A', 'Z'), ('a', 'z'), ('_', '_')],
        ["xdigit"] = [('0', '9'), ('A', 'F'), ('a', 'f')],
    };

    private bool TryPosixClass(out ScalarClass cls)
    {
        cls = ScalarClass.Empty();
        int close = _pattern.IndexOf(":]", _pos + 2, StringComparison.Ordinal);
        if (close < 0) return false;
        string name = _pattern[(_pos + 2)..close];
        bool negated = name.StartsWith('^');
        if (negated) name = name[1..];
        if (!Posix.TryGetValue(name, out var ranges)) return false;
        cls = new ScalarClass(ranges.Select(r => new ScalarRange(r.Item1, r.Item2)));
        if (negated) cls.Negate();
        _pos = close + 2;
        return true;
    }

    // --- Helpers -----------------------------------------------------------------

    private bool More => _pos < _pattern.Length;

    private char Cur => _pattern[_pos];

    private bool Peek(string s) => string.CompareOrdinal(_pattern, _pos, s, 0, s.Length) == 0;

    private FormatException Error(string message) =>
        new($"{message} (at {_pos} in {_pattern})");
}
