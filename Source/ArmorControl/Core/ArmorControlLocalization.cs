using System;
using System.Collections.Generic;
using System.IO;
using ArmorOverhaul.ArmorControl.Protocol;

namespace ArmorOverhaul.ArmorControl.Core
{
    // Shared with the web UI. Cache lookups; never read disk during every GUI repaint.
    internal sealed class ArmorControlLocalization
    {
        private string locale;
        private string catalog;
        private readonly Dictionary<string, string> cache = new Dictionary<string, string>();
        internal string Text(string root, string language, string source)
        {
            if (locale != language)
            {
                locale = language;
                cache.Clear();
                catalog = null;
                try { catalog = File.ReadAllText(Path.Combine(root, "Localization", language == "en-US" ? "en-US.json" : "zh-CN.json")); }
                catch (Exception) { /* English source remains usable if the catalog is missing. */ }
            }
            string result;
            if (!cache.TryGetValue(source, out result))
            {
                result = catalog == null ? source : WireProtocol.ReadString(catalog, source) ?? source;
                cache[source] = result;
            }
            return result;
        }
    }
}
