using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SemanticDeveloper.Models;
using SemanticDeveloper.Services;
using SemanticDeveloper.Views;

namespace SemanticDeveloper;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly ObservableCollection<SessionTab> _sessions = new();
    private SessionTab? _selectedSession;
    private int _sessionCounter = 0;
    private AppSettings _sharedSettings;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _sharedSettings = CloneAppSettings(SettingsService.Load());
        AddSession(applySharedSettings: true);
    }

    public ObservableCollection<SessionTab> Sessions => _sessions;

    public SessionTab? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (_selectedSession == value)
                return;
            _selectedSession = value;
            if (_selectedSession is not null)
            {
                _sharedSettings = CloneAppSettings(_selectedSession.View.GetCurrentSettings());
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedSessionTitle));
        }
    }

    public string SelectedSessionTitle => SelectedSession?.Header ?? "No session";

    private void OnAddSessionClick(object? sender, RoutedEventArgs e) => AddSession(applySharedSettings: true);

    private void AddSession(bool applySharedSettings)
    {
        var title = $"Session {++_sessionCounter}";
        var view = new SessionView();
        if (applySharedSettings)
        {
            view.ApplySettingsSnapshot(_sharedSettings);
        }
        var tab = new SessionTab(title, view);
        view.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SessionView.SessionStatus))
            {
                tab.UpdateHeader();
            }
        };
        tab.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SessionTab.Header) && ReferenceEquals(SelectedSession, tab))
                OnPropertyChanged(nameof(SelectedSessionTitle));
        };
        tab.UpdateHeader();
        _sessions.Add(tab);
        SelectedSession = tab;
        _sharedSettings = CloneAppSettings(view.GetCurrentSettings());
    }

    private async void OnOpenCliSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedSession is null)
            return;

        var updated = await SelectedSession.View.ShowCliSettingsDialogAsync(_sharedSettings);
        if (updated is null)
            return;

        _sharedSettings = CloneAppSettings(updated);
        foreach (var session in _sessions)
        {
            if (session == SelectedSession)
                continue;
            session.View.ApplySettingsSnapshot(_sharedSettings);
        }
    }

    private async void OnOpenAboutClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new AboutDialog();
        await dialog.ShowDialog(this);
    }

    private void OnOpenReadmeClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            const string readmeUrl = "https://github.com/semantic-developer/SemanticDeveloper?tab=readme-ov-file";
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo { FileName = readmeUrl, UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", readmeUrl);
            }
            else
            {
                Process.Start("xdg-open", readmeUrl);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open README: {ex.Message}");
        }
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static AppSettings CloneAppSettings(AppSettings source) => new()
    {
        Command = source.Command,
        VerboseLoggingEnabled = source.VerboseLoggingEnabled,
        McpEnabled = source.McpEnabled,
        UseApiKey = source.UseApiKey,
        ApiKey = source.ApiKey,
        AllowNetworkAccess = source.AllowNetworkAccess,
        ShowMcpResultsInLog = source.ShowMcpResultsInLog,
        ShowMcpResultsOnlyWhenNoEdits = source.ShowMcpResultsOnlyWhenNoEdits,
        SelectedProfile = source.SelectedProfile,
        UseWsl = source.UseWsl
    };

    public class SessionTab : INotifyPropertyChanged
    {
        public SessionTab(string title, SessionView view)
        {
            Title = title;
            View = view;
            _header = $"{Title} - {view.SessionStatus}";
        }

        public string Title { get; }
        public SessionView View { get; }

        private string _header;
        public string Header
        {
            get => _header;
            private set
            {
                if (_header == value) return;
                _header = value;
                OnPropertyChanged();
            }
        }

        public void UpdateHeader()
        {
            Header = $"{Title} - {View.SessionStatus}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
