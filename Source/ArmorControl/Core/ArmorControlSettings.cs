using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace ArmorOverhaul.ArmorControl.Core
{
    internal sealed class ArmorControlSettings
    {
        internal bool Enabled = true;
        internal bool AutoStartServer = true;
        internal IPAddress BindAddress = IPAddress.Any;
        internal int Port = 8765;
        internal int FastTelemetryHz = 30;
        internal int RegularTelemetryHz = 5;
        internal string AccessToken = string.Empty;
        internal string Language = "zh-CN";

        internal static ArmorControlSettings Load(string path, Action<string> warn)
        {
            var settings = new ArmorControlSettings();
            try
            {
                ConfigNode loaded = ConfigNode.Load(path);
                if (loaded == null)
                {
                    warn("settings.cfg not found; using defaults.");
                    return settings;
                }

                ConfigNode node = loaded.GetNode("ARMOR_CONTROL") ?? loaded;
                bool enabled;
                if (bool.TryParse(node.GetValue("enabled"), out enabled)) settings.Enabled = enabled;

                bool autoStartServer;
                if (bool.TryParse(node.GetValue("autoStartServer"), out autoStartServer)) settings.AutoStartServer = autoStartServer;

                IPAddress address;
                string bindAddress = node.GetValue("bindAddress");
                if (!string.IsNullOrWhiteSpace(bindAddress) && IPAddress.TryParse(bindAddress, out address)) settings.BindAddress = address;
                else if (!string.IsNullOrWhiteSpace(bindAddress)) warn("invalid bindAddress; using 0.0.0.0.");

                settings.Port = ReadInt(node, "port", settings.Port, 1024, 65535, warn);
                settings.FastTelemetryHz = ReadInt(node, "fastTelemetryHz", settings.FastTelemetryHz, 1, 60, warn);
                settings.RegularTelemetryHz = ReadInt(node, "regularTelemetryHz", settings.RegularTelemetryHz, 1, 20, warn);
                string accessToken = node.GetValue("accessToken");
                string language = node.GetValue("language");
                if (language == "zh-CN" || language == "en-US") settings.Language = language;
                if (accessToken != null) settings.AccessToken = accessToken.Trim();
            }
            catch (Exception exception)
            {
                warn("failed to load settings.cfg; using defaults: " + exception.Message);
            }
            return settings;
        }

        internal void Save(string path)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var text = new StringBuilder();
            text.AppendLine("ARMOR_CONTROL");
            text.AppendLine("{");
            text.AppendLine("    enabled = " + Enabled);
            text.AppendLine("    autoStartServer = " + AutoStartServer);
            text.AppendLine("    bindAddress = " + BindAddress);
            text.AppendLine("    port = " + Port.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("    fastTelemetryHz = " + FastTelemetryHz.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("    regularTelemetryHz = " + RegularTelemetryHz.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("    // Empty disables authentication. Only use this mode on a trusted local network.");
            text.AppendLine("    accessToken = " + AccessToken);
            text.AppendLine("    language = " + Language);
            text.AppendLine("}");
            File.WriteAllText(path, text.ToString());
        }

        private static int ReadInt(ConfigNode node, string name, int fallback, int minimum, int maximum, Action<string> warn)
        {
            int value;
            string raw = node.GetValue(name);
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= minimum && value <= maximum)
            {
                return value;
            }
            if (!string.IsNullOrWhiteSpace(raw)) warn(name + " is outside " + minimum + ".." + maximum + "; using " + fallback + ".");
            return fallback;
        }
    }
}
