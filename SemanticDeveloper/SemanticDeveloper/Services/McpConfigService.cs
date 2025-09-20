using System;
using System.IO;

namespace SemanticDeveloper.Services;

public static class McpConfigService
{
    public static string GetConfigPath()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SemanticDeveloper");
            Directory.CreateDirectory(dir);
            var newPath = Path.Combine(dir, "mcp_servers.json");
            var legacyPath = Path.Combine(dir, "mcp_mux.json");
            // Migrate legacy file name -> new file name
            try
            {
                if (!File.Exists(newPath) && File.Exists(legacyPath))
                {
                    File.Copy(legacyPath, newPath, overwrite: false);
                }
            }
            catch { }
            return newPath;
        }
        catch
        {
            return Path.Combine(Directory.GetCurrentDirectory(), "mcp_servers.json");
        }
    }

    public static void EnsureConfigExists()
    {
        var path = GetConfigPath();
        if (File.Exists(path)) return;
        try
        {
            var sample = "{\n  \"servers\": [\n    {\n      \"name\": \"playwright\",\n      \"command\": \"npx\",\n      \"args\": [\"@playwright/mcp@latest\"],\n      \"enabled\": true\n    }\n  ]\n}\n";
            File.WriteAllText(path, sample);
        }
        catch { }
    }
}
