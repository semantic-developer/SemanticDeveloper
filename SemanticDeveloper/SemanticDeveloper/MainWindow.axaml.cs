using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SemanticDeveloper.Models;
using SemanticDeveloper.Services;
using SemanticDeveloper.Views;
using System.Threading.Tasks;

namespace SemanticDeveloper;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly ObservableCollection<SessionTab> _sessions = new();
    private SessionTab? _selectedSession;
    private int _sessionCounter = 0;
    private AppSettings _sharedSettings;
    private bool _profileCheckPerformed;

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

        await OpenCliSettingsAsync(SelectedSession);
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

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(async () => await EnsureProfilesConfiguredAsync());
    }

    private async Task EnsureProfilesConfiguredAsync()
    {
        if (_profileCheckPerformed)
            return;
        _profileCheckPerformed = true;

        try
        {
            var profiles = CodexConfigService.TryGetProfiles();
            if (profiles.Count > 0)
                return;

            var info = new InfoDialog
            {
                Title = "Add a Codex Profile",
                Message = "No Codex CLI profiles were found in ~/.codex/config.toml.\n\nCreate a profile under [profiles.<name>] with the model you want to use, for example:\n\n[profiles.default]\nmodel = \"gpt-5-codex\"\nmodel_provider = \"openai\"\n\nAfter you add a profile, select it in CLI Settings so sessions know which model to run."
            };

            await info.ShowDialog(this);

            if (SelectedSession is not null)
            {
                await OpenCliSettingsAsync(SelectedSession);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to prompt for profile setup: {ex.Message}");
        }
    }

    private async Task OpenCliSettingsAsync(SessionTab session)
    {
        var updated = await session.View.ShowCliSettingsDialogAsync(_sharedSettings);
        if (updated is null)
            return;

        _sharedSettings = CloneAppSettings(updated);
        foreach (var other in _sessions)
        {
            if (other == session)
                continue;
            other.View.ApplySettingsSnapshot(_sharedSettings);
        }
    }

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
