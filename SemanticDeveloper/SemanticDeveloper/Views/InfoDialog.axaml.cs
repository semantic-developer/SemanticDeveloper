using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SemanticDeveloper.Views;

public partial class InfoDialog : Window
{
    public InfoDialog()
    {
        InitializeComponent();
    }

    public string Message
    {
        get => MessageText.Text ?? string.Empty;
        set => MessageText.Text = value;
    }

    private void OnClose(object? sender, RoutedEventArgs e)
        => Close();
}
