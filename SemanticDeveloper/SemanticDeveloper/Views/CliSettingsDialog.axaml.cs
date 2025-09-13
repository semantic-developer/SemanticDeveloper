using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SemanticDeveloper.Views;

public class CliSettings
{
    public string Command { get; set; } = "codex";
    public string AdditionalArgs { get; set; } = string.Empty;
    public bool VerboseLoggingEnabled { get; set; } = false;
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
