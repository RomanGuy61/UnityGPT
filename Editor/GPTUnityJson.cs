using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace GPTUnity
{
    /// <summary>
    /// Minimal, dependency-free JSON parser + serializer for the GPTUnity bridge.
    /// Uses Dictionary&lt;string,object&gt; / List&lt;object&gt; as the object model so we
    /// don't depend on Newtonsoft.Json or the limitations of JsonUtility.
    /// </summary>
    public static class Json
    {
        // ---------------- Serialization ----------------

        public static string Serialize(object value)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object v, int depth)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string s) { WriteString(sb, s); return; }
            if (v is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (v is char c) { WriteString(sb, c.ToString()); return; }
            if (v is double d) { sb.Append(FormatNumber(d)); return; }
            if (v is float f) { sb.Append(FormatNumber(f)); return; }
            if (v is decimal m) { sb.Append(FormatNumber((double)m)); return; }
            if (v is int || v is long || v is short || v is byte || v is sbyte || v is uint || v is ushort || v is ulong)
            { sb.Append(Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is IDictionary dict) { WriteDict(sb, dict, depth); return; }
            if (v is IEnumerable enumerable) { WriteList(sb, enumerable, depth); return; }
            if (v is DateTime dt) { WriteString(sb, dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)); return; }
            if (depth > 14) { WriteString(sb, v.ToString()); return; }
            if (v is UnityEngine.Object) { WriteString(sb, v.ToString()); return; }
            if (v.GetType().IsEnum) { WriteString(sb, v.ToString()); return; }
            WriteReflect(sb, v, depth);
        }

        static void WriteDict(StringBuilder sb, IDictionary dict, int depth)
        {
            sb.Append('{');
            bool first = true;
            foreach (DictionaryEntry e in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, e.Key == null ? "null" : e.Key.ToString());
                sb.Append(':');
                WriteValue(sb, e.Value, depth + 1);
            }
            sb.Append('}');
        }

        static void WriteList(StringBuilder sb, IEnumerable items, int depth)
        {
            sb.Append('[');
            bool first = true;
            foreach (object o in items)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteValue(sb, o, depth + 1);
            }
            sb.Append(']');
        }

        static void WriteReflect(StringBuilder sb, object v, int depth)
        {
            try
            {
                var t = v.GetType();
                if (t.Namespace != null && t.Namespace.StartsWith("System.") && t != typeof(System.IO.FileInfo))
                {
                    WriteString(sb, v.ToString());
                    return;
                }
                sb.Append('{');
                bool first = true;
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                foreach (var f in t.GetFields(flags))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, f.Name);
                    sb.Append(':');
                    WriteValue(sb, f.GetValue(v), depth + 1);
                }
                foreach (var p in t.GetProperties(flags))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    if (p.GetGetMethod(false) == null) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, p.Name);
                    sb.Append(':');
                    try { WriteValue(sb, p.GetValue(v, null), depth + 1); }
                    catch { WriteValue(sb, null, depth + 1); }
                }
                sb.Append('}');
            }
            catch
            {
                WriteString(sb, v.ToString());
            }
        }

        static string FormatNumber(double d)
        {
            if (double.IsInfinity(d) || double.IsNaN(d)) return "\"NaN\"";
            string s = d.ToString("R", CultureInfo.InvariantCulture);
            if (!s.Contains(".") && !s.Contains("E") && !s.Contains("e")) s += ".0";
            return s;
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20)
                            sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------- Parsing ----------------

        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int i = 0;
            return ParseValue(json, ref i);
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') i++;
                else break;
            }
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;
            switch (s[i])
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var result = new Dictionary<string, object>();
            i++; // {
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == '}') { i++; break; }
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                object val = ParseValue(s, ref i);
                result[key] = val;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; break; }
            }
            return result;
        }

        static List<object> ParseArray(string s, ref int i)
        {
            var result = new List<object>();
            i++; // [
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == ']') { i++; break; }
                result.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; break; }
            }
            return result;
        }

        static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\' && i < s.Length)
                {
                    char e = s[i++];
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
                            if (i + 4 <= s.Length)
                            {
                                string hex = s.Substring(i, 4);
                                i += 4;
                                try { sb.Append((char)Convert.ToInt32(hex, 16)); }
                                catch { sb.Append('?'); }
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            bool isDouble = false;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E'))
            {
                char c = s[i];
                if (c == '.' || c == 'e' || c == 'E') isDouble = true;
                i++;
            }
            string num = s.Substring(start, i - start);
            if (string.IsNullOrEmpty(num)) return 0d;
            try
            {
                if (!isDouble && long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                    return l;
                return double.Parse(num, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0d;
            }
        }

        static void Expect(string s, ref int i, string word)
        {
            for (int k = 0; k < word.Length; k++)
            {
                if (i < s.Length && s[i] == word[k]) i++;
                else { i += word.Length; break; }
            }
        }

        // ---------------- Access helpers ----------------

        public static Dictionary<string, object> AsDict(object o)
        {
            return o as Dictionary<string, object>;
        }

        public static List<object> AsList(object o)
        {
            return o as List<object>;
        }

        public static string GetString(Dictionary<string, object> d, string key, string fallback = null)
        {
            if (d != null && d.TryGetValue(key, out object v) && v != null) return v.ToString();
            return fallback;
        }

        public static int GetInt(Dictionary<string, object> d, string key, int fallback = 0)
        {
            if (d != null && d.TryGetValue(key, out object v))
            {
                if (v is long l) return (int)l;
                if (v is double dd) return (int)dd;
                if (v is int i) return i;
                if (int.TryParse(v.ToString(), out int n)) return n;
            }
            return fallback;
        }

        public static double GetDouble(Dictionary<string, object> d, string key, double fallback = 0)
        {
            if (d != null && d.TryGetValue(key, out object v))
            {
                if (v is long l) return l;
                if (v is double dd) return dd;
                if (double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return n;
            }
            return fallback;
        }

        public static bool GetBool(Dictionary<string, object> d, string key, bool fallback = false)
        {
            if (d != null && d.TryGetValue(key, out object v))
            {
                if (v is bool b) return b;
                if (v is string str)
                {
                    if (str.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                    if (str.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
                }
                if (v is long l) return l != 0;
            }
            return fallback;
        }

        public static List<object> GetList(Dictionary<string, object> d, string key)
        {
            if (d != null && d.TryGetValue(key, out object v) && v is List<object> l) return l;
            return null;
        }

        public static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            if (d != null && d.TryGetValue(key, out object v) && v is Dictionary<string, object> dd) return dd;
            return null;
        }
    }
}