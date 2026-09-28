using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ArmorOverhaul.ArmorControl.Core
{
    internal sealed class ArmorControlLocalization
    {
        private string loadedPath;
        private Dictionary<string, string> messages = new Dictionary<string, string>();

        internal string Text(string root, string language, string source)
        {
            source = source ?? string.Empty;
            if (string.IsNullOrEmpty(root)) return source;
            // Unknown configuration values use English, never an arbitrary file path.
            string locale = language == "zh-CN" ? "zh-CN" : "en-US";
            string path;
            try { path = Path.Combine(root, "Localization", locale + ".json"); }
            catch (ArgumentException) { return source; }
            if (loadedPath != path)
            {
                loadedPath = path;
                messages = new Dictionary<string, string>();
                try
                {
                    messages = ParseMessages(File.ReadAllText(path, new UTF8Encoding(false, true)), locale);
                }
                catch (Exception) { /* Missing/malformed catalog: English source stays usable. */ }
            }
            string key = source.Trim(), translated;
            if (key.Length == 0 || !messages.TryGetValue(key, out translated) || translated == null) return source;
            int start = source.IndexOf(key, StringComparison.Ordinal);
            return source.Substring(0, start) + translated + source.Substring(start + key.Length);
        }

        // Deliberately schema-specific: only locale + a string-to-string messages object.
        // No serializer dependencies. Attributes on even private nested types
        // would force KSP's other mods to resolve their assembly during reflection.
        internal static Dictionary<string, string> ParseMessages(string json, string locale)
        {
            var reader = new CatalogReader(json);
            string actualLocale = null;
            Dictionary<string, string> result = null;
            reader.ReadObject(key =>
            {
                if (key == "locale") actualLocale = reader.ReadString();
                else if (key == "messages")
                {
                    result = new Dictionary<string, string>(StringComparer.Ordinal);
                    reader.ReadObject(message => result.Add(message, reader.ReadString()));
                }
                else throw new FormatException("Unknown localization catalog field.");
            });
            reader.End();
            if (actualLocale != locale || result == null) throw new FormatException("Invalid localization catalog.");
            return result;
        }

        private sealed class CatalogReader
        {
            private readonly string json;
            private int position;

            internal CatalogReader(string json)
            {
                if (json == null) throw new FormatException("Missing JSON.");
                this.json = json;
            }

            private void Whitespace()
            {
                while (position < json.Length && (json[position] == ' ' || json[position] == '\t' ||
                    json[position] == '\r' || json[position] == '\n')) position++;
            }

            private bool Take(char value)
            {
                Whitespace();
                if (position >= json.Length || json[position] != value) return false;
                position++;
                return true;
            }

            private void Expect(char value)
            {
                if (!Take(value)) throw new FormatException("Invalid JSON delimiter.");
            }

            internal void End()
            {
                Whitespace();
                if (position != json.Length) throw new FormatException("Trailing JSON content.");
            }

            internal void ReadObject(Action<string> value)
            {
                Expect('{');
                var keys = new HashSet<string>(StringComparer.Ordinal);
                if (Take('}')) return;
                do
                {
                    string key = ReadString();
                    if (!keys.Add(key)) throw new FormatException("Duplicate JSON key.");
                    Expect(':');
                    value(key);
                    if (Take('}')) return;
                    Expect(',');
                } while (true);
            }

            internal string ReadString()
            {
                Expect('"');
                var text = new StringBuilder();
                while (position < json.Length)
                {
                    char c = json[position++];
                    if (c == '"') return text.ToString();
                    if (c < 0x20) throw new FormatException("Unescaped JSON control character.");
                    if (c != '\\') { text.Append(c); continue; }
                    if (position >= json.Length) break;
                    switch (json[position++])
                    {
                        case '"': text.Append('"'); break;
                        case '\\': text.Append('\\'); break;
                        case '/': text.Append('/'); break;
                        case 'b': text.Append('\b'); break;
                        case 'f': text.Append('\f'); break;
                        case 'n': text.Append('\n'); break;
                        case 'r': text.Append('\r'); break;
                        case 't': text.Append('\t'); break;
                        case 'u':
                            int code = 0;
                            for (int i = 0; i < 4; i++)
                            {
                                if (position >= json.Length) throw new FormatException("Incomplete Unicode escape.");
                                char hex = json[position++];
                                int digit = hex >= '0' && hex <= '9' ? hex - '0' :
                                    hex >= 'a' && hex <= 'f' ? hex - 'a' + 10 :
                                    hex >= 'A' && hex <= 'F' ? hex - 'A' + 10 : -1;
                                if (digit < 0) throw new FormatException("Invalid Unicode escape.");
                                code = code * 16 + digit;
                            }
                            text.Append((char)code);
                            break;
                        default: throw new FormatException("Invalid JSON escape.");
                    }
                }
                throw new FormatException("Unterminated JSON string.");
            }
        }
    }
}
