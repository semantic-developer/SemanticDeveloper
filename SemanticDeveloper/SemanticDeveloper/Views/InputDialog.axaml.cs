using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SemanticDeveloper.Views;

public partial class InputDialog : Window
{
    public InputDialog()
    {
        InitializeComponent();
    }

    public string Prompt
    {
        get => PromptText.Text ?? string.Empty;
        set => PromptText.Text = value;
    }

    public string Input
    {
        get => InputText.Text ?? string.Empty;
        set => InputText.Text = value;
    }

    public bool ShowCreatePullRequestOption
    {
        get => CreatePrCheckBox.IsVisible;
        set => CreatePrCheckBox.IsVisible = value;
    }

    public bool CreatePullRequest
    {
        get => CreatePrCheckBox.IsChecked ?? false;
        set => CreatePrCheckBox.IsChecked = value;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
        => Close(new InputDialogResult
        {
            Text = InputText.Text ?? string.Empty,
            CreatePullRequest = CreatePullRequest
        });

    private void OnCancel(object? sender, RoutedEventArgs e)
        => Close(null);
}

public sealed class InputDialogResult
{
    public string Text { get; set; } = string.Empty;
    public bool CreatePullRequest { get; set; }
}
