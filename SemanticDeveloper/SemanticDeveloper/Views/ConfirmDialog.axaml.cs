using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SemanticDeveloper.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public string Message
    {
        get => MessageText.Text ?? string.Empty;
        set => MessageText.Text = value;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
        => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e)
        => Close(false);
}

