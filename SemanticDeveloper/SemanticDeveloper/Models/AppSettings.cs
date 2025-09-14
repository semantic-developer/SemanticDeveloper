namespace SemanticDeveloper.Models;

public class AppSettings
{
    public string Command { get; set; } = "codex";
    public string AdditionalArgs { get; set; } = string.Empty;
    public bool VerboseLoggingEnabled { get; set; } = false;
    public bool UseApiKey { get; set; } = false;
    public string ApiKey { get; set; } = string.Empty;
}
