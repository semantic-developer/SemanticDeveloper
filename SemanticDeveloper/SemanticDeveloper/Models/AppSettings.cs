namespace SemanticDeveloper.Models;

public class AppSettings
{
    public string Command { get; set; } = "codex";
    public string AdditionalArgs { get; set; } = string.Empty;
    public bool VerboseLoggingEnabled { get; set; } = false;
}
