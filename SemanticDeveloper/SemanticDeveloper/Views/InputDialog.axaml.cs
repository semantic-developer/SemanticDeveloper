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

    private void OnOk(object? sender, RoutedEventArgs e)
        => Close(InputText.Text);

    private void OnCancel(object? sender, RoutedEventArgs e)
        => Close(null);
}

