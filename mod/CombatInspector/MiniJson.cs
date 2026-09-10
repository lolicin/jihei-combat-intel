using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace CombatInspector
{
    /// <summary>
    /// A tiny dependency-free JSON writer.
    ///
    /// Why this exists: the mod originally serialised with Newtonsoft.Json because the game shipped
    /// it. A game update then REMOVED Newtonsoft.Json.dll from the Managed folder entirely, which
    /// would have made every serialisation call throw FileNotFoundException at runtime. Relying on
    /// assemblies the game may add or drop at will is fragile, so the mod now owns its own writer.
    ///
    /// Supports exactly what the snapshots and the reflective deep dump produce:
    /// null / bool / string / char / enum / all numeric types / DateTime,
    /// IDictionary (object), IEnumerable (array), and anything else via its public instance
    /// fields then public readable properties. Non-finite floats become null (valid JSON),
    /// reference cycles become "&lt;circular&gt;", and depth is capped.
    /// </summary>
    public static class MiniJson
    {
        public const int MaxDepth = 12;

        public static string Serialize(object root, bool indent)
        {
            var sb = new StringBuilder(8192);
            var seen = new HashSet<object>(RefEq.Instance);
            Write(sb, root, 0, indent, seen);
            return sb.ToString();
        }

        // ---------------------------------------------------------------- core

        private static void Write(StringBuilder sb, object v, int depth, bool indent, HashSet<object> seen)
        {
            if (v == null) { sb.Append("null"); return; }
            if (depth > MaxDepth) { sb.Append("\"<depth limit>\""); return; }

            var t = v.GetType();

            if (v is string s) { WriteString(sb, s); return; }
            if (v is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (v is char ch) { WriteString(sb, ch.ToString()); return; }
            if (t.IsEnum) { WriteString(sb, v.ToString()); return; }

            if (v is float f) { WriteFloat(sb, f); return; }
            if (v is double d) { WriteFloat(sb, d); return; }
            if (v is decimal dec) { sb.Append(dec.ToString(CultureInfo.InvariantCulture)); return; }
            if (t.IsPrimitive) { sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return; }
            if (v is DateTime dt) { WriteString(sb, dt.ToString("o", CultureInfo.InvariantCulture)); return; }
            if (v is Guid g) { WriteString(sb, g.ToString()); return; }

            bool isRef = !t.IsValueType;
            if (isRef && !seen.Add(v)) { sb.Append("\"<circular>\""); return; }

            try
            {
                var dict = v as IDictionary;
                if (dict != null) { WriteDict(sb, dict, depth, indent, seen); return; }

                var en = v as IEnumerable;
                if (en != null) { WriteArray(sb, en, depth, indent, seen); return; }

                WriteMembers(sb, v, t, depth, indent, seen);
            }
            finally
            {
                if (isRef) seen.Remove(v);
            }
        }

        private static void WriteFloat(StringBuilder sb, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) { sb.Append("null"); return; }
            sb.Append(v.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteDict(StringBuilder sb, IDictionary dict, int depth, bool indent, HashSet<object> seen)
        {
            sb.Append('{');
            bool first = true;
            foreach (DictionaryEntry e in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                NewLine(sb, depth + 1, indent);
                WriteString(sb, e.Key == null ? "null" : Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                sb.Append(':');
                if (indent) sb.Append(' ');
                Write(sb, e.Value, depth + 1, indent, seen);
            }
            if (!first) NewLine(sb, depth, indent);
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, IEnumerable en, int depth, bool indent, HashSet<object> seen)
        {
            sb.Append('[');
            bool first = true;
            foreach (var item in en)
            {
                if (!first) sb.Append(',');
                first = false;
                NewLine(sb, depth + 1, indent);
                Write(sb, item, depth + 1, indent, seen);
            }
            if (!first) NewLine(sb, depth, indent);
            sb.Append(']');
        }

        private static void WriteMembers(StringBuilder sb, object v, Type t, int depth, bool indent, HashSet<object> seen)
        {
            var pairs = new List<KeyValuePair<string, object>>(16);

            FieldInfo[] fields;
            try { fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance); }
            catch { fields = new FieldInfo[0]; }
            for (int i = 0; i < fields.Length; i++)
            {
                object val;
                try { val = fields[i].GetValue(v); }
                catch { continue; }
                pairs.Add(new KeyValuePair<string, object>(fields[i].Name, val));
            }

            PropertyInfo[] props;
            try { props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance); }
            catch { props = new PropertyInfo[0]; }
            for (int i = 0; i < props.Length; i++)
            {
                var p = props[i];
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                object val;
                try { val = p.GetValue(v, null); }
                catch { continue; }
                pairs.Add(new KeyValuePair<string, object>(p.Name, val));
            }

            if (pairs.Count == 0)
            {
                // Zero-sized tag structs and similar: keep them visible but empty.
                sb.Append("{}");
                return;
            }

            sb.Append('{');
            for (int i = 0; i < pairs.Count; i++)
            {
                if (i > 0) sb.Append(',');
                NewLine(sb, depth + 1, indent);
                WriteString(sb, pairs[i].Key);
                sb.Append(':');
                if (indent) sb.Append(' ');
                Write(sb, pairs[i].Value, depth + 1, indent, seen);
            }
            NewLine(sb, depth, indent);
            sb.Append('}');
        }

        // ---------------------------------------------------------------- primitives

        private static void NewLine(StringBuilder sb, int depth, bool indent)
        {
            if (!indent) return;
            sb.Append('\n');
            for (int i = 0; i < depth; i++) sb.Append("  ");
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class RefEq : IEqualityComparer<object>
        {
            public static readonly RefEq Instance = new RefEq();
            public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object obj) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj); }
        }
    }
}
