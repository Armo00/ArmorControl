using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ArmorOverhaul.ArmorControl.Core;

internal static class LocalizationTests
{
    internal static void Run()
    {
        string productRoot = Environment.GetEnvironmentVariable("ARMORCONTROL_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(productRoot))
            throw new Exception("Run Tools/ArmorControl.VerifyRelease.ps1 to configure the test paths.");
        string root = Path.Combine(productRoot, "prototype");
        var localization = new ArmorControlLocalization();
        Check(localization.Text(root, "zh-CN", "Start Server") == "启动服务器", "Chinese server label");
        Check(localization.Text(root, "en-US", "Start Server") == "Start Server", "English server label");
        Check(localization.Text(root, "zh-CN", "Local: ").EndsWith(" "), "keep prefix whitespace");
        Check(localization.Text(root, "invalid/locale", "Start Server") == "Start Server", "English fallback");
        Check(localization.Text(Path.Combine(root, "not-a-real-root"), "zh-CN", "Start Server") == "Start Server", "missing file fallback");
        Check(localization.Text(root, "zh-CN", "Start Server") == "启动服务器", "cache must include root");
        Check(localization.Text(root, "zh-CN", null) == string.Empty, "null source");
        Check(localization.Text(null, "zh-CN", "Start Server") == "Start Server", "null root fallback");
        Check(localization.Text("\0", "zh-CN", "Start Server") == "Start Server", "invalid root fallback");
        foreach (string locale in new[] { "en-US", "zh-CN" })
        {
            string json = File.ReadAllText(Path.Combine(root, "Localization", locale + ".json"));
            var parsed = ArmorControlLocalization.ParseMessages(json, locale);
            // Independent JSON implementation exists in this net8 test host only.
            using (var oracle = JsonDocument.Parse(json))
            {
                var expected = oracle.RootElement.GetProperty("messages");
                int count = 0;
                foreach (var item in expected.EnumerateObject())
                {
                    Check(parsed[item.Name] == item.Value.GetString(), "catalog value: " + item.Name);
                    count++;
                }
                Check(parsed.Count == count, "complete catalog");
            }
        }
        string escaped = "{\"messages\":{\"Key\":\"中文 \\u4e2d\\u6587 \\uD83D\\uDE80 \\\"\\\\\\/\\b\\f\\n\\r\\t\"},\"locale\":\"zh-CN\"}";
        Check(ArmorControlLocalization.ParseMessages(escaped, "zh-CN")["Key"] == "中文 中文 🚀 \"\\/\b\f\n\r\t", "Unicode and all JSON escapes");
        foreach (string invalid in new[] {
            "{}", "[]", "{\"locale\":\"en-US\",\"messages\":{}}", // locale mismatch
            "{\"locale\":\"zh-CN\",\"messages\":{\"Key\":null}}",
            "{\"locale\":\"zh-CN\",\"messages\":{\"Key\":2}}",
            "{\"locale\":\"zh-CN\",\"messages\":{\"Key\":\"a\",\"Key\":\"b\"}}",
            "{\"locale\":\"zh-CN\",\"locale\":\"zh-CN\",\"messages\":{}}",
            "{\"locale\":\"zh-CN\",\"messages\":{},}",
            "{\"locale\":\"zh-CN\",\"messages\":{}} trailing",
            escaped.Replace("\\u4e2d", "\\uXXXX"), escaped.Replace("\\u4e2d", "\\x20"),
            escaped.Replace("\\n", "\n"), escaped.Substring(0, escaped.Length - 2)
        })
        {
            bool rejected = false;
            try { ArmorControlLocalization.ParseMessages(invalid, "zh-CN"); }
            catch (FormatException) { rejected = true; }
            Check(rejected, "reject malformed catalog");
        }
        string fixture = Path.Combine(Path.GetTempPath(), "ArmorControl-l10n-" + Guid.NewGuid().ToString("N"));
        try
        {
            string directory = Path.Combine(fixture, "Localization");
            Directory.CreateDirectory(directory);
            string catalog = Path.Combine(directory, "zh-CN.json");
            File.WriteAllText(catalog, escaped, new UTF8Encoding(true));
            Check(new ArmorControlLocalization().Text(fixture, "zh-CN", "Key").StartsWith("中文 中文"), "UTF8 BOM file");
            File.WriteAllText(catalog, "{\"locale\":\"zh-CN\",\"messages\":{\"Key\":\"partial\",}}", Encoding.UTF8);
            Check(new ArmorControlLocalization().Text(fixture, "zh-CN", "Key") == "Key", "malformed file atomic fallback");
        }
        finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
        CheckPublishedAssembly(Environment.GetEnvironmentVariable("ARMORCONTROL_TEST_DLL") ??
            Path.GetFullPath(Path.Combine(root, "..", "Plugins", "ArmorControl.dll")));
        Console.WriteLine("PASS native localization: complete catalogs, Unicode/escapes, malformed JSON, directory/locale fallback, cache");
    }

    private static void CheckPublishedAssembly(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var pe = new PEReader(stream))
        {
            var metadata = pe.GetMetadataReader();
            bool foundMechJeb = false, foundVesselView = false;
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                string name = metadata.GetString(reference.Name);
                Check(name != "System.Runtime.Serialization" && name != "System.Text.Json", "no unavailable serializer assembly reference");
                if (name == "MechJeb2")
                {
                    foundMechJeb = true;
                    // MJ 2.14.3 has FileVersion 2.14.3.0 but AssemblyVersion 2.5.1.0.
                    // The build gate pins the file version; do not confuse the two.
                    Check(reference.Version == new Version(2, 5, 1, 0), "MechJeb local assembly identity");
                }
                if (name == "VesselView") foundVesselView = true;
            }
            Check(foundMechJeb && foundVesselView, "required direct assembly dependencies");
            foreach (var handle in metadata.TypeReferences)
            {
                var type = metadata.GetTypeReference(handle);
                Check(!metadata.GetString(type.Namespace).StartsWith("System.Runtime.Serialization", StringComparison.Ordinal),
                    "no serializer types or attributes in DLL metadata");
            }
        }
        Console.WriteLine("PASS published DLL metadata: no serializer assembly, type or attribute references");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("Localization regression: " + name);
    }
}
