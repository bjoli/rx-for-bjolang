using System.Globalization;
using System.Text;

namespace BjoRx.Tests.Harness;

/// <summary>
/// A reader for the TOML that the upstream test data uses, and no more:
/// <c>[[table]]</c> headers, <c>key = value</c>, basic and literal strings
/// (single-line, including <c>'''</c>), integers, booleans, arrays over
/// several lines, inline tables and <c>#</c> comments.
/// </summary>
/// <remarks>
/// Values are <see cref="string"/>, <see cref="long"/>, <see cref="bool"/>,
/// <c>List&lt;object&gt;</c> and <c>Dictionary&lt;string, object&gt;</c>.
/// </remarks>
public sealed class Toml
{
    private readonly string _text;
    private readonly string _file;
    private int _pos;

    private Toml(string text, string file)
    {
        _text = text;
        _file = file;
    }

    /// <summary>The tables of every <c>[[name]]</c> array, by name, in order.</summary>
    public static Dictionary<string, List<Dictionary<string, object>>> ReadArraysOfTables(string path)
    {
        var reader = new Toml(File.ReadAllText(path), path);
        return reader.ReadDocument();
    }

    private Dictionary<string, List<Dictionary<string, object>>> ReadDocument()
    {
        var result = new Dictionary<string, List<Dictionary<string, object>>>();
        Dictionary<string, object>? current = null;
        while (true)
        {
            SkipBlankAndComments();
            if (_pos >= _text.Length) break;
            if (Peek("[["))
            {
                _pos += 2;
                int close = _text.IndexOf("]]", _pos, StringComparison.Ordinal);
                if (close < 0) throw Error("unterminated [[header]]");
                string name = _text[_pos..close].Trim();
                _pos = close + 2;
                current = new Dictionary<string, object>();
                if (!result.TryGetValue(name, out var list)) result[name] = list = [];
                list.Add(current);
                continue;
            }
            if (current is null) throw Error("a key outside any [[table]]");
            var (key, value) = ReadKeyValue();
            current[key] = value;
        }
        return result;
    }

    private (string, object) ReadKeyValue()
    {
        string key = ReadKey();
        SkipSpaces();
        Expect('=');
        SkipSpaces();
        return (key, ReadValue());
    }

    private string ReadKey()
    {
        int start = _pos;
        while (_pos < _text.Length && (char.IsAsciiLetterOrDigit(_text[_pos]) || _text[_pos] is '-' or '_')) _pos++;
        if (_pos == start) throw Error("expected a key");
        return _text[start.._pos];
    }

    private object ReadValue()
    {
        if (_pos >= _text.Length) throw Error("expected a value");
        char c = _text[_pos];
        if (Peek("'''")) return ReadMultiLiteral();
        if (c == '\'') return ReadLiteral();
        if (c == '"') return ReadBasic();
        if (c == '[') return ReadArray();
        if (c == '{') return ReadInlineTable();
        if (Peek("true")) { _pos += 4; return true; }
        if (Peek("false")) { _pos += 5; return false; }
        return ReadInteger();
    }

    private string ReadMultiLiteral()
    {
        _pos += 3;
        int close = _text.IndexOf("'''", _pos, StringComparison.Ordinal);
        if (close < 0) throw Error("unterminated '''");
        string s = _text[_pos..close];
        _pos = close + 3;
        // TOML drops a newline that immediately follows the opening quotes.
        return s.StartsWith('\n') ? s[1..] : s;
    }

    private string ReadLiteral()
    {
        _pos++;
        int close = _text.IndexOf('\'', _pos);
        if (close < 0) throw Error("unterminated literal string");
        string s = _text[_pos..close];
        _pos = close + 1;
        return s;
    }

    private string ReadBasic()
    {
        _pos++;
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _text.Length || _text[_pos] == '\n') throw Error("unterminated string");
            char c = _text[_pos++];
            if (c == '"') return sb.ToString();
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            char e = _text[_pos++];
            switch (e)
            {
                case 'b': sb.Append('\b'); break;
                case 't': sb.Append('\t'); break;
                case 'n': sb.Append('\n'); break;
                case 'f': sb.Append('\f'); break;
                case 'r': sb.Append('\r'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'u': sb.Append(char.ConvertFromUtf32(Hex(4))); break;
                case 'U': sb.Append(char.ConvertFromUtf32(Hex(8))); break;
                default: throw Error($"unknown escape \\{e}");
            }
        }
    }

    private int Hex(int digits)
    {
        if (_pos + digits > _text.Length) throw Error("short unicode escape");
        int v = int.Parse(_text.AsSpan(_pos, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        _pos += digits;
        return v;
    }

    private List<object> ReadArray()
    {
        Expect('[');
        var items = new List<object>();
        while (true)
        {
            SkipBlankAndComments();
            if (_pos < _text.Length && _text[_pos] == ']')
            {
                _pos++;
                return items;
            }
            items.Add(ReadValue());
            SkipBlankAndComments();
            if (_pos < _text.Length && _text[_pos] == ',') _pos++;
            else if (_pos >= _text.Length || _text[_pos] != ']') throw Error("expected , or ] in array");
        }
    }

    private Dictionary<string, object> ReadInlineTable()
    {
        Expect('{');
        var table = new Dictionary<string, object>();
        while (true)
        {
            SkipSpaces();
            if (_pos < _text.Length && _text[_pos] == '}')
            {
                _pos++;
                return table;
            }
            var (key, value) = ReadKeyValue();
            table[key] = value;
            SkipSpaces();
            if (_pos < _text.Length && _text[_pos] == ',') _pos++;
            else if (_pos >= _text.Length || _text[_pos] != '}') throw Error("expected , or } in inline table");
        }
    }

    private long ReadInteger()
    {
        int start = _pos;
        if (_pos < _text.Length && _text[_pos] is '-' or '+') _pos++;
        while (_pos < _text.Length && (char.IsAsciiDigit(_text[_pos]) || _text[_pos] == '_')) _pos++;
        if (_pos == start) throw Error($"unexpected '{_text[_pos]}'");
        return long.Parse(_text[start.._pos].Replace("_", ""), CultureInfo.InvariantCulture);
    }

    private void SkipSpaces()
    {
        while (_pos < _text.Length && _text[_pos] is ' ' or '\t') _pos++;
    }

    private void SkipBlankAndComments()
    {
        while (_pos < _text.Length)
        {
            char c = _text[_pos];
            if (c is ' ' or '\t' or '\r' or '\n') _pos++;
            else if (c == '#')
            {
                while (_pos < _text.Length && _text[_pos] != '\n') _pos++;
            }
            else break;
        }
    }

    private bool Peek(string s) => string.CompareOrdinal(_text, _pos, s, 0, s.Length) == 0;

    private void Expect(char c)
    {
        if (_pos >= _text.Length || _text[_pos] != c) throw Error($"expected '{c}'");
        _pos++;
    }

    private FormatException Error(string message)
    {
        int line = 1 + _text.AsSpan(0, Math.Min(_pos, _text.Length)).Count('\n');
        return new FormatException($"{_file}:{line}: {message}");
    }
}
