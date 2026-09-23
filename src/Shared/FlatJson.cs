using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BeepTone
{
    // Reads and writes config.json. The file is one flat object, so a small parser is enough and
    // avoids loading System.Web (about 17 extra modules) just for JSON.
    public static class FlatJson
    {
        public static object Parse(string text)
        {
            int at = 0;
            object value = ParseValue(text, ref at);
            SkipSpace(text, ref at);
            if (at != text.Length) throw Error(at, "unexpected text after the value");
            return value;
        }

        public static string Quote(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in value ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        static object ParseValue(string s, ref int at)
        {
            SkipSpace(s, ref at);
            if (at >= s.Length) throw Error(at, "the file ended early");
            char c = s[at];
            if (c == '{')
            {
                var obj = new Dictionary<string, object>(StringComparer.Ordinal);
                at++;
                SkipSpace(s, ref at);
                if (at < s.Length && s[at] == '}') { at++; return obj; }
                while (true)
                {
                    SkipSpace(s, ref at);
                    if (at >= s.Length || s[at] != '"') throw Error(at, "expected a name in quotes");
                    string name = ParseString(s, ref at);
                    SkipSpace(s, ref at);
                    if (at >= s.Length || s[at] != ':') throw Error(at, "expected ':'");
                    at++;
                    obj[name] = ParseValue(s, ref at);
                    SkipSpace(s, ref at);
                    if (at < s.Length && s[at] == ',') { at++; continue; }
                    if (at < s.Length && s[at] == '}') { at++; return obj; }
                    throw Error(at, "expected ',' or '}'");
                }
            }
            if (c == '[')
            {
                var list = new List<object>();
                at++;
                SkipSpace(s, ref at);
                if (at < s.Length && s[at] == ']') { at++; return list; }
                while (true)
                {
                    list.Add(ParseValue(s, ref at));
                    SkipSpace(s, ref at);
                    if (at < s.Length && s[at] == ',') { at++; continue; }
                    if (at < s.Length && s[at] == ']') { at++; return list; }
                    throw Error(at, "expected ',' or ']'");
                }
            }
            if (c == '"') return ParseString(s, ref at);
            if (Word(s, ref at, "true")) return true;
            if (Word(s, ref at, "false")) return false;
            if (Word(s, ref at, "null")) return null;
            int start = at;
            while (at < s.Length && "+-0123456789.eE".IndexOf(s[at]) >= 0) at++;
            double number;
            if (at > start && double.TryParse(s.Substring(start, at - start), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                return number;
            throw Error(start, "unexpected character");
        }

        static string ParseString(string s, ref int at)
        {
            var sb = new StringBuilder();
            at++;
            while (at < s.Length)
            {
                char c = s[at++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (at >= s.Length) break;
                char e = s[at++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (at + 4 > s.Length) throw Error(at, "short \\u escape");
                        sb.Append((char)int.Parse(s.Substring(at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        at += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw Error(at, "a string is not closed");
        }

        static bool Word(string s, ref int at, string word)
        {
            if (string.CompareOrdinal(s, at, word, 0, word.Length) != 0) return false;
            at += word.Length;
            return true;
        }

        static void SkipSpace(string s, ref int at)
        {
            while (at < s.Length && char.IsWhiteSpace(s[at])) at++;
        }

        static InvalidDataException Error(int at, string what)
        {
            return new InvalidDataException("JSON " + what + " at character " + at);
        }
    }
}
