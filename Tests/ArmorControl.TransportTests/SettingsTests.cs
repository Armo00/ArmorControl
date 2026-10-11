using System;
using System.IO;
using ArmorOverhaul.ArmorControl.Core;

// The KSP ConfigNode loader is not exercised in this offline save-path test.
internal sealed class ConfigNode
{
    internal static ConfigNode Load(string path) { throw new NotSupportedException("KSP-only loader"); }
    internal ConfigNode GetNode(string name) { throw new NotSupportedException(); }
    internal string GetValue(string name) { throw new NotSupportedException(); }
}

internal static class SettingsTests
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "ArmorControl-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new ArmorControlSettings { Port = 18765 };
            string path = Path.Combine(root, "settings.cfg"), error;
            Check(settings.TrySaveLanguage("zh-CN", path, out error), "create settings");
            Check(File.ReadAllText(path).Contains("language = zh-CN"), "Chinese saved");
            Check(settings.TrySaveLanguage("en-US", path, out error), "replace existing settings");
            string saved = File.ReadAllText(path);
            Check(saved.Contains("language = en-US") && saved.Contains("port = 18765"), "preserve other settings");
            Check(!settings.TrySaveLanguage("invalid", path, out error), "invalid choice rejected");
            Check(File.ReadAllText(path) == saved && settings.Language == "en-US", "invalid choice leaves disk and memory unchanged");
            string blocked = Path.Combine(root, "blocked");
            Directory.CreateDirectory(blocked);
            Check(!settings.TrySaveLanguage("zh-CN", blocked, out error), "failed write reported");
            Check(settings.Language == "en-US" && File.ReadAllText(path) == saved, "failed write rollback");
            Check(Directory.GetFiles(root, "*.tmp").Length == 0, "temporary files cleaned");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("PASS actual settings save: create/atomic replace, preserved values, validation and write-failure rollback");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Settings regression: " + message);
    }
}
