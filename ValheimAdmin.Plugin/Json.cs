using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimAdmin
{
    /// <summary>
    /// Minimal JSON for the agent protocol and snapshot payloads.
    /// Objects parse to Dictionary&lt;string, object&gt;, arrays to List&lt;object&gt;,
    /// integers to long, other numbers to double.
    /// </summary>
    public static class Json
    {
        /// <summary>Already-serialized JSON, written verbatim.</summary>
        public sealed class Raw
        {
            public readonly string Text;
            public Raw(string text) { Text = text; }
        }

        public static string Serialize(object value)
        {
            var sb = new StringBuilder(256);
            Write(sb, value);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case Raw raw:
                    sb.Append(raw.Text);
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case float f:
                    WriteDouble(sb, f);
                    break;
                case double d:
                    WriteDouble(sb, d);
                    break;
                case Enum e:
                    sb.Append(Convert.ToInt64(e, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                    break;
                case int _:
                case long _:
                case short _:
                case byte _:
                case uint _:
                case ulong _:
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case IDictionary dict:
                    sb.Append('{');
                    bool first = true;
                    foreach (DictionaryEntry entry in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                        sb.Append(':');
                        Write(sb, entry.Value);
                    }
                    sb.Append('}');
                    break;
                case IEnumerable list:
                    sb.Append('[');
                    bool firstItem = true;
                    foreach (object item in list)
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        Write(sb, item);
                    }
                    sb.Append(']');
                    break;
                default:
                    WriteString(sb, value.ToString());
                    break;
            }
        }

        private static void WriteDouble(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
                sb.Append('0');
            else
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public static object Parse(string text)
        {
            int pos = 0;
            object result = ParseValue(text, ref pos);
            SkipWhitespace(text, ref pos);
            if (pos != text.Length)
                throw new FormatException("Unexpected trailing data at " + pos);
            return result;
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            return Parse(text) as Dictionary<string, object> ?? throw new FormatException("JSON object expected");
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        }

        private static object ParseValue(string s, ref int pos)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[pos];
            switch (c)
            {
                case '{': return ParseDict(s, ref pos);
                case '[': return ParseList(s, ref pos);
                case '"': return ParseString(s, ref pos);
                case 't': Expect(s, ref pos, "true"); return true;
                case 'f': Expect(s, ref pos, "false"); return false;
                case 'n': Expect(s, ref pos, "null"); return null;
                default: return ParseNumber(s, ref pos);
            }
        }

        private static void Expect(string s, ref int pos, string word)
        {
            if (string.CompareOrdinal(s, pos, word, 0, word.Length) != 0)
                throw new FormatException("Unexpected token at " + pos);
            pos += word.Length;
        }

        private static Dictionary<string, object> ParseDict(string s, ref int pos)
        {
            var dict = new Dictionary<string, object>();
            pos++; // {
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return dict; }
            while (true)
            {
                SkipWhitespace(s, ref pos);
                string key = ParseString(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != ':') throw new FormatException("':' expected at " + pos);
                pos++;
                dict[key] = ParseValue(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("Unterminated object");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; return dict; }
                throw new FormatException("',' or '}' expected at " + pos);
            }
        }

        private static List<object> ParseList(string s, ref int pos)
        {
            var list = new List<object>();
            pos++; // [
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return list; }
            while (true)
            {
                list.Add(ParseValue(s, ref pos));
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("Unterminated array");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ']') { pos++; return list; }
                throw new FormatException("',' or ']' expected at " + pos);
            }
        }

        private static string ParseString(string s, ref int pos)
        {
            if (s[pos] != '"') throw new FormatException("String expected at " + pos);
            pos++;
            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (pos >= s.Length) break;
                char e = s[pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        sb.Append((char)int.Parse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        pos += 4;
                        break;
                    default: throw new FormatException("Bad escape at " + pos);
                }
            }
            throw new FormatException("Unterminated string");
        }

        private static object ParseNumber(string s, ref int pos)
        {
            int start = pos;
            bool isFloat = false;
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == '.' || c == 'e' || c == 'E') isFloat = true;
                else if (!(char.IsDigit(c) || c == '-' || c == '+')) break;
                pos++;
            }
            string num = s.Substring(start, pos - start);
            if (num.Length == 0) throw new FormatException("Unexpected character at " + start);
            if (!isFloat && long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                return l;
            return double.Parse(num, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Typed lookups on parsed JSON objects.</summary>
    public static class JsonExt
    {
        public static string Str(this Dictionary<string, object> d, string key, string def = null)
        {
            return d != null && d.TryGetValue(key, out object v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : def;
        }

        public static long Long(this Dictionary<string, object> d, string key, long def = 0)
        {
            if (d == null || !d.TryGetValue(key, out object v) || v == null) return def;
            if (v is string s) return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long r) ? r : def;
            return Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }

        public static int Int(this Dictionary<string, object> d, string key, int def = 0)
        {
            return (int)d.Long(key, def);
        }

        public static double Double(this Dictionary<string, object> d, string key, double def = 0)
        {
            if (d == null || !d.TryGetValue(key, out object v) || v == null) return def;
            if (v is string s) return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) ? r : def;
            return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }

        public static bool Bool(this Dictionary<string, object> d, string key, bool def = false)
        {
            return d != null && d.TryGetValue(key, out object v) && v is bool b ? b : def;
        }

        public static List<object> List(this Dictionary<string, object> d, string key)
        {
            return d != null && d.TryGetValue(key, out object v) ? v as List<object> : null;
        }

        public static Dictionary<string, object> Obj(this Dictionary<string, object> d, string key)
        {
            return d != null && d.TryGetValue(key, out object v) ? v as Dictionary<string, object> : null;
        }
    }
}
