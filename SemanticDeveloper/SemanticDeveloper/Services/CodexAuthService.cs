using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace SemanticDeveloper.Services;

public static class CodexAuthService
{
    public static string GetAuthJsonPath()
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME");
            string dir;
            if (!string.IsNullOrWhiteSpace(home)) dir = home!;
            else dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            return Path.Combine(dir, "auth.json");
        }
        catch
        {
            return Path.Combine(Directory.GetCurrentDirectory(), "auth.json");
        }
    }

    // Reads auth.json and reports whether token-based auth exists and any api key value found
    public static (bool Exists, bool HasTokens, string? ApiKey) ProbeAuth()
    {
        try
        {
            var path = GetAuthJsonPath();
            if (!File.Exists(path)) return (false, false, null);
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return (true, false, null);
            var obj = JObject.Parse(text);

            string? apiKey = null;
            var apiVal = obj["OPENAI_API_KEY"];
            if (apiVal != null && apiVal.Type == JTokenType.String)
            {
                var s = apiVal.ToString();
                if (!string.IsNullOrWhiteSpace(s)) apiKey = s;
            }

            bool hasTokens = false;
            if (obj["tokens"] is JObject tok)
            {
                var id = tok["id_token"]?.ToString();
                var access = tok["access_token"]?.ToString();
                hasTokens = !string.IsNullOrWhiteSpace(id) || !string.IsNullOrWhiteSpace(access);
            }

            return (true, hasTokens, apiKey);
        }
        catch
        {
            return (false, false, null);
        }
    }
}

