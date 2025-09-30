using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SemanticDeveloper.Views;

public class CliSettings
{
    public string Command { get; set; } = "codex"; // kept for backwards-compatibility
    public bool VerboseLoggingEnabled { get; set; } = false;
    public bool McpEnabled { get; set; } = false;
    public bool UseApiKey { get; set; } = false;
    public string ApiKey { get; set; } = string.Empty;
    public bool AllowNetworkAccess { get; set; } = true;
    public bool ShowMcpResultsInLog { get; set; } = true;
    public bool ShowMcpResultsOnlyWhenNoEdits { get; set; } = true;
    public System.Collections.Generic.List<string> Profiles { get; set; } = new();
    public string SelectedProfile { get; set; } = string.Empty;
    public bool UseWsl { get; set; } = false;
    public bool CanUseWsl { get; set; } = OperatingSystem.IsWindows();
}

public partial class CliSettingsDialog : Window
{
    public CliSettingsDialog()
    {
        InitializeComponent();
    }

    private CliSettings ViewModel => (DataContext as CliSettings) ?? new CliSettings();

    private void OnSave(object? sender, RoutedEventArgs e)
        => Close(ViewModel);

    private void OnCancel(object? sender, RoutedEventArgs e)
        => Close(null);
}
