// ---------------------------------------------------------------------------
//  Settings, stored as a flat JSON file next to the executable so the whole
//  folder stays portable: copy it to another machine and the chosen hotkey
//  travels with it.
//
//  The reader and writer are hand-rolled rather than pulled from a serializer.
//  The file is flat, people edit it by hand, and a forgiving parser is worth
//  more here than a strict one -- plus it keeps the executable dependency-free.
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KbFix
{
    internal sealed class Settings
    {
        public string Hotkey = "Win+Space";
        public bool SmartSelection = true;
        public bool SwitchLanguage = true;

        /// Safety cap on how much text Smart Selection may re-select, in
        /// characters. Only stops runaway cases; the word limit below is the one
        /// that shapes normal behaviour.
        public int MaxSmartChars = 300;

        /// How many words before the caret Smart Selection may take. One by
        /// default, because text typed on the wrong layout looks exactly like
        /// text meant for that layout -- see the note on KbFix.SmartSelection.
        public int MaxSmartWords = 1;

        public static string PathFor(string folder) { return Path.Combine(folder, "settings.json"); }

        public static Settings Load(string folder, out string problem)
        {
            problem = null;
            Settings s = new Settings();
            string path = PathFor(folder);
            if (!File.Exists(path)) return s;

            try
            {
                Dictionary<string, string> raw = ParseFlatJson(File.ReadAllText(path, Encoding.UTF8));
                string v;
                if (raw.TryGetValue("hotkey", out v) && !string.IsNullOrEmpty(v)) s.Hotkey = v;
                if (raw.TryGetValue("smartselection", out v)) s.SmartSelection = AsBool(v, s.SmartSelection);
                if (raw.TryGetValue("switchlanguage", out v)) s.SwitchLanguage = AsBool(v, s.SwitchLanguage);
                if (raw.TryGetValue("maxsmartchars", out v)) s.MaxSmartChars = AsInt(v, s.MaxSmartChars);
                if (raw.TryGetValue("maxsmartwords", out v)) s.MaxSmartWords = AsInt(v, s.MaxSmartWords);
                if (s.MaxSmartChars < 10 || s.MaxSmartChars > 5000) s.MaxSmartChars = 300;
                if (s.MaxSmartWords < 1 || s.MaxSmartWords > 50) s.MaxSmartWords = 1;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
            }
            return s;
        }

        public bool Save(string folder, out string problem)
        {
            problem = null;
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"Hotkey\": " + Quote(Hotkey) + ",");
                sb.AppendLine("  \"SmartSelection\": " + (SmartSelection ? "true" : "false") + ",");
                sb.AppendLine("  \"SwitchLanguage\": " + (SwitchLanguage ? "true" : "false") + ",");
                sb.AppendLine("  \"MaxSmartChars\": " + MaxSmartChars.ToString(CultureInfo.InvariantCulture) + ",");
                sb.AppendLine("  \"MaxSmartWords\": " + MaxSmartWords.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("}");
                File.WriteAllText(PathFor(folder), sb.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                return false;
            }
        }

        private static bool AsBool(string v, bool fallback)
        {
            if (v == null) return fallback;
            string t = v.Trim().ToLowerInvariant();
            if (t == "true" || t == "1" || t == "yes") return true;
            if (t == "false" || t == "0" || t == "no") return false;
            return fallback;
        }

        private static int AsInt(string v, int fallback)
        {
            int n;
            if (int.TryParse((v ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            return fallback;
        }

        private static string Quote(string s)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') { sb.Append('\\'); sb.Append(c); }
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        /// Reads a one-level JSON object into lower-cased keys mapped to their
        /// raw string values. Nested objects and arrays are skipped rather than
        /// treated as an error, so an extended file written by a later version
        /// still loads here.
        internal static Dictionary<string, string> ParseFlatJson(string text)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();
            if (text == null) return result;

            int i = 0;
            SkipWs(text, ref i);
            if (i < text.Length && text[i] == '{') i++;

            while (i < text.Length)
            {
                SkipWs(text, ref i);
                if (i >= text.Length || text[i] == '}') break;

                if (text[i] != '"') { i++; continue; }        // tolerate junk
                string key = ReadString(text, ref i);

                SkipWs(text, ref i);
                if (i < text.Length && text[i] == ':') i++;
                SkipWs(text, ref i);
                if (i >= text.Length) break;

                string value;
                if (text[i] == '"') value = ReadString(text, ref i);
                else if (text[i] == '{' || text[i] == '[') { SkipNested(text, ref i); value = null; }
                else
                {
                    int start = i;
                    while (i < text.Length && text[i] != ',' && text[i] != '}' && !char.IsWhiteSpace(text[i])) i++;
                    value = text.Substring(start, i - start);
                }

                if (value != null && key != null) result[key.ToLowerInvariant()] = value;

                SkipWs(text, ref i);
                if (i < text.Length && text[i] == ',') i++;
            }
            return result;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static void SkipNested(string s, ref int i)
        {
            char open = s[i];
            char close = open == '{' ? '}' : ']';
            int depth = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"') { ReadString(s, ref i); continue; }
                if (c == open) depth++;
                else if (c == close) { depth--; i++; if (depth == 0) return; continue; }
                i++;
            }
        }

        private static string ReadString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') return null;
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case '/': sb.Append('/'); break;
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case 'u':
                        if (i + 4 <= s.Length)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber,
                                             CultureInfo.InvariantCulture, out code)) sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }
    }
}
