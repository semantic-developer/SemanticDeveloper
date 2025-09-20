using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SemanticDeveloper.Views;

public partial class SelectOptionDialog : Window
{
    public SelectOptionDialog()
    {
        InitializeComponent();
    }

    public string Prompt
    {
        get => PromptText.Text ?? string.Empty;
        set => PromptText.Text = value;
    }

    private List<string> _options = new();
    public List<string> Options
    {
        get => _options;
        set
        {
            _options = value ?? new();
            try
            {
                OptionsList.ItemsSource = _options;
                if (_options.Count > 0)
                    OptionsList.SelectedIndex = 0;
            }
            catch { }
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var idx = OptionsList.SelectedIndex;
        Close(idx);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
