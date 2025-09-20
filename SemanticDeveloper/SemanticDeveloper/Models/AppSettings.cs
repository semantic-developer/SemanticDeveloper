namespace SemanticDeveloper.Models;

public class AppSettings
{
    public string Command { get; set; } = "codex";
    public bool VerboseLoggingEnabled { get; set; } = false;
    public bool UseApiKey { get; set; } = false;
    public string ApiKey { get; set; } = string.Empty;
    public bool McpEnabled { get; set; } = false;
    public bool AllowNetworkAccess { get; set; } = true;
    public bool ShowMcpResultsInLog { get; set; } = true;
    public bool ShowMcpResultsOnlyWhenNoEdits { get; set; } = true;
    public string SelectedProfile { get; set; } = string.Empty;
}
