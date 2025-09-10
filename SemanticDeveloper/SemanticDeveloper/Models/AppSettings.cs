namespace SemanticDeveloper.Models;

public class AppSettings
{
    public string Command { get; set; } = "codex";
    public string AdditionalArgs { get; set; } = string.Empty;
    public bool AutoApproveEnabled { get; set; } = true;
}
