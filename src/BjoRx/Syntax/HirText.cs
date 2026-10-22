using System.Globalization;
using System.Text;

namespace BjoRx.Syntax;

/// <summary>
/// A <see cref="Hir"/> as text: what Bjolang's <c>#rx(...)</c> compiles to at
/// compile time, and what the runtime reads back into a tree.
/// </summary>
///
/// <remarks>
/// <para>
/// Prefix notation, one form per node, scalars in hexadecimal:
/// </para>
/// <list type="table">
/// <item><term><c>(e)</c></term><description>the empty string</description></item>
/// <item><term><c>(l 61 1F600)</c></term><description>a literal, scalar by scalar</description></item>
/// <item><term><c>(c 30-39 5F)</c></term><description>a class of scalar ranges; <c>(c)</c> never matches</description></item>
/// <item><term><c>(s x y ...)</c></term><description>a concatenation</description></item>
/// <item><term><c>(a x y ...)</c></term><description>an alternation, earlier first</description></item>
/// <item><term><c>(r min max g x)</c></term><description>a repetition; <c>max</c> is <c>-</c> for no bound, <c>g</c> greedy or <c>l</c> lazy</description></item>
/// <item><term><c>(g n x)</c></term><description>capture group <c>n</c>, counting from 1</description></item>
/// <item><term><c>(A)</c>, <c>(z)</c></term><description>the start and the end of the input</description></item>
/// </list>
/// <para>
/// Group names are not part of the text; a caller keeps them beside it.
/// Reading goes through <see cref="Hir"/>'s smart constructors, so the tree
/// read back is simplified as any other is.
/// </para>
/// </remarks>
[NonUserCode]
public static class HirText
{
    public static string Write(Hir hir)
    {
        var sb = new StringBuilder();
        WriteTo(sb, hir);
        return sb.ToString();
    }

    private static void WriteTo(StringBuilder sb, Hir hir)
    {
        switch (hir)
        {
            case HirEmpty:
                sb.Append("(e)");
                break;
            case HirLiteral lit:
                sb.Append("(l");
                foreach (var r in Encoding.UTF8.GetString(lit.Bytes).EnumerateRunes())
                {
                    sb.Append(' ').Append(r.Value.ToString("X", CultureInfo.InvariantCulture));
                }
                sb.Append(')');
                break;
            case HirClass cls:
                sb.Append("(c");
                foreach (var r in cls.Set.Ranges)
                {
                    sb.Append(' ').Append(r.Start.ToString("X", CultureInfo.InvariantCulture));
                    if (r.End != r.Start)
                    {
                        sb.Append('-').Append(r.End.ToString("X", CultureInfo.InvariantCulture));
                    }
                }
                sb.Append(')');
                break;
            case HirLook look:
                sb.Append(look.Assertion == Look.Start ? "(A)" : "(z)");
                break;
            case HirRepetition rep:
                sb.Append("(r ").Append(rep.Min).Append(' ');
                if (rep.Max is { } max) sb.Append(max); else sb.Append('-');
                sb.Append(rep.Greedy ? " g " : " l ");
                WriteTo(sb, rep.Sub);
                sb.Append(')');
                break;
            case HirCapture cap:
                sb.Append("(g ").Append(cap.Index).Append(' ');
                WriteTo(sb, cap.Sub);
                sb.Append(')');
                break;
            case HirConcat concat:
                WriteList(sb, 's', concat.Subs);
                break;
            case HirAlternation alt:
                WriteList(sb, 'a', alt.Subs);
                break;
            default:
                throw new ArgumentException($"no text form for {hir.GetType().Name}", nameof(hir));
        }
    }

    private static void WriteList(StringBuilder sb, char head, IReadOnlyList<Hir> subs)
    {
        sb.Append('(').Append(head);
        foreach (var sub in subs)
        {
            sb.Append(' ');
            WriteTo(sb, sub);
        }
        sb.Append(')');
    }

    /// <summary>Reads the text back. Throws <see cref="FormatException"/>, naming the offset, on anything else.</summary>
    public static Hir Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var reader = new Reader(text);
        var hir = reader.Form();
        reader.SkipSpace();
        if (!reader.AtEnd) throw reader.Error("text after the pattern");
        return hir;
    }

    [NonUserCode]
    private sealed class Reader(string text)
    {
        private int _at;

        public bool AtEnd => _at >= text.Length;

        public FormatException Error(string what) => new($"rx pattern, at offset {_at}: {what}");

        public void SkipSpace()
        {
            while (!AtEnd && text[_at] == ' ') _at++;
        }

        private void Expect(char c)
        {
            SkipSpace();
            if (AtEnd || text[_at] != c) throw Error($"expected '{c}'");
            _at++;
        }

        private bool Peek(char c)
        {
            SkipSpace();
            return !AtEnd && text[_at] == c;
        }

        private string Word()
        {
            SkipSpace();
            int start = _at;
            while (!AtEnd && text[_at] is not (' ' or '(' or ')')) _at++;
            if (_at == start) throw Error("expected a word");
            return text[start.._at];
        }

        private uint Hex(string word)
        {
            if (!uint.TryParse(word, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint n)
                || !Rune.IsValid(n))
            {
                throw Error($"'{word}' is not a scalar in hexadecimal");
            }
            return n;
        }

        private uint Count(string word) =>
            uint.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out uint n)
                ? n
                : throw Error($"'{word}' is not a count");

        public Hir Form()
        {
            Expect('(');
            string head = Word();
            Hir hir = head switch
            {
                "e" => Hir.Empty(),
                "l" => Literal(),
                "c" => Class(),
                "s" => Hir.Concat(Forms()),
                "a" => Hir.Alternation(Forms()),
                "r" => Repetition(),
                "g" => Capture(),
                "A" => Hir.Look(Look.Start),
                "z" => Hir.Look(Look.End),
                _ => throw Error($"'{head}' is not a form"),
            };
            Expect(')');
            return hir;
        }

        private List<Hir> Forms()
        {
            var forms = new List<Hir>();
            while (Peek('(')) forms.Add(Form());
            return forms;
        }

        private Hir Literal()
        {
            var sb = new StringBuilder();
            while (!Peek(')')) sb.Append(new Rune(Hex(Word())).ToString());
            return Hir.Literal(sb.ToString());
        }

        private Hir Class()
        {
            var ranges = new List<ScalarRange>();
            while (!Peek(')'))
            {
                string word = Word();
                int dash = word.IndexOf('-');
                ranges.Add(dash < 0
                    ? new ScalarRange(Hex(word), Hex(word))
                    : new ScalarRange(Hex(word[..dash]), Hex(word[(dash + 1)..])));
            }
            return Hir.Class(new ScalarClass(ranges));
        }

        private Hir Repetition()
        {
            uint min = Count(Word());
            string maxWord = Word();
            uint? max = maxWord == "-" ? null : Count(maxWord);
            if (max < min) throw Error("a repetition's maximum is below its minimum");
            bool greedy = Word() switch
            {
                "g" => true,
                "l" => false,
                var other => throw Error($"'{other}' is neither g (greedy) nor l (lazy)"),
            };
            return Hir.Repetition(min, max, greedy, Form());
        }

        private Hir Capture()
        {
            uint index = Count(Word());
            if (index == 0) throw Error("group 0 is the whole match, so a group counts from 1");
            return Hir.Capture(index, null, Form());
        }
    }
}
