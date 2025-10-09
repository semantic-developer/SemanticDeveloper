using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.TextMate;
using AvaloniaEdit.Rendering;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Controls.Primitives;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using SemanticDeveloper.Models;
using SemanticDeveloper.Services;
using AvaloniaEdit; 
using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SemanticDeveloper.Views;

public partial class SessionView : UserControl, INotifyPropertyChanged
{
    private readonly CodexCliService _cli = new();
    private string? _currentModel;
    // Auto-approval UI removed; approvals require manual handling
    private AppSettings _settings = new();

    private long _nextRequestId;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JToken>> _pendingRequests = new();
    private readonly List<McpServerDefinition> _configuredMcpServers = new();
    private JObject? _pendingConfigOverrides;
    private bool _appServerInitialized;
    private string? _conversationId;
    private string? _conversationSubscriptionId;

    private string? _currentWorkspacePath;
    private string _cliLog = string.Empty;
    private TextEditor? _logEditor;
    private bool _logAutoScroll = true;
    private bool _isCliRunning;
    public ObservableCollection<FileTreeItem> TreeRoots { get; } = new();
    private string? _lastRenderedAgentMessage;
    private bool _assistantStreaming;
    private string? _pendingUserInput;
    private string _tokenStats = string.Empty;
    private string? _selectedFilePath;
    private string _selectedFileName = string.Empty;
    private bool _isCompareMode;
    private System.Collections.Generic.Dictionary<string, string>? _gitStatusMap;
    private bool _isGitRepo;
    private string _currentBranch = string.Empty;
    private bool _canInitGit;
    // MCP mux removed — we load MCP server definitions directly into Codex.

    // Editor documents
    public TextDocument CurrentFileDocument { get; } = new TextDocument();
    public TextDocument BaseFileDocument { get; } = new TextDocument();
    private DiffGutterRenderer? _baseDiffRenderer;
    private DiffGutterRenderer? _currentDiffRenderer;
    private DiffGutterRenderer? _singleDiffRenderer;
    private TextEditor? _editorBase;
    private TextEditor? _editorCurrent;
    private TextEditor? _editorCurrent2;
    private Image? _imageBase;
    private Image? _imageCurrent;
    private Image? _imageCurrent2;
    private RegistryOptions? _editorRegistryOptions;
    private bool _suppressScrollSync;
    private bool _scrollHandlersAttached;
    private const double EditorButtonsMinWidth = 420.0;
    private bool _verboseLogging;
    private bool _mcpEnabled;
    private bool _allowNetworkAccess;
    private bool _loginInProgress;
    private bool _loginAttempted;
    private bool _showMcpResultsInLog;
    private bool _showMcpResultsOnlyWhenNoEdits;
    private string _selectedProfile = string.Empty;

    // Per-turn flags
    private bool _turnInProgress;
    private bool _turnSawExec;
    private bool _turnSawPatch;

    // Track exec command outputs to avoid flooding log
    private readonly System.Collections.Generic.Dictionary<string, int> _execOutputBytes = new();
    private readonly System.Collections.Generic.HashSet<string> _execTruncated = new();
    private readonly System.Collections.Generic.HashSet<string> _execSuppressed = new();
    private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> _execCommandById = new();
    private const int ExecOutputSoftLimit = 2000; // cap per command

    private enum ScrollMaster { None, Base, Current }
    private ScrollMaster _scrollMaster = ScrollMaster.None;

    public SessionView()
    {
        InitializeComponent();

        DataContext = this;

        McpServers.CollectionChanged += (_, __) =>
        {
            OnPropertyChanged(nameof(HasMcpServers));
            OnPropertyChanged(nameof(HasNoMcpServers));
        };

        // Capture Enter/Shift+Enter on the CLI input box before default handling
        try
        {
            var cliTb = this.FindControl<TextBox>("CliInputTextBox");
            if (cliTb != null)
            {
                cliTb.AddHandler(InputElement.KeyDownEvent, OnCliInputPreviewKeyDown, RoutingStrategies.Tunnel);
            }
        }
        catch { }

        // Install TextMate JSON highlighting for the log editor (best-effort)
        try
        {
            var ctrl = this.FindControl<Control>("LogEditor");
            if (ctrl is AvaloniaEdit.TextEditor editor)
            {
                _logEditor = editor;
                try
                {
                    var opts = editor.Options;
                    opts.AllowScrollBelowDocument = false;
                }
                catch { }
                var registryOptions = new RegistryOptions(ThemeName.DarkPlus);
                var installation = editor.InstallTextMate(registryOptions);
                installation.SetGrammar("source.json");
                editor.TextArea.TextView.LineTransformers.Add(new LogPrefixColorizer());
                try
                {
                    editor.TextArea.TextView.ScrollOffsetChanged += OnLogScrollOffsetChanged;
                }
                catch { }
            }
        }
        catch { }

        // Load settings and apply
        var initialSettings = SettingsService.Load();
        ApplyAppSettingsInternal(initialSettings, persist: false, logPrefix: "System: Loaded CLI settings: ", logSuffix: string.Empty);

        // Load MCP servers list from config for selection before session start
        try { LoadMcpServersFromConfig(); } catch { }

        _cli.OutputReceived += OnCliOutput;
        _cli.Exited += (_, _) =>
        {
            IsCliRunning = false;
            SessionStatus = "stopped";
            _appServerInitialized = false;
            _conversationId = null;
            _conversationSubscriptionId = null;
            _currentModel = null;
            foreach (var pending in _pendingRequests.Values)
            {
                pending.TrySetException(new InvalidOperationException("Codex CLI exited."));
            }
            _pendingRequests.Clear();
        };

        // Lazy-load file tree nodes when expanded
        AddHandler(TreeViewItem.ExpandedEvent, OnTreeItemExpanded, RoutingStrategies.Bubble);

        // Ensure editor panel visibility default
        try { RefreshEditorPanelsVisibility(); } catch { }

        // Capture editor controls and attach gutters/highlighting support
        TrySetupEditors();
    }

    private Window? GetHostWindow() => TopLevel.GetTopLevel(this) as Window;

    private static AppSettings CloneSettings(AppSettings settings) => new()
    {
        Command = settings.Command,
        VerboseLoggingEnabled = settings.VerboseLoggingEnabled,
        McpEnabled = settings.McpEnabled,
        UseApiKey = settings.UseApiKey,
        ApiKey = settings.ApiKey,
        AllowNetworkAccess = settings.AllowNetworkAccess,
        ShowMcpResultsInLog = settings.ShowMcpResultsInLog,
        ShowMcpResultsOnlyWhenNoEdits = settings.ShowMcpResultsOnlyWhenNoEdits,
        SelectedProfile = settings.SelectedProfile,
        UseWsl = settings.UseWsl
    };

    private void ApplyAppSettingsInternal(AppSettings settings, bool persist, string? logPrefix, string? logSuffix)
    {
        _settings = CloneSettings(settings);

        _cli.Command = _settings.Command;
        _cli.UseApiKey = _settings.UseApiKey;
        _cli.ApiKey = string.IsNullOrWhiteSpace(_settings.ApiKey) ? null : _settings.ApiKey;
        _cli.UseWsl = OperatingSystem.IsWindows() && _settings.UseWsl;

        SelectedProfile = _settings.SelectedProfile;
        _currentModel = TryExtractModelFromArgs(_cli.AdditionalArgs);
        _verboseLogging = _settings.VerboseLoggingEnabled;
        IsMcpEnabled = _settings.McpEnabled;
        _allowNetworkAccess = _settings.AllowNetworkAccess;
        _showMcpResultsInLog = _settings.ShowMcpResultsInLog;
        _showMcpResultsOnlyWhenNoEdits = _settings.ShowMcpResultsOnlyWhenNoEdits;

        if (!string.IsNullOrWhiteSpace(logPrefix))
        {
            var profileSuffix = string.IsNullOrWhiteSpace(_settings.SelectedProfile) ? string.Empty : " • profile=" + _settings.SelectedProfile;
            var wslSuffix = _cli.UseWsl ? " • WSL" : string.Empty;
            AppendCliLog($"{logPrefix}'{_cli.Command}'{profileSuffix}{wslSuffix}{(logSuffix ?? string.Empty)}");
        }

        if (persist)
        {
            SettingsService.Save(_settings);
        }
    }

    public void ApplySettingsSnapshot(AppSettings settings, bool logChange = false)
    {
        var prefix = logChange ? "System: CLI settings updated: " : null;
        var suffix = logChange ? " (--proto enabled)" : null;
        ApplyAppSettingsInternal(settings, persist: false, logPrefix: prefix, logSuffix: suffix);
    }

    public AppSettings GetCurrentSettings() => CloneSettings(_settings);

    private void LoadMcpServersFromConfig()
    {
        var path = Services.McpConfigService.GetConfigPath();
        try
        {
            Services.McpConfigService.EnsureConfigExists();
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path);
            var json = JObject.Parse(text);
            var existing = McpServers.ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddOrUpdate(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                name = new string(name.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '-').ToArray());
                seen.Add(name);
                if (!existing.ContainsKey(name))
                    McpServers.Add(new McpServerEntry { Name = name, Selected = true });
            }

            if (json["mcpServers"] is JObject map)
            {
                foreach (var p in map.Properties()) AddOrUpdate(p.Name);
            }
            else if (json["servers"] is JArray arr)
            {
                foreach (var s in arr.OfType<JObject>()) AddOrUpdate(s["name"]?.ToString() ?? string.Empty);
            }

            // Remove entries no longer present
            for (int i = McpServers.Count - 1; i >= 0; i--)
            {
                if (!seen.Contains(McpServers[i].Name)) McpServers.RemoveAt(i);
            }
        }
        catch { }

        OnPropertyChanged(nameof(HasMcpServers));
        OnPropertyChanged(nameof(HasNoMcpServers));
    }

    private async void OnCliInputPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var isEnter = e.Key == Key.Enter || e.Key == Key.Return;
        if (!isEnter) return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Insert a newline manually and consume the event
            e.Handled = true;
            try
            {
                var t = tb.Text ?? string.Empty;
                var caret = tb.CaretIndex;
                var nl = "\n";
                tb.Text = t.Insert(caret, nl);
                tb.CaretIndex = caret + nl.Length;
            }
            catch { }
            return;
        }

        // Plain Enter sends
        e.Handled = true;
        CliInput = tb.Text ?? string.Empty;
        await SendCliInputAsync();
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string? CurrentWorkspacePath
    {
        get => _currentWorkspacePath;
        set
        {
            if (_currentWorkspacePath == value) return;
            _currentWorkspacePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasWorkspace));
            // Reset shell working directory to new workspace by default
            _shellCwd = _currentWorkspacePath;
            _shellPrevCwd = null;
            OnPropertyChanged(nameof(ShellPromptPath));
            // If no workspace is selected, reflect disconnected status
            if (string.IsNullOrWhiteSpace(_currentWorkspacePath) || !Directory.Exists(_currentWorkspacePath))
            {
                SessionStatus = "disconnected";
            }
        }
    }

    public bool HasWorkspace => !string.IsNullOrWhiteSpace(CurrentWorkspacePath) && Directory.Exists(CurrentWorkspacePath);

    public string ShellPromptPath
    {
        get
        {
            var path = _shellCwd ?? CurrentWorkspacePath ?? string.Empty;
            const int MaxLen = 30;
            if (string.IsNullOrEmpty(path) || path.Length <= MaxLen) return path;
            // Keep the tail end, prefix with ellipsis
            var tailLen = Math.Max(1, MaxLen - 1);
            return "…" + path.Substring(path.Length - tailLen, tailLen);
        }
    }

    // MCP settings flag (controls whether to pass servers to Codex)
    public bool IsMcpEnabled
    {
        get => _mcpEnabled;
        set
        {
            if (_mcpEnabled == value) return;
            _mcpEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMcpDisabled));
        }
    }

    public bool IsMcpDisabled => !_mcpEnabled;

    public bool HasMcpServers => McpServers.Count > 0;
    public bool HasNoMcpServers => McpServers.Count == 0;

    // Exposed tool names for the MCP panel
    public ObservableCollection<string> McpToolNames { get; } = new();
    public ObservableCollection<McpServerEntry> McpServers { get; } = new();

    public string CliLog
    {
        get => _cliLog;
        set
        {
            if (_cliLog == value) return;
            _cliLog = value;
            OnPropertyChanged();
        }
    }

    public string TokenStats
    {
        get => _tokenStats;
        set
        {
            if (_tokenStats == value) return;
            _tokenStats = value;
            OnPropertyChanged();
        }
    }

    public string SelectedFileName
    {
        get => _selectedFileName;
        set
        {
            if (_selectedFileName == value) return;
            _selectedFileName = value;
            OnPropertyChanged();
        }
    }

    public bool IsGitRepo
    {
        get => _isGitRepo;
        set
        {
            if (_isGitRepo == value) return;
            _isGitRepo = value;
            OnPropertyChanged();
        }
    }

    public string CurrentBranch
    {
        get => _currentBranch;
        set
        {
            if (_currentBranch == value) return;
            _currentBranch = value;
            OnPropertyChanged();
        }
    }

    public bool CanInitGit
    {
        get => _canInitGit;
        set
        {
            if (_canInitGit == value) return;
            _canInitGit = value;
            OnPropertyChanged();
        }
    }

    

    public bool IsCompareMode
    {
        get => _isCompareMode;
        set
        {
            if (_isCompareMode == value) return;
            _isCompareMode = value;
            OnPropertyChanged();
            RefreshEditorPanelsVisibility();
            _ = RefreshBaseDocumentAsync();
            TrySetupEditors();
            _scrollMaster = ScrollMaster.None;
        }
    }

    // AvaloniaEdit document for rich editing/better rendering
    public TextDocument CliLogDocument { get; } = new TextDocument();

    private string _cliInput = string.Empty;
    public string CliInput
    {
        get => _cliInput;
        set
        {
            if (_cliInput == value) return;
            _cliInput = value;
            OnPropertyChanged();
        }
    }

    private string _sessionStatus = "disconnected";
    public string SessionStatus
    {
        get => _sessionStatus;
        set
        {
            if (_sessionStatus == value) return;
            _sessionStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    public bool IsBusy => _sessionStatus is "thinking…" or "responding…" or "applying patch…" or "starting…" || _assistantStreaming;

    public bool IsCliRunning
    {
        get => _isCliRunning;
        set
        {
            if (_isCliRunning == value) return;
            _isCliRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    public string SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (_selectedProfile == value) return;
            _selectedProfile = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedProfile));
        }
    }

    public bool HasSelectedProfile => !string.IsNullOrWhiteSpace(SelectedProfile);

    private void AppendCliLog(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var cleaned = TextFilter.StripAnsi(text);
        if (Dispatcher.UIThread.CheckAccess())
        {
            CliLog += cleaned + Environment.NewLine;
            CliLogDocument.Text += cleaned + Environment.NewLine;
            AutoScrollLogIfNeeded();
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                CliLog += cleaned + Environment.NewLine;
                CliLogDocument.Text += cleaned + Environment.NewLine;
                AutoScrollLogIfNeeded();
            });
        }
    }

    private void AppendCliInline(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var cleaned = TextFilter.StripAnsi(text);
        if (Dispatcher.UIThread.CheckAccess())
        {
            CliLog += cleaned;
            CliLogDocument.Text += cleaned;
            AutoScrollLogIfNeeded();
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                CliLog += cleaned;
                CliLogDocument.Text += cleaned;
                AutoScrollLogIfNeeded();
            });
        }
    }

    private void AppendCliLogVerbose(string text)
    {
        if (!_verboseLogging) return;
        AppendCliLog(text);
    }

    private void AutoScrollLogIfNeeded()
    {
        if (!_logAutoScroll) return;
        if (_logEditor is null) return;
        try
        {
            var docLength = _logEditor.Document?.TextLength ?? 0;
            _logEditor.TextArea.Caret.Offset = docLength;
            _logEditor.TextArea.Caret.BringCaretToView();
        }
        catch { }
    }

    private void OnLogScrollOffsetChanged(object? sender, EventArgs e)
    {
        if (_logEditor is null)
        {
            _logAutoScroll = true;
            return;
        }

        try
        {
            var textView = _logEditor.TextArea.TextView;
            var documentHeight = textView.DocumentHeight;
            var viewportHeight = textView.Bounds.Height;

            if (viewportHeight <= 0 || documentHeight <= viewportHeight)
            {
                _logAutoScroll = true;
                return;
            }

            var bottom = textView.VerticalOffset + viewportHeight;
            var threshold = Math.Max(24, viewportHeight / 3);
            _logAutoScroll = bottom >= documentHeight - threshold;
        }
        catch
        {
            _logAutoScroll = true;
        }
    }

    private void OnCliOutput(object? sender, string line)
    {
        if (string.IsNullOrEmpty(line))
            return;

        if (TryHandleAppServerMessage(line))
            return;

        AppendCliLog(line);
    }

    private bool TryHandleAppServerMessage(string line)
    {
        JObject root;
        try { root = JObject.Parse(line); }
        catch { return false; }

        if (root.TryGetValue("method", out var methodToken))
        {
            var method = methodToken?.ToString() ?? string.Empty;
            if (root.ContainsKey("id"))
            {
                _ = HandleServerRequestAsync(root);
            }
            else
            {
                HandleServerNotification(method, root["params"] as JObject);
            }
            return true;
        }

        if (root.ContainsKey("id"))
        {
            HandleServerResponse(root);
            return true;
        }

        if (root["error"] is JObject errorObj)
        {
            var message = errorObj["message"]?.ToString() ?? "Unknown Codex error";
            AppendCliLog("System: ERROR: " + message);
            SetStatusSafe("error");
            return true;
        }

        return false;
    }

    private bool TryRenderProtocolEvent(JObject root)
    {
        JObject? msg = root["msg"] as JObject;
        if (msg is null)
        {
            if (root.TryGetValue("type", out var typeCandidate) && typeCandidate?.Type == JTokenType.String)
            {
                msg = root;
            }
            else
            {
                return false;
            }
        }

        var type = msg["type"]?.ToString();
        if (string.IsNullOrWhiteSpace(type)) return false;

        var lower = type.ToLowerInvariant();
        switch (lower)
        {
            case "agent_message_delta":
                {
                    var delta = msg? ["delta"]?.ToString();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        SetStatusSafe("responding…");
                        if (!_assistantStreaming)
                        {
                            _assistantStreaming = true;
                            if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
                            AppendCliLog("Assistant:");
                        }
                        AppendCliInline(delta);
                    }
                    return true;
                }
            case "agent_message":
                {
                    var message = msg? ["message"]?.ToString();
                    if (!string.IsNullOrEmpty(message))
                    {
                        if (_assistantStreaming)
                        {
                            // Already streamed; end with newline and reset
                            if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
                            _assistantStreaming = false;
                            return true;
                        }
                        if (_lastRenderedAgentMessage == message) return true;
                        _lastRenderedAgentMessage = message;
                        if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
                        AppendCliLog("Assistant:");
                        AppendCliLog(message);
                        AppendCliInline(Environment.NewLine);
                    }
                    return true;
                }
            case "user_message":
                {
                    var um = msg? ["message"]?.ToString();
                    if (!string.IsNullOrEmpty(um))
                    {
                        // If we already logged this locally, skip echo to avoid duplicates
                        if (_pendingUserInput != null && string.Equals(_pendingUserInput, um, StringComparison.Ordinal))
                        {
                            _pendingUserInput = null;
                            return true;
                        }
                        if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
                        AppendCliLog("You:");
                        AppendCliLog(um);
                    }
                    return true;
                }
            case "agent_reasoning_delta":
                SetStatusSafe("thinking…");
                return true;
            case "agent_reasoning":
                SetStatusSafe("thinking…");
                return true;
            case "agent_reasoning_section_break":
            case "agent_reasoning_raw_content_delta":
            case "agent_reasoning_raw_content":
                // Suppress verbose reasoning stream in the main log by default
                return true;
            case "mcp_tool_call_begin":
                {
                    try
                    {
                        var inv = msg? ["invocation"] as JObject;
                        var server = inv? ["server"]?.ToString();
                        var tool = inv? ["tool"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(server) && !string.IsNullOrWhiteSpace(tool))
                            AppendCliLog($"System: MCP tool begin: {server}.{tool}");
                    }
                    catch { }
                    return true;
                }
            case "mcp_tool_call_end":
                {
                    try
                    {
                        var inv = msg? ["invocation"] as JObject;
                        var server = inv? ["server"]?.ToString();
                        var tool = inv? ["tool"]?.ToString();
                        // Handle success/error across variants (is_error/isError, Err, error, success=false)
                        var res = msg? ["result"] as JObject;
                        bool isError = false;
                        string? errorMsg = null;
                        int contentCount = -1;
                        bool hasStructured = false;
                        int structuredResults = -1;
                        if (res != null)
                        {
                            var isErrToken = res["is_error"] ?? res["isError"]; // tolerate both
                            if (isErrToken != null && bool.TryParse(isErrToken.ToString(), out var b))
                                isError = b;
                            // Rust-style Result { Err: "..." } or { Ok: {...} }
                            if (!isError && res["Err"] != null)
                            {
                                isError = true;
                                var e = res["Err"];
                                errorMsg = e?.Type == JTokenType.String ? e?.ToString() : e?.ToString(Newtonsoft.Json.Formatting.None);
                            }
                            // Common error keys
                            if (!isError && res["error"] != null)
                            {
                                isError = true;
                                var e = res["error"];
                                errorMsg = e?.Type == JTokenType.String ? e?.ToString() : e?.ToString(Newtonsoft.Json.Formatting.None);
                            }
                            // success flag
                            if (!isError && res["success"] != null && bool.TryParse(res["success"]?.ToString(), out var succ))
                                isError = !succ;
                            try
                            {
                                if (res["content"] is JArray arr)
                                    contentCount = arr.Count;
                            }
                            catch { }
                            try
                            {
                                var scNode = res["structured_content"] ?? res["structuredContent"];
                                if (scNode is Newtonsoft.Json.Linq.JToken sc)
                                {
                                    hasStructured = true;
                                    var resultsNode = sc["results"];
                                    if (resultsNode is JArray arr2)
                                        structuredResults = arr2.Count;
                                }
                            }
                            catch { }
                        }
                        var extra = string.Empty;
                        if (!isError)
                        {
                            if (contentCount >= 0) extra = $" • items: {contentCount}";
                            if (hasStructured)
                            {
                                if (structuredResults >= 0) extra += $" • structured.results: {structuredResults}";
                                else extra += " • structured";
                            }
                        }
                        if (isError && !string.IsNullOrWhiteSpace(errorMsg))
                            AppendCliLog($"System: MCP tool end: {server}.{tool} • ERR — {errorMsg}");
                        else
                            AppendCliLog($"System: MCP tool end: {server}.{tool} • {(isError ? "ERR" : "OK")}{extra}");
                        // Surface search-like results in Q&A flows
                        try
                        {
                            if (!isError && _showMcpResultsInLog && (!_showMcpResultsOnlyWhenNoEdits || (!_turnSawExec && !_turnSawPatch)))
                            {
                                // Prefer structured_content.results list with title/url
                                var resObj = msg? ["result"] as JObject;
                                var sc = (resObj? ["structured_content"]) ?? (resObj? ["structuredContent"]);
                                var results = sc? ["results"] as JArray;
                                if (results != null && results.Count > 0)
                                {
                                    // Local helpers to normalize field names across servers
                                    string? GetTitle(JObject it)
                                    {
                                        return it["title"]?.ToString()
                                               ?? it["Title"]?.ToString()
                                               ?? it["item1"]?.ToString()
                                               ?? it["Item1"]?.ToString();
                                    }
                                    string? GetUrl(JObject it)
                                    {
                                        return it["url"]?.ToString()
                                               ?? it["Url"]?.ToString()
                                               ?? it["link"]?.ToString()
                                               ?? it["href"]?.ToString()
                                               ?? it["item2"]?.ToString()
                                               ?? it["Item2"]?.ToString();
                                    }
                                    int max = Math.Min(8, results.Count);
                                    if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
                                    AppendCliLog($"Results ({server}.{tool}):");
                                    for (int i = 0; i < max; i++)
                                    {
                                        if (results[i] is JObject item)
                                        {
                                            var title = GetTitle(item);
                                            var url = GetUrl(item);
                                            if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(url))
                                            {
                                                AppendCliLog($"- {(string.IsNullOrWhiteSpace(title) ? url : title)}{(string.IsNullOrWhiteSpace(url) ? string.Empty : " — " + url)}");
                                            }
                                        }
                                    }
                                    if (results.Count > max)
                                        AppendCliLog($"… and {results.Count - max} more");
                                }
                                else
                                {
                                    // Fallback: show first text content block if present
                                    var contentTok = resObj? ["content"];
                                    string? text = null;
                                    if (contentTok is JArray contentArr)
                                    {
                                        var textBlock = contentArr.FirstOrDefault(t => (t?["type"]?.ToString() ?? string.Empty).Equals("text", StringComparison.OrdinalIgnoreCase)) as JObject;
                                        text = textBlock? ["text"]?.ToString();
                                    }
                                    else if (contentTok is JValue v && v.Type == JTokenType.String)
                                    {
                                        text = v.ToString();
                                    }
                                    if (!string.IsNullOrWhiteSpace(text))
                                    {
                                        var lines = text!.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Take(10).ToList();
                                        if (lines.Count > 0)
                                        {
                                            if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
                                            AppendCliLog($"Results ({server}.{tool}):");
                                            foreach (var ln in lines)
                                            {
                                                var trimmed = ln.Trim();
                                                if (string.IsNullOrEmpty(trimmed)) continue;
                                                AppendCliLog(trimmed);
                                            }
                                            if (text!.Length > 1500) AppendCliLog("… (truncated)");
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                        // Optional: verbose raw result logging for troubleshooting
                        if (_verboseLogging && res != null)
                        {
                            try
                            {
                                var raw = res.ToString(Newtonsoft.Json.Formatting.None);
                                if (raw.Length > 1200) raw = raw.Substring(0, 1200) + "…";
                                AppendCliLog("System: MCP result (raw, trimmed): " + raw);
                            }
                            catch { }
                        }
                    }
                    catch { }
                    return true;
                }
            case "mcp_list_tools_response":
                {
                    try
                    {
                        var tools = msg? ["tools"] as JObject;
                        var toolArray = msg? ["tools"] as JArray;
                        var names = new List<string>();
                        if (tools != null)
                        {
                            names = tools.Properties().Select(p => p.Name).ToList();
                        }
                        else if (toolArray != null)
                        {
                            foreach (var t in toolArray)
                            {
                                var name = t?["name"]?.ToString();
                                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                            }
                        }
                        if (names.Count > 0)
                        {
                            names.Sort(StringComparer.OrdinalIgnoreCase);
                            Dispatcher.UIThread.Post(() =>
                            {
                                try
                                {
                                    McpToolNames.Clear();
                                    foreach (var n in names) McpToolNames.Add(n);

                                    // Populate tools under servers
                                    var byName = McpServers.ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);
                                    foreach (var entry in McpServers) entry.Tools.Clear();
                                    foreach (var toolName in names)
                                    {
                                        var parts = SplitToolIdentifier(toolName);
                                        var server = parts.Server;
                                        var shortName = parts.Short;
                                        if (string.IsNullOrWhiteSpace(server)) continue;
                                        if (!byName.TryGetValue(server, out var entry)) continue; // only show tools for known servers
                                        if (!entry.Tools.Any(t => string.Equals(t.Full, toolName, StringComparison.Ordinal)))
                                            entry.Tools.Add(new ToolItem { Short = shortName, Full = toolName });
                                    }
                                }
                                catch { }
                            });
                            AppendCliLog($"System: MCP tools available: {names.Count} ({string.Join(", ", names.Take(10))}{(names.Count>10?", …":"")})");
                        }
                    }
                    catch { }
                    return true;
                }
            case "stream_error":
                {
                    var msgText = msg? ["message"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(msgText))
                    {
                        if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
                        AppendCliLog("System: ERROR: " + msgText);
                        // Detect unauthorized and offer CLI login when not using API key
                        if (msgText.IndexOf("401 Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            _ = HandleUnauthorizedAsync();
                        }
                    }
                    SetStatusSafe("error");
                    return true;
                }
            case "exec_command_output_delta":
                {
                    try
                    {
                        var chunkB64 = msg? ["chunk"]?.ToString();
                        var callId = msg? ["call_id"]?.ToString();
                        if (!string.IsNullOrEmpty(chunkB64))
                        {
                            var bytes = Convert.FromBase64String(chunkB64);
                            var text = System.Text.Encoding.UTF8.GetString(bytes);
                            if (!string.IsNullOrEmpty(text))
                            {
                                if (!string.IsNullOrWhiteSpace(callId) && _execSuppressed.Contains(callId!))
                                {
                                    // suppress noisy file dumps
                                }
                                else if (!string.IsNullOrWhiteSpace(callId))
                                {
                                    var used = _execOutputBytes.TryGetValue(callId!, out var v) ? v : 0;
                                    if (used >= ExecOutputSoftLimit)
                                    {
                                        _execTruncated.Add(callId!);
                                    }
                                    else
                                    {
                                        var remaining = ExecOutputSoftLimit - used;
                                        var toWrite = Math.Min(remaining, text.Length);
                                        if (toWrite > 0)
                                        {
                                            AppendCliInline(text.Substring(0, toWrite));
                                            _execOutputBytes[callId!] = used + toWrite;
                                        }
                                        if (toWrite < text.Length)
                                        {
                                            _execTruncated.Add(callId!);
                                        }
                                    }
                                }
                                else
                                {
                                    // Unknown call; write with small cap
                                    var toWrite = Math.Min(ExecOutputSoftLimit, text.Length);
                                    AppendCliInline(text.Substring(0, toWrite));
                                    if (toWrite < text.Length && !CliLog.EndsWith("\n")) AppendCliInline("\n… (output truncated)\n");
                                }
                            }
                        }
                    }
                    catch { }
                    return true;
                }
            case "exec_command_begin":
                {
                    var cmdArr = msg? ["command"] as JArray;
                    var callId = msg? ["call_id"]?.ToString();
                    var tokens = cmdArr is null ? new System.Collections.Generic.List<string>() : cmdArr.Select(t => t?.ToString() ?? string.Empty).ToList();
                    _turnSawExec = true;
                    if (!string.IsNullOrWhiteSpace(callId))
                    {
                        _execOutputBytes[callId!] = 0;
                        _execTruncated.Remove(callId!);
                        _execSuppressed.Remove(callId!);
                        _execCommandById[callId!] = tokens;
                        var joined = string.Join(" ", tokens).ToLowerInvariant();
                        if (joined.Contains(" sed ") || joined.Contains(" sed -n") || joined.Contains(" ripgrep") || joined.Contains(" rg ") || joined.Contains(" cat ") || joined.Contains(" type "))
                        {
                            _execSuppressed.Add(callId!);
                        }
                    }
                    var cmd = tokens.Count == 0 ? string.Empty : string.Join(" ", tokens.Take(5));
                    if (!string.IsNullOrEmpty(cmd))
                    {
                        if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
                        var hasPatch = cmdArr != null && cmdArr.Any(x => (x?.ToString() ?? string.Empty).StartsWith("*** Begin Patch"));
                        AppendCliLog($"System: Running: {cmd}{(hasPatch ? " (patch)" : string.Empty)}");
                    }
                    return true;
                }
            case "exec_command_end":
                {
                    var code = msg? ["exit_code"]?.ToString();
                    var callId = msg? ["call_id"]?.ToString();
                    if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
                    if (!string.IsNullOrWhiteSpace(callId) && _execTruncated.Contains(callId!))
                        AppendCliLog($"System: (exit {code}) — output truncated");
                    else
                        AppendCliLog($"System: (exit {code})");
                    return true;
                }
            case "patch_apply_begin":
                {
                    AppendCliLog("System: Applying patch…");
                    SetStatusSafe("thinking…");
                    _turnSawPatch = true;
                    return true;
                }
            case "patch_apply_end":
                {
                    if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
                    AppendCliLog("System: Patch applied");
                    SetStatusSafe("thinking…");
                    // Refresh file tree to reflect new files
                    if (HasWorkspace && CurrentWorkspacePath is not null)
                    {
                        try { LoadTree(CurrentWorkspacePath); } catch { }
                    }
                    try { UpdateSingleDiffGutter(); } catch { }
                    return true;
                }
            case "turn_diff":
                return true;
            case "token_count":
                try { UpdateTokenStats(msg); } catch { }
                return true;
            case "turn_aborted":
                if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
                AppendCliLog("System: Turn aborted");
                SetStatusSafe("idle");
                _assistantStreaming = false;
                _turnInProgress = false;
                return true;
            case "exec_approval_request":
            case "apply_patch_approval_request":
                return true;
            case "task_started":
                if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
                AppendCliLog("System: Task started");
                SetStatusSafe("thinking…");
                _turnInProgress = true;
                _turnSawExec = false;
                _turnSawPatch = false;
                return true;
            case "task_complete":
                if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
                AppendCliLog("System: Task complete");
                SetStatusSafe("idle");
                _assistantStreaming = false;
                _turnInProgress = false;
                return true;
            case "session_configured":
                // handled elsewhere for model detection; suppress raw JSON
                return true;
            case "error":
                {
                    var msgText = msg? ["message"]?.ToString();
                    if (!string.IsNullOrEmpty(msgText))
                    {
                        if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
                        AppendCliLog("System: ERROR: " + msgText);
                        if (msgText.IndexOf("401 Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            _ = HandleUnauthorizedAsync();
                        }
                        SetStatusSafe("error");
                    }
                    return true;
                }
            default:
                return false;
        }
    }

    // Auto-approval helper removed

    private void SetStatusSafe(string status)
    {
        if (Dispatcher.UIThread.CheckAccess())
            SessionStatus = status;
        else
            Dispatcher.UIThread.Post(() => SessionStatus = status);
    }

    private void UpdateTokenStats(JObject msg)
    {
        var info = msg["info"] as JObject;
        if (info is null) return;
        var total = info["total_token_usage"] as JObject;
        if (total is null) return;

        long input = total.Value<long?>("input_tokens") ?? 0L;
        long cached = total.Value<long?>("cached_input_tokens") ?? 0L;
        long output = total.Value<long?>("output_tokens") ?? 0L;
        long blendedTotal = Math.Max(0, (input - cached)) + output;

        var modelCw = (info["model_context_window"]?.Type == JTokenType.Integer)
            ? info.Value<long?>("model_context_window")
            : null;

        int? percentLeft = null;
        if (modelCw.HasValue && modelCw.Value > 0)
        {
            const long DefaultBaseline = 12000;
            long window = modelCw.Value;
            long baseline = Math.Min(DefaultBaseline, Math.Max(0L, window / 4));
            long effectiveWindow = window - baseline;
            if (effectiveWindow <= 0)
            {
                baseline = 0;
                effectiveWindow = window;
            }

            if (effectiveWindow > 0)
            {
                long used = Math.Max(0, blendedTotal - baseline);
                long remaining = Math.Max(0, effectiveWindow - used);
                percentLeft = (int)Math.Clamp(remaining * 100.0 / Math.Max(1, effectiveWindow), 0, 100);
            }
        }

        string num(long v) => v.ToString("N0", CultureInfo.InvariantCulture);
        TokenStats = percentLeft.HasValue
            ? $"tokens {num(blendedTotal)} • {percentLeft.Value}% left"
            : $"tokens {num(blendedTotal)}";
    }

    private async Task SelectWorkspaceAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var provider = topLevel?.StorageProvider;
        if (provider is null)
            return;
        var result = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Select workspace folder"
        });
        var folder = result?.FirstOrDefault();
        if (folder is null) return;
        var selected = folder.Path?.LocalPath;
        if (string.IsNullOrWhiteSpace(selected) || !Directory.Exists(selected))
            return;

        CurrentWorkspacePath = selected;
        LoadTree(selected);
        // do not persist workspace; every session should be fresh

        _ = CheckCodexVersionAsync();

        // Git check
        if (!GitHelper.IsGitRepository(selected))
        {
            bool gitAvailable = await GitHelper.IsGitAvailableAsync();
            if (!gitAvailable)
            {
                AppendCliLog("Git library unavailable. Skipping repository initialization.");
            }
            else
            {
                var confirm = new ConfirmDialog
                {
                    Title = "Initialize Git",
                    Message = $"The selected folder is not a git repository.\nInitialize git here?"
                };
                var window = GetHostWindow();
                var confirmed = window is not null && await confirm.ShowDialog<bool>(window);
                if (confirmed)
                {
                    AppendCliLog($"Initializing git in {selected}…");
                    var initResult = await GitHelper.InitializeRepositoryAsync(selected);
                    if (initResult.Success)
                    {
                        AppendCliLog(initResult.Output.Trim());
                    }
                    else
                    {
                        AppendCliLog("Git init failed: " + initResult.Error.Trim());
                    }
                }
            }
        }

        RefreshGitUi();
        await RestartCliAsync();
    }

    private async Task CheckCodexVersionAsync()
    {
        try
        {
            var latest = CodexVersionService.TryReadLatestVersion();
            if (!latest.Ok || string.IsNullOrWhiteSpace(latest.Version)) return;

            (bool Ok, string? Version, string? Error) installed;
            if (_cli.UseWsl && OperatingSystem.IsWindows())
            {
                var psi = await _cli.BuildProcessStartInfoAsync(CurrentWorkspacePath ?? Directory.GetCurrentDirectory(), new[] { "--version" }, redirectStdIn: false);
                installed = await CodexVersionService.TryGetInstalledVersionAsync(psi);
            }
            else
            {
                installed = await CodexVersionService.TryGetInstalledVersionAsync(_cli.Command, CurrentWorkspacePath ?? Directory.GetCurrentDirectory());
            }
            if (!installed.Ok || string.IsNullOrWhiteSpace(installed.Version)) return;

            if (CodexVersionService.IsNewer(latest.Version, installed.Version))
            {
                AppendCliLog($"System: Codex {latest.Version} is available (installed {installed.Version}). Update with 'npm install -g @openai/codex@latest' or 'brew upgrade codex'.");
            }
        }
        catch
        {
            // best-effort; ignore failures
        }
    }

    private void LoadTree(string rootPath)
    {
        void DoLoad()
        {
            TreeRoots.Clear();
            try
            {
                // Build git status cache
                try { _gitStatusMap = Services.GitHelper.TryGetWorkingDirectoryStatus(rootPath); } catch { _gitStatusMap = null; }
                var root = FileTreeItem.CreateLazy(rootPath);
                // Show icon only for the root node
                root.Name = string.Empty;
                TreeRoots.Add(root);
            }
            catch (Exception ex)
            {
                AppendCliLog("Failed to load tree: " + ex.Message);
            }
        }

        if (Dispatcher.UIThread.CheckAccess()) DoLoad();
        else Dispatcher.UIThread.Post(DoLoad);
    }

    private void RefreshGitUi()
    {
        try
        {
            string? repoRoot = null;
            if (HasWorkspace && CurrentWorkspacePath is not null)
                repoRoot = GitHelper.DiscoverRepositoryRoot(CurrentWorkspacePath);
            IsGitRepo = !string.IsNullOrEmpty(repoRoot);
            if (IsGitRepo && CurrentWorkspacePath is not null)
            {
                var (ok, branch, _) = GitHelper.TryGetCurrentBranch(CurrentWorkspacePath);
                CurrentBranch = ok && !string.IsNullOrWhiteSpace(branch) ? branch! : string.Empty;
            }
            else
            {
                CurrentBranch = string.Empty;
            }
            CanInitGit = HasWorkspace && !IsGitRepo;
        }
        catch
        {
            IsGitRepo = false;
            CurrentBranch = string.Empty;
            CanInitGit = HasWorkspace;
        }
    }

    private void OnTreeItemExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TreeViewItem tvi && tvi.DataContext is FileTreeItem item)
        {
            item.LoadChildrenIfNeeded(_gitStatusMap);
        }
    }

    private async void OnCreateAgentsFileClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (sender is not MenuItem menu) return;
            var item = menu.CommandParameter as FileTreeItem ?? menu.DataContext as FileTreeItem;
            if (item is null) return;
            if (!item.IsDirectory) return;

            var folder = item.FullPath;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                AppendCliLog("System: Cannot locate folder for AGENTS.md.");
                return;
            }

            var targetPath = Path.Combine(folder, "AGENTS.md");
            var existed = File.Exists(targetPath);
            if (!existed)
            {
                var template = "# AGENTS.md\n\n" +
                    "Use this file to give Codex CLI directory-specific guidance.\n" +
                    "Describe coding style, testing commands, or any constraints that apply to files under this folder.\n\n" +
                    "## Guidelines\n" +
                    "- List the expectations the agent must follow.\n" +
                    "- Mention how to run or scope tests for this area.\n" +
                    "- Note naming conventions, file layout rules, or things to avoid.\n\n" +
                    "## Notes\n" +
                    "These instructions apply to **" + Path.GetFileName(folder) + "** and all subdirectories.\n" +
                    "If you add more specific AGENTS.md files deeper in the tree, those take precedence.";

                await File.WriteAllTextAsync(targetPath, template);

                var rel = !string.IsNullOrWhiteSpace(CurrentWorkspacePath)
                    ? Path.GetRelativePath(CurrentWorkspacePath, targetPath)
                    : targetPath;
                AppendCliLog($"System: Created AGENTS.md at {rel}.");
            }
            else
            {
                var rel = !string.IsNullOrWhiteSpace(CurrentWorkspacePath)
                    ? Path.GetRelativePath(CurrentWorkspacePath, targetPath)
                    : targetPath;
                AppendCliLog($"System: AGENTS.md already exists at {rel}; opening.");
            }

            if (!string.IsNullOrWhiteSpace(CurrentWorkspacePath))
            {
                LoadTree(CurrentWorkspacePath);
            }

            _selectedFilePath = targetPath;
            SelectedFileName = Path.GetFileName(targetPath);
            await LoadSelectedFileAsync(targetPath);
        }
        catch (Exception ex)
        {
            AppendCliLog("System: Failed to create AGENTS.md: " + ex.Message);
        }
    }

    private void OnPromptsButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button button) return;
            var prompts = LoadPromptEntries(out var dir);
            if (prompts.Count == 0)
            {
                AppendCliLog($"System: No prompts found (expected in {dir}).");
                return;
            }

            var flyout = new MenuFlyout();
            foreach (var prompt in prompts)
            {
                var item = new MenuItem
                {
                    Header = prompt.Display,
                    Tag = prompt.Command
                };
                item.Click += OnPromptMenuItemClick;
                flyout.Items.Add(item);
            }
            flyout.ShowAt(button);
        }
        catch (Exception ex)
        {
            AppendCliLog("System: Unable to load prompts: " + ex.Message);
        }
    }

    private async void OnPromptMenuItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem menu) return;
        var command = menu.Tag as string;
        if (string.IsNullOrWhiteSpace(command)) return;
        if (!_cli.IsRunning)
        {
            AppendCliLog("System: CLI session not running; cannot send prompt.");
            return;
        }

        var label = menu.Header?.ToString() ?? command;
        AppendCliLog($"System: Sending prompt '{label}'.");
        CliInput = command;
        await SendCliInputAsync();
    }

    private List<(string Display, string Command)> LoadPromptEntries(out string directory)
    {
        var results = new List<(string, string)>();
        directory = Services.CodexConfigService.GetPromptsDirectory();
        try
        {
            if (!Directory.Exists(directory)) return results;
            var files = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (string.IsNullOrWhiteSpace(name)) continue;
                var display = name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
                var command = "/" + name;
                results.Add((display, command));
            }
        }
        catch { }
        return results;
    }

    private async void OnDeleteTreeItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem menu) return;
        var item = menu.CommandParameter as FileTreeItem ?? menu.DataContext as FileTreeItem;
        if (item is null) return;

        var path = item.FullPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalizedPath = NormalizePath(path);
        var normalizedWorkspace = !string.IsNullOrWhiteSpace(CurrentWorkspacePath)
            ? NormalizePath(CurrentWorkspacePath)
            : null;
        if (normalizedWorkspace != null && string.Equals(normalizedWorkspace, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            AppendCliLog("System: Cannot delete the workspace root from the tree.");
            return;
        }

        var targetName = item.Name;
        if (string.IsNullOrWhiteSpace(targetName)) targetName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(targetName)) targetName = path;

        var confirm = new ConfirmDialog
        {
            Title = "Delete",
            Message = $"Delete '{targetName}'?\nThis action cannot be undone."
        };
        var window = GetHostWindow();
        var result = window is not null && await confirm.ShowDialog<bool>(window);
        if (!result) return;

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                AppendCliLog($"System: Deleted folder '{targetName}'.");
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
                AppendCliLog($"System: Deleted file '{targetName}'.");
            }
            else
            {
                AppendCliLog($"System: '{targetName}' no longer exists.");
            }
        }
        catch (Exception ex)
        {
            AppendCliLog("System: Delete failed: " + ex.Message);
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(_selectedFilePath) && string.Equals(NormalizePath(_selectedFilePath), normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                _selectedFilePath = null;
                SelectedFileName = string.Empty;
                CurrentFileDocument.Text = string.Empty;
            }
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(CurrentWorkspacePath))
        {
            LoadTree(CurrentWorkspacePath);
        }
    }

    private async void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (sender is TreeView tv && tv.SelectedItem is FileTreeItem item && !item.IsDirectory)
            {
                _selectedFileItem = item;
                await LoadSelectedFileAsync(item.FullPath);
            }
        }
        catch (Exception ex)
        {
            AppendCliLog("System: Failed to load file: " + ex.Message);
        }
    }

    private async Task LoadSelectedFileAsync(string path)
    {
        _selectedFilePath = path;
        SelectedFileName = Path.GetFileName(path);
        try
        {
            if (IsImagePath(path))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        var bmp = new Bitmap(path);
                        if (_imageCurrent != null)
                        {
                            _imageCurrent.Source = bmp;
                            _imageCurrent.IsVisible = true;
                        }
                        if (_editorCurrent != null) _editorCurrent.IsVisible = false;
                        if (_imageCurrent2 != null) { _imageCurrent2.Source = bmp; _imageCurrent2.IsVisible = true; }
                        if (_editorCurrent2 != null) _editorCurrent2.IsVisible = false;
                    }
                    catch (Exception ex)
                    {
                        CurrentFileDocument.Text = $"Failed to load image: {ex.Message}";
                        if (_imageCurrent != null) { _imageCurrent.IsVisible = false; _imageCurrent.Source = null; }
                        if (_editorCurrent != null) _editorCurrent.IsVisible = true;
                        if (_imageCurrent2 != null) { _imageCurrent2.IsVisible = false; _imageCurrent2.Source = null; }
                        if (_editorCurrent2 != null) _editorCurrent2.IsVisible = true;
                    }
                });
            }
            else
            {
                string text = string.Empty;
                using (var fs = File.OpenRead(path))
                using (var sr = new StreamReader(fs, Encoding.UTF8, true))
                {
                    text = await sr.ReadToEndAsync();
                }
                if (Dispatcher.UIThread.CheckAccess())
                {
                    CurrentFileDocument.Text = text;
                    ResetEditorViewport(_editorCurrent);
                    ResetEditorViewport(_editorCurrent2);
                }
                else
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        CurrentFileDocument.Text = text;
                        ResetEditorViewport(_editorCurrent);
                        ResetEditorViewport(_editorCurrent2);
                    });
                }
                UpdateSingleDiffGutter();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_imageCurrent != null) { _imageCurrent.IsVisible = false; _imageCurrent.Source = null; }
                    if (_editorCurrent != null) _editorCurrent.IsVisible = true;
                    if (_imageCurrent2 != null) { _imageCurrent2.IsVisible = false; _imageCurrent2.Source = null; }
                    if (_editorCurrent2 != null) _editorCurrent2.IsVisible = true;
                });
            }
        }
        catch (Exception ex)
        {
            if (Dispatcher.UIThread.CheckAccess())
                CurrentFileDocument.Text = $"Failed to read file: {ex.Message}";
            else
                Dispatcher.UIThread.Post(() => CurrentFileDocument.Text = $"Failed to read file: {ex.Message}");
        }

        // Apply syntax highlighting by file type
        TryApplySyntaxHighlightingForPath(_selectedFilePath);
        await RefreshBaseDocumentAsync();
    }

    private async Task RefreshBaseDocumentAsync()
    {
        if (!IsCompareMode)
        {
            // Clear base doc when not comparing
            if (Dispatcher.UIThread.CheckAccess())
            {
                BaseFileDocument.Text = string.Empty;
                ResetEditorViewport(_editorBase);
                if (_imageBase != null) { _imageBase.IsVisible = false; _imageBase.Source = null; }
                if (_editorBase != null) _editorBase.IsVisible = true;
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    BaseFileDocument.Text = string.Empty;
                    ResetEditorViewport(_editorBase);
                    if (_imageBase != null) { _imageBase.IsVisible = false; _imageBase.Source = null; }
                    if (_editorBase != null) _editorBase.IsVisible = true;
                });
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(_selectedFilePath))
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                BaseFileDocument.Text = string.Empty;
                ResetEditorViewport(_editorBase);
            }
            else
                Dispatcher.UIThread.Post(() =>
                {
                    BaseFileDocument.Text = string.Empty;
                    ResetEditorViewport(_editorBase);
                });
            return;
        }

        await Task.Run(() =>
        {
            try
            {
                if (IsImagePath(_selectedFilePath!))
                {
                    var (okImg, bytes, errImg) = Services.GitHelper.TryReadBytesAtHead(_selectedFilePath!);
                    if (okImg && bytes != null)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            try
                            {
                                var bmp = new Bitmap(new MemoryStream(bytes));
                                if (_imageBase != null)
                                {
                                    _imageBase.Source = bmp;
                                    _imageBase.IsVisible = true;
                                }
                                if (_editorBase != null) _editorBase.IsVisible = false;
                            }
                            catch (Exception ex)
                            {
                                BaseFileDocument.Text = $"Failed to load base image: {ex.Message}";
                                ResetEditorViewport(_editorBase);
                                if (_imageBase != null) { _imageBase.IsVisible = false; _imageBase.Source = null; }
                                if (_editorBase != null) _editorBase.IsVisible = true;
                            }
                        });
                    }
                    else
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            BaseFileDocument.Text = $"No HEAD version available: {errImg}";
                            ResetEditorViewport(_editorBase);
                            if (_imageBase != null) { _imageBase.IsVisible = false; _imageBase.Source = null; }
                            if (_editorBase != null) _editorBase.IsVisible = true;
                        });
                    }

                    // Skip text diff for images
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_baseDiffRenderer != null) _baseDiffRenderer.Update(null, new System.Collections.Generic.HashSet<int>());
                        if (_currentDiffRenderer != null) _currentDiffRenderer.Update(new System.Collections.Generic.HashSet<int>(), null);
                    });
                }
                else
                {
                    var (ok, content, error) = Services.GitHelper.TryReadTextAtHead(_selectedFilePath!);
                    var text = ok ? (content ?? string.Empty) : $"No HEAD version available: {error}";
                    if (Dispatcher.UIThread.CheckAccess())
                    {
                        BaseFileDocument.Text = text;
                        ResetEditorViewport(_editorBase);
                    }
                    else
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            BaseFileDocument.Text = text;
                            ResetEditorViewport(_editorBase);
                        });
                    }

                    // Compute diff sets for gutters
                    var (dok, baseDeleted, currentAdded, derr) = Services.GitHelper.TryGetLineDiffSets(_selectedFilePath!);
                    if (_baseDiffRenderer != null)
                    {
                        _baseDiffRenderer.Update(null, dok ? baseDeleted : new System.Collections.Generic.HashSet<int>());
                    }
                    if (_currentDiffRenderer != null)
                    {
                        _currentDiffRenderer.Update(dok ? currentAdded : new System.Collections.Generic.HashSet<int>(), null);
                    }
                    Dispatcher.UIThread.Post(() =>
                    {
                        _editorBase?.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
                        _editorCurrent2?.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
                        if (_imageBase != null) { _imageBase.IsVisible = false; _imageBase.Source = null; }
                        if (_editorBase != null) _editorBase.IsVisible = true;
                        if (_imageCurrent2 != null && _imageCurrent2.IsVisible)
                        {
                            _imageCurrent2.IsVisible = false; _imageCurrent2.Source = null;
                            if (_editorCurrent2 != null) _editorCurrent2.IsVisible = true;
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    BaseFileDocument.Text = $"Failed to read from git: {ex.Message}";
                    ResetEditorViewport(_editorBase);
                }
                else Dispatcher.UIThread.Post(() =>
                {
                    BaseFileDocument.Text = $"Failed to read from git: {ex.Message}";
                    ResetEditorViewport(_editorBase);
                });
            }
        });
    }

    private static bool IsImagePath(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".gif":
                case ".bmp":
                case ".ico":
                case ".tif":
                case ".tiff":
                    return true;
                default:
                    return false;
            }
        }
        catch { return false; }
    }

    private void RefreshEditorPanelsVisibility()
    {
        try
        {
            var single = this.FindControl<Control>("EditorSinglePanel");
            var compare = this.FindControl<Control>("EditorComparePanel");
            if (single != null) single.IsVisible = !IsCompareMode;
            if (compare != null) compare.IsVisible = IsCompareMode;
        }
        catch { }
    }

    private void OnEditorViewCurrentClick(object? sender, RoutedEventArgs e)
    {
        IsCompareMode = false;
    }

    private void OnEditorViewCompareClick(object? sender, RoutedEventArgs e)
    {
        IsCompareMode = true;
    }

    private FileTreeItem? _selectedFileItem;

    private async void OnSaveCurrentFileClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedFilePath))
        {
            AppendCliLog("System: No file selected to save.");
            return;
        }
        try
        {
            var text = CurrentFileDocument.Text ?? string.Empty;
            using (var fs = new FileStream(_selectedFilePath!, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var sw = new StreamWriter(fs, Encoding.UTF8))
            {
                await sw.WriteAsync(text);
            }
            AppendCliLog($"System: Saved '{_selectedFilePath}'.");

        // Refresh diff sets and base doc if compare is on
        await RefreshBaseDocumentAsync();
        // Refresh single-view gutter indicators
        UpdateSingleDiffGutter();

            // Update git status for the saved file only (avoid repo-wide status scan)
            try
            {
                var (ok, status) = Services.GitHelper.TryGetStatusForFile(_selectedFilePath!);
                if (ok && _selectedFileItem != null)
                {
                    _selectedFileItem.GitStatus = status;
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            AppendCliLog("System: Failed to save file: " + ex.Message);
        }
    }

    private static string NormalizePath(string p)
        => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private void ResetEditorViewport(TextEditor? editor)
    {
        if (editor is null) return;
        try
        {
            editor.Select(0, 0);
            if ((editor.Document?.LineCount ?? 0) > 0)
                editor.ScrollTo(1, 0);
            else
                editor.ScrollToHome();
            editor.TextArea.Caret.BringCaretToView();
        }
        catch { }
    }

    private void TrySetupEditors()
    {
        try
        {
            _editorBase ??= this.FindControl<TextEditor>("EditorBase");
            _editorCurrent ??= this.FindControl<TextEditor>("EditorCurrent");
            _editorCurrent2 ??= this.FindControl<TextEditor>("EditorCurrent2");
            _imageBase ??= this.FindControl<Image>("ImageBase");
            _imageCurrent ??= this.FindControl<Image>("ImageCurrent");
            _imageCurrent2 ??= this.FindControl<Image>("ImageCurrent2");

            _editorRegistryOptions ??= new RegistryOptions(ThemeName.DarkPlus);

            // Install TextMate highlighting for editors if needed
            if (_editorBase is not null)
            {
                try { var inst = _editorBase.InstallTextMate(_editorRegistryOptions); } catch { }
                if (_baseDiffRenderer == null)
                {
                    _baseDiffRenderer = new DiffGutterRenderer { ShowRemoved = true };
                    _editorBase.TextArea.TextView.BackgroundRenderers.Add(_baseDiffRenderer);
                }
            }
            if (_editorCurrent2 is not null)
            {
                try { var inst = _editorCurrent2.InstallTextMate(_editorRegistryOptions); } catch { }
                if (_currentDiffRenderer == null)
                {
                    _currentDiffRenderer = new DiffGutterRenderer { ShowAdded = true };
                    _editorCurrent2.TextArea.TextView.BackgroundRenderers.Add(_currentDiffRenderer);
                }
            }
            if (_editorCurrent is not null)
            {
                try { var inst = _editorCurrent.InstallTextMate(_editorRegistryOptions); } catch { }
                if (_singleDiffRenderer == null)
                {
                    _singleDiffRenderer = new DiffGutterRenderer { ShowAdded = true };
                    _editorCurrent.TextArea.TextView.BackgroundRenderers.Add(_singleDiffRenderer);
                }
            }

            EnsureScrollSyncHandlers();

            // Initialize buttons visibility based on current width
            try
            {
                var grid = this.FindControl<Control>("EditorPaneGrid");
                if (grid != null)
                {
                    UpdateEditorButtonsVisibility(grid.Bounds.Width);
                }
            }
            catch { }
        }
        catch { }
    }

    private void EnsureScrollSyncHandlers()
    {
        // Disabled: remove any existing sync handlers so panes scroll independently
        RemoveScrollSyncHandlers();
    }

    private void RemoveScrollSyncHandlers()
    {
        try
        {
            if (_editorBase?.TextArea?.TextView is { } vb)
                vb.VisualLinesChanged -= OnEditorBaseVisualLinesChanged;
            if (_editorCurrent2?.TextArea?.TextView is { } vc)
                vc.VisualLinesChanged -= OnEditorCurrent2VisualLinesChanged;

            _editorBase?.RemoveHandler(InputElement.PointerWheelChangedEvent, OnEditorBaseWheel);
            _editorCurrent2?.RemoveHandler(InputElement.PointerWheelChangedEvent, OnEditorCurrent2Wheel);
            _editorBase?.RemoveHandler(InputElement.PointerPressedEvent, OnEditorBasePointerPressed);
            _editorCurrent2?.RemoveHandler(InputElement.PointerPressedEvent, OnEditorCurrent2PointerPressed);
            if (_editorBase != null) _editorBase.GotFocus -= OnEditorBaseGotFocus;
            if (_editorCurrent2 != null) _editorCurrent2.GotFocus -= OnEditorCurrent2GotFocus;
        }
        catch { }
        _scrollHandlersAttached = false;
        _suppressScrollSync = false;
        _scrollMaster = ScrollMaster.None;
    }

    private void OnEditorBaseVisualLinesChanged(object? sender, EventArgs e)
    {
        if (!IsCompareMode) return;
        if (_suppressScrollSync) return;
        if (_editorBase is null || _editorCurrent2 is null) return;
        if (_scrollMaster != ScrollMaster.Base) return;
        var top = GetTopVisibleLine(_editorBase);
        if (top <= 0) return;
        _suppressScrollSync = true;
        try { _editorCurrent2.ScrollTo(top, 0); }
        catch { }
        finally { Dispatcher.UIThread.Post(() => _suppressScrollSync = false); }
    }

    private void OnEditorCurrent2VisualLinesChanged(object? sender, EventArgs e)
    {
        if (!IsCompareMode) return;
        if (_suppressScrollSync) return;
        if (_editorBase is null || _editorCurrent2 is null) return;
        if (_scrollMaster != ScrollMaster.Current) return;
        var top = GetTopVisibleLine(_editorCurrent2);
        if (top <= 0) return;
        _suppressScrollSync = true;
        try { _editorBase.ScrollTo(top, 0); }
        catch { }
        finally { Dispatcher.UIThread.Post(() => _suppressScrollSync = false); }
    }

    private static int GetTopVisibleLine(TextEditor editor)
    {
        try
        {
            var v = editor.TextArea.TextView.VisualLines;
            if (v is not null && v.Count > 0)
            {
                return v[0].FirstDocumentLine.LineNumber;
            }
        }
        catch { }
        return -1;
    }

    // Pointer wheel sync for more responsive coupling
    private void OnEditorBaseWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!IsCompareMode) return;
        if (_suppressScrollSync) return;
        if (_editorBase is null || _editorCurrent2 is null) return;
        _scrollMaster = ScrollMaster.Base;
        var top = GetTopVisibleLine(_editorBase);
        if (top <= 0) return;
        _suppressScrollSync = true;
        try { _editorCurrent2.ScrollTo(top, 0); }
        catch { }
        finally { Dispatcher.UIThread.Post(() => _suppressScrollSync = false); }
    }

    private void OnEditorCurrent2Wheel(object? sender, PointerWheelEventArgs e)
    {
        if (!IsCompareMode) return;
        if (_suppressScrollSync) return;
        if (_editorBase is null || _editorCurrent2 is null) return;
        _scrollMaster = ScrollMaster.Current;
        var top = GetTopVisibleLine(_editorCurrent2);
        if (top <= 0) return;
        _suppressScrollSync = true;
        try { _editorBase.ScrollTo(top, 0); }
        catch { }
        finally { Dispatcher.UIThread.Post(() => _suppressScrollSync = false); }
    }

    private void OnEditorBasePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _scrollMaster = ScrollMaster.Base;
    }

    private void OnEditorCurrent2PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _scrollMaster = ScrollMaster.Current;
    }

    private void OnEditorBaseGotFocus(object? sender, GotFocusEventArgs e)
    {
        _scrollMaster = ScrollMaster.Base;
    }

    private void OnEditorCurrent2GotFocus(object? sender, GotFocusEventArgs e)
    {
        _scrollMaster = ScrollMaster.Current;
    }

    private void OnEditorPaneSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        try
        {
            UpdateEditorButtonsVisibility(e.NewSize.Width);
        }
        catch { }
    }

    private void UpdateEditorButtonsVisibility(double width)
    {
        var panel = this.FindControl<Avalonia.Controls.StackPanel>("EditorButtonsPanel");
        if (panel != null)
        {
            panel.IsVisible = width >= EditorButtonsMinWidth;
        }
    }

    private void TryApplySyntaxHighlightingForPath(string? filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            string scope = ext switch
            {
                ".cs" => "source.cs",
                ".js" => "source.js",
                ".ts" => "source.ts",
                ".json" => "source.json",
                ".xml" or ".xaml" or ".axaml" or ".csproj" => "text.xml",
                ".yml" or ".yaml" => "source.yaml",
                ".py" => "source.python",
                ".rb" => "source.ruby",
                ".go" => "source.go",
                ".rs" => "source.rust",
                ".java" => "source.java",
                ".css" => "source.css",
                ".html" or ".htm" => "text.html.basic",
                ".md" => "text.md",
                ".sh" => "source.shell",
                ".ps1" => "source.powershell",
                ".sql" => "source.sql",
                ".toml" => "source.toml",
                ".ini" or ".sln" => "source.ini",
                _ => "text.plain"
            };

            // Apply to all three editors (current, base, current2)
            if (_editorRegistryOptions is null) _editorRegistryOptions = new RegistryOptions(ThemeName.DarkPlus);
            if (_editorCurrent is not null)
            {
                try { var inst = _editorCurrent.InstallTextMate(_editorRegistryOptions); inst.SetGrammar(scope); } catch { }
            }
            if (_editorCurrent2 is not null)
            {
                try { var inst = _editorCurrent2.InstallTextMate(_editorRegistryOptions); inst.SetGrammar(scope); } catch { }
            }
            if (_editorBase is not null)
            {
                try { var inst = _editorBase.InstallTextMate(_editorRegistryOptions); inst.SetGrammar(scope); } catch { }
            }
        }
        catch { }
    }

    private void UpdateSingleDiffGutter()
    {
        try
        {
            if (_singleDiffRenderer == null || string.IsNullOrWhiteSpace(_selectedFilePath)) return;
            var (ok, baseDeleted, currentAdded, err) = Services.GitHelper.TryGetLineDiffSets(_selectedFilePath!);
            _singleDiffRenderer.Update(ok ? currentAdded : new System.Collections.Generic.HashSet<int>(), null);
            try { _editorCurrent?.TextArea?.TextView?.InvalidateLayer(KnownLayer.Background); } catch { }
        }
        catch { }
    }

    private async Task RestartCliAsync()
    {
        if (!HasWorkspace || CurrentWorkspacePath is null)
            return;

        if (_cli.IsRunning)
            _cli.Stop();

        // Reset login prompt guard for a fresh session
        _loginAttempted = false;

        CliLog = string.Empty;
        SessionStatus = "starting…";
        try
        {
            var prevArgs = _cli.AdditionalArgs;
            var effectiveArgs = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(_settings.SelectedProfile))
            {
                effectiveArgs.Append("-c profile=").Append(_settings.SelectedProfile).Append(' ');
                AppendCliLog($"System: Using profile '{_settings.SelectedProfile}'");
            }

        _pendingConfigOverrides = null;
        if (IsMcpEnabled)
        {
            try
            {
                Services.McpConfigService.EnsureConfigExists();
                var overrides = BuildMcpConfigOverrides(out var serverNames);
                if (overrides is not null)
                {
                    _pendingConfigOverrides = overrides;
                    if (_verboseLogging)
                    {
                        if (serverNames.Count == 1)
                        {
                            AppendCliLog($"System: Injected MCP server '{serverNames[0]}'.");
                        }
                        else if (serverNames.Count > 1)
                        {
                            AppendCliLog($"System: Injected MCP servers: {string.Join(", ", serverNames)}.");
                        }
                        else
                        {
                            AppendCliLog("System: Injected MCP servers from config.");
                        }
                    }
                }
                else if (serverNames.Count == 0)
                {
                    AppendCliLog("System: No MCP servers configured; skipping.");
                }
                else if (_verboseLogging)
                {
                    AppendCliLog("System: Injected MCP servers from config.");
                }
            }
            catch (Exception ex)
            {
                AppendCliLog("System: Failed to prepare MCP config: " + ex.Message);
                }
            }
            // Apply args composed from profile overrides only (AdditionalArgs deprecated)
            var composed = effectiveArgs.ToString().Trim();
            _cli.AdditionalArgs = composed;

            // Proactive authentication: API key login or interactive chat login
            // 1) If user provided API key in settings, use it non-interactively
            if (_cli.UseApiKey && !string.IsNullOrWhiteSpace(_cli.ApiKey))
            {
                try
                {
                    AppendCliLog("System: Authenticating CLI with API key…");
                    var exitLogin = await RunCodexLoginWithApiKeyAsync(_cli.ApiKey!);
                    if (exitLogin == 0)
                    {
                        AppendCliLog("System: CLI authenticated via API key.");
                    }
                    else
                    {
                        AppendCliLog($"System: API key login failed (exit {exitLogin}).");
                        _cli.AdditionalArgs = prevArgs; // restore before abort
                        SessionStatus = "error";
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AppendCliLog("System: API key login error: " + ex.Message);
                    _cli.AdditionalArgs = prevArgs; // restore before abort
                    SessionStatus = "error";
                    return;
                }
            }
            else
            {
                // 2) No API key in settings: probe ~/.codex/auth.json
                var authProbe = Services.CodexAuthService.ProbeAuth();
                if (authProbe.Exists && authProbe.HasTokens)
                {
                    // Tokens present — assume already authenticated; skip proactive login
                    AppendCliLog("System: Found existing Codex auth tokens; skipping login.");
                }
                else
                {
                    // Regardless of API key present in auth.json, settings dictate NOT to use API key.
                    // Fall back to interactive login to avoid implicit API key usage.
                    try
                    {
                        AppendCliLog("System: Authenticating CLI via 'codex login'…");
                        var exit = await RunCodexAuthLoginAsync();
                        if (exit == 0)
                        {
                            AppendCliLog("System: CLI authenticated (chat login).");
                        }
                        else
                        {
                            AppendCliLog($"System: CLI login failed (exit {exit}).");
                            _cli.AdditionalArgs = prevArgs; // restore before abort
                            SessionStatus = "error";
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendCliLog("System: CLI login error: " + ex.Message);
                        _cli.AdditionalArgs = prevArgs; // restore before abort
                        SessionStatus = "error";
                        return;
                    }
                }
            }

            await _cli.StartAsync(CurrentWorkspacePath, CancellationToken.None);
            _cli.AdditionalArgs = prevArgs; // restore original
            IsCliRunning = _cli.IsRunning;
            if (!IsCliRunning)
            {
                SessionStatus = "stopped";
                return;
            }

            _pendingRequests.Clear();
            _nextRequestId = 0;
            _appServerInitialized = false;
            _conversationId = null;
            _conversationSubscriptionId = null;

            try
            {
                await InitializeAppServerAsync();
                SessionStatus = "idle";
            }
            catch (Exception initEx)
            {
                AppendCliLog("System: Failed to initialize Codex: " + initEx.Message);
                SessionStatus = "error";
                _cli.Stop();
                IsCliRunning = false;
            }
        }
        catch (Exception ex)
        {
            AppendCliLog("System: Failed to start CLI: " + ex.Message);
            SessionStatus = "error";
        }
    }

    private async Task InitializeAppServerAsync()
    {
        if (!_cli.IsRunning)
            throw new InvalidOperationException("CLI is not running.");

        var version = typeof(SessionView).Assembly?.GetName().Version?.ToString() ?? "dev";
        var initParams = new JObject
        {
            ["clientInfo"] = new JObject
            {
                ["name"] = "semantic-developer",
                ["title"] = "Semantic Developer",
                ["version"] = version
            }
        };

        await SendRequestAsync("initialize", initParams);
        _appServerInitialized = true;
        AppendCliLog("System: Codex app server ready.");

        await StartConversationAsync();
    }

    private async Task StartConversationAsync()
    {
        if (!_appServerInitialized)
            throw new InvalidOperationException("Codex app server not initialized.");

        var conversationParams = new JObject
        {
            ["approvalPolicy"] = "on-request",
            ["sandbox"] = "workspace-write"
        };

        if (_pendingConfigOverrides is { })
            conversationParams["config"] = (JObject)_pendingConfigOverrides.DeepClone();

        if (!string.IsNullOrWhiteSpace(_settings.SelectedProfile))
            conversationParams["profile"] = _settings.SelectedProfile;

        if (!string.IsNullOrWhiteSpace(CurrentWorkspacePath))
            conversationParams["cwd"] = CurrentWorkspacePath;

        var response = await SendRequestAsync("newConversation", conversationParams);
        if (response is JObject obj)
        {
            _conversationId = obj["conversationId"]?.ToString() ?? _conversationId;
            var model = obj["model"]?.ToString();
            if (!string.IsNullOrWhiteSpace(model))
            {
                _currentModel = model;
                AppendCliLog($"System: Model • {model}");
            }
        }

        await SubscribeToConversationAsync();
        _ = RefreshMcpToolInventoryAsync();
    }

    private async Task SubscribeToConversationAsync()
    {
        if (string.IsNullOrWhiteSpace(_conversationId))
            return;

        var response = await SendRequestAsync(
            "addConversationListener",
            new JObject { ["conversationId"] = _conversationId }
        );

        if (response is JObject obj)
        {
            _conversationSubscriptionId = obj["subscriptionId"]?.ToString();
        }
    }

    private void HandleServerResponse(JObject response)
    {
        if (!response.TryGetValue("id", out var idToken))
            return;

        if (!TryGetRequestId(idToken, out var id))
            return;

        if (!_pendingRequests.TryRemove(id, out var tcs))
            return;

        if (response["error"] is JObject error)
        {
            var message = error["message"]?.ToString() ?? "Unknown Codex error";
            tcs.TrySetException(new InvalidOperationException(message));
            if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
            AppendCliLog("System: ERROR: " + message);
            if (message.IndexOf("401", StringComparison.OrdinalIgnoreCase) >= 0)
                _ = HandleUnauthorizedAsync();
            SetStatusSafe("error");
        }
        else
        {
            tcs.TrySetResult(response["result"] ?? JValue.CreateNull());
        }
    }

    private void HandleServerNotification(string method, JObject? parameters)
    {
        if (string.IsNullOrEmpty(method))
            return;

        switch (method)
        {
            case "authStatusChange":
                break;
            case "loginChatGptComplete":
                if (parameters? ["success"]?.Value<bool>() == true)
                    AppendCliLog("System: ChatGPT login completed.");
                else if (parameters != null)
                    AppendCliLog("System: ChatGPT login failed: " + (parameters["error"]?.ToString() ?? "unknown error"));
                break;
            case "sessionConfigured":
                if (parameters is not null)
                    HandleSessionConfiguredNotification(parameters);
                break;
            default:
                if (method.StartsWith("codex/event/", StringComparison.Ordinal))
                {
                    if (parameters is JObject evt)
                    {
                        var handled = TryRenderProtocolEvent(evt);
                        if (!handled && _verboseLogging)
                        {
                            AppendCliLog(evt.ToString(Formatting.None));
                        }
                    }
                }
                else if (_verboseLogging)
                {
                    AppendCliLog($"System: Notification {method}: {parameters?.ToString(Formatting.None) ?? string.Empty}");
                }
                break;
        }
    }

    private void HandleSessionConfiguredNotification(JObject payload)
    {
        try
        {
            var sessionId = payload["sessionId"]?.ToString();
            if (!string.IsNullOrWhiteSpace(sessionId))
                _conversationId = sessionId;

            var model = payload["model"]?.ToString();
            if (!string.IsNullOrWhiteSpace(model))
            {
                _currentModel = model;
                AppendCliLog($"System: Session model • {model}");
            }

            if (payload["initialMessages"] is JArray initial)
            {
                foreach (var item in initial.OfType<JObject>())
                {
                    _ = TryRenderProtocolEvent(item);
                }
            }
        }
        catch { }
    }

    private Task HandleServerRequestAsync(JObject request)
    {
        return Task.Run(async () =>
        {
            var method = request["method"]?.ToString() ?? string.Empty;
            var idToken = request["id"];
            if (idToken is null)
                return;

            try
            {
                switch (method)
                {
                    case "execCommandApproval":
                        LogApprovalSummary("exec", request["params"] as JObject);
                        await SendServerResponseAsync(idToken, new JObject { ["decision"] = "approved" });
                        break;
                    case "applyPatchApproval":
                        LogApprovalSummary("patch", request["params"] as JObject);
                        await SendServerResponseAsync(idToken, new JObject { ["decision"] = "approved" });
                        break;
                    default:
                        AppendCliLog($"System: Unhandled request '{method}'");
                        await SendServerErrorAsync(idToken, -32601, $"Unsupported request '{method}'");
                        break;
                }
            }
            catch (Exception ex)
            {
                AppendCliLog("System: Failed to respond to request: " + ex.Message);
            }
        });
    }

    private async Task SendServerResponseAsync(JToken idToken, JObject result)
    {
        var response = new JObject
        {
            ["id"] = idToken.DeepClone(),
            ["result"] = result
        };
        await _cli.SendAsync(response.ToString(Formatting.None)).ConfigureAwait(false);
    }

    private async Task SendServerErrorAsync(JToken idToken, int code, string message)
    {
        var response = new JObject
        {
            ["id"] = idToken.DeepClone(),
            ["error"] = new JObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
        await _cli.SendAsync(response.ToString(Formatting.None)).ConfigureAwait(false);
    }

    private void LogApprovalSummary(string kind, JObject? parameters)
    {
        if (parameters is null)
            return;

        try
        {
            string summary = string.Empty;
            if (kind == "exec")
            {
                var cmd = parameters["command"] as JArray;
                if (cmd != null)
                {
                    var tokens = cmd.Select(t => t?.ToString() ?? string.Empty).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                    if (tokens.Count > 0)
                        summary = string.Join(" ", tokens.Take(5));
                }
            }
            else if (kind == "patch")
            {
                var files = parameters["file_changes"] as JObject;
                if (files != null)
                {
                    summary = $"{files.Properties().Count()} file(s)";
                }
            }

            if (!string.IsNullOrEmpty(summary))
            {
                if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
                AppendCliLog($"System: Auto-approved {kind}: {summary}");
            }
        }
        catch { }
    }

    private async Task<JToken> SendRequestAsync(string method, JObject? parameters = null, CancellationToken cancellationToken = default)
    {
        if (!_cli.IsRunning)
            throw new InvalidOperationException("CLI is not running.");

        var id = Interlocked.Increment(ref _nextRequestId);
        var request = new JObject
        {
            ["id"] = id,
            ["method"] = method
        };

        if (parameters != null && parameters.HasValues)
            request["params"] = parameters;

        var payload = request.ToString(Formatting.None);

        var tcs = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        try
        {
            await _cli.SendAsync(payload).ConfigureAwait(false);
        }
        catch (Exception sendEx)
        {
            _pendingRequests.TryRemove(id, out _);
            tcs.TrySetException(sendEx);
        }

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                if (_pendingRequests.TryRemove(id, out var source))
                    source.TrySetCanceled();
            });
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    private static bool TryGetRequestId(JToken token, out long id)
    {
        id = 0;
        try
        {
            id = token.Type switch
            {
                JTokenType.Integer => token.Value<long>(),
                JTokenType.String when long.TryParse(token.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => id
            };
            return id != 0 || token.Type == JTokenType.Integer;
        }
        catch
        {
            return false;
        }
    }

    private async Task InterruptCliAsync()
    {
        if (!_cli.IsRunning) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(_conversationId))
            {
                await SendRequestAsync(
                    "interruptConversation",
                    new JObject { ["conversationId"] = _conversationId }
                );
            }
            SetStatusSafe("idle");
        }
        catch
        {
            _cli.Stop();
            IsCliRunning = false;
            SessionStatus = "stopped";
        }
    }

    private JObject? BuildMcpConfigOverrides(out List<string> serverNames)
    {
        _configuredMcpServers.Clear();
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        serverNames = new List<string>();
        try
        {
            var path = Services.McpConfigService.GetConfigPath();
            if (!File.Exists(path))
                return null;

            var root = JObject.Parse(File.ReadAllText(path));
            var selected = new HashSet<string>(McpServers.Where(s => s.Selected).Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            bool HasSelection() => selected.Count > 0;

            var result = new JObject();
            var mcpServersObj = new JObject();
            result["mcp_servers"] = mcpServersObj;

            void AddServer(string rawName, JObject definition)
            {
                if (definition is null) return;

                var enabledTok = definition["enabled"];
                if (enabledTok != null && bool.TryParse(enabledTok.ToString(), out var enabled) && !enabled)
                    return;

                var name = SanitizeServerName(rawName);
                if (string.IsNullOrWhiteSpace(name))
                    return;
                if (HasSelection() && !selected.Contains(name))
                    return;

                var commandText = definition["command"]?.ToString();
                var urlText = definition["url"]?.ToString();
                if (string.IsNullOrWhiteSpace(commandText) && string.IsNullOrWhiteSpace(urlText))
                    return;

                var serverObj = new JObject();
                var def = new McpServerDefinition { Name = name };

                if (!string.IsNullOrWhiteSpace(commandText))
                {
                    commandText = commandText.Trim();
                    serverObj["command"] = commandText;
                    def.Command = commandText;

                    if (definition["args"] is JArray argsArray && argsArray.Count > 0)
                    {
                        var args = argsArray
                            .Select(a => a?.ToString() ?? string.Empty)
                            .Where(a => !string.IsNullOrWhiteSpace(a))
                            .ToList();
                        if (args.Count > 0)
                        {
                            serverObj["args"] = new JArray(args);
                            def.Args.AddRange(args);
                        }
                    }
                    else if (definition["args"] is JValue argValue && argValue.Type == JTokenType.String)
                    {
                        var argText = argValue.ToString();
                        if (!string.IsNullOrWhiteSpace(argText))
                        {
                            serverObj["args"] = new JArray(argText);
                            def.Args.Add(argText);
                        }
                    }

                    var cwdToken = definition["cwd"];
                    if (cwdToken != null && !string.IsNullOrWhiteSpace(cwdToken.ToString()))
                    {
                        var cwd = cwdToken.ToString();
                        serverObj["cwd"] = cwd;
                        def.Cwd = cwd;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(urlText))
                {
                    urlText = urlText.Trim();
                    serverObj["url"] = urlText;
                    def.Url = urlText;
                    var bearerToken = definition["bearer_token"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(bearerToken))
                    {
                        serverObj["bearer_token"] = bearerToken;
                        def.BearerToken = bearerToken;
                    }
                }

                var startupSecToken = definition["startup_timeout_sec"];
                if (startupSecToken != null && double.TryParse(startupSecToken.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var startupSec))
                {
                    serverObj["startup_timeout_sec"] = startupSec;
                    def.StartupTimeoutSec = startupSec;
                }

                var startupMsToken = definition["startup_timeout_ms"];
                if (startupMsToken != null && long.TryParse(startupMsToken.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var startupMs))
                    serverObj["startup_timeout_ms"] = startupMs;

                var toolTimeoutToken = definition["tool_timeout_sec"];
                if (toolTimeoutToken != null && double.TryParse(toolTimeoutToken.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var toolTimeout))
                {
                    serverObj["tool_timeout_sec"] = toolTimeout;
                    def.ToolTimeoutSec = toolTimeout;
                }

                if (definition["env"] is JObject envObj && envObj.Properties().Any())
                {
                    var env = new JObject();
                    foreach (var prop in envObj.Properties())
                    {
                        var value = prop.Value?.ToString() ?? string.Empty;
                        env[prop.Name] = value;
                        def.Env[prop.Name] = value;
                    }
                    serverObj["env"] = env;
                }

                if (!serverObj.HasValues)
                    return;

                mcpServersObj[name] = serverObj;
                _configuredMcpServers.Add(def);
                included.Add(name);
            }

            if (root["mcpServers"] is JObject map)
            {
                foreach (var property in map.Properties())
                {
                    if (property.Value is JObject definition)
                        AddServer(property.Name, definition);
                }
            }
            else if (root["servers"] is JArray legacy)
            {
                foreach (var definition in legacy.OfType<JObject>())
                {
                    var name = definition["name"]?.ToString() ?? string.Empty;
                    AddServer(name, definition);
                }
            }

            if (root["experimental_use_rmcp_client"] is JToken rmcpToken)
            {
                if (rmcpToken.Type == JTokenType.Boolean)
                    result["experimental_use_rmcp_client"] = rmcpToken.Value<bool>();
                else if (bool.TryParse(rmcpToken.ToString(), out var rmcp))
                    result["experimental_use_rmcp_client"] = rmcp;
            }

            if (!included.Any())
            {
                _configuredMcpServers.Clear();
                return null;
            }

            serverNames = included.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            return result;
        }
        catch
        {
            _configuredMcpServers.Clear();
            included.Clear();
        }

        serverNames = included.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return null;
    }

    private Task RefreshMcpToolInventoryAsync()
    {
        var servers = _configuredMcpServers.Select(s => s.Clone()).ToList();
        if (servers.Count == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var entry in McpServers)
                    entry.Tools.Clear();
            });
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            var results = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var successServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var discoverySkipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var logs = new List<(string Message, bool Always)>();

            foreach (var server in servers)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(server.Url))
                    {
                        logs.Add(($"System: MCP server '{server.Name}' uses streamable transport; tool discovery is not yet supported.", true));
                        results[server.Name] = new List<string>();
                        discoverySkipped.Add(server.Name);
                        continue;
                    }

                    var tools = await TryFetchToolsAsync(server, CancellationToken.None).ConfigureAwait(false);
                    results[server.Name] = tools;
                    successServers.Add(server.Name);
                    if (tools.Count > 0)
                        logs.Add(($"System: MCP tools • {server.Name}: {string.Join(", ", tools)}", false));
                    else
                        logs.Add(($"System: MCP tools • {server.Name}: none detected", false));
                }
                catch (Exception ex)
                {
                    logs.Add(($"System: MCP tool probe failed for '{server.Name}': {ex.Message}", true));
                    results[server.Name] = new List<string>();
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    foreach (var entry in McpServers)
                    {
                        entry.Tools.Clear();
                        if (!results.TryGetValue(entry.Name, out var toolNames))
                            continue;

                        foreach (var toolName in toolNames)
                        {
                            entry.Tools.Add(new ToolItem
                            {
                                Short = toolName,
                                Full = string.IsNullOrWhiteSpace(entry.Name) ? toolName : $"{entry.Name}.{toolName}"
                            });
                        }

                        if (successServers.Contains(entry.Name))
                        {
                            var count = toolNames.Count;
                            if (count == 0)
                                AppendCliLog($"System: MCP '{entry.Name}' server started (no tools detected).");
                            else
                                AppendCliLog($"System: MCP '{entry.Name}' server started with {count} tool{(count == 1 ? string.Empty : "s")}.");
                        }
                        else if (discoverySkipped.Contains(entry.Name))
                        {
                            AppendCliLog($"System: MCP '{entry.Name}' server started (tool discovery skipped).");
                        }
                    }

                    foreach (var (message, always) in logs)
                    {
                        if (_verboseLogging || always)
                            AppendCliLog(message);
                    }
                }
                catch (Exception ex)
                {
                    AppendCliLog("System: Failed to update MCP tools UI: " + ex.Message);
                }
            });
        });
    }

    private async Task<List<string>> TryFetchToolsAsync(McpServerDefinition server, CancellationToken cancellationToken)
    {
        var tools = new List<string>();

        if (string.IsNullOrWhiteSpace(server.Command))
            return tools;

        var psi = new ProcessStartInfo
        {
            FileName = server.Command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(server.Cwd))
        {
            try { psi.WorkingDirectory = server.Cwd; } catch { }
        }

        foreach (var arg in server.Args)
            psi.ArgumentList.Add(arg);

        foreach (var kvp in server.Env)
        {
            try { psi.Environment[kvp.Key] = kvp.Value; } catch { }
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
        {
            AppendCliLog($"System: Failed to start MCP server '{server.Name}' for tool probe.");
            return tools;
        }

        process.StandardInput.NewLine = "\n";
        process.StandardInput.AutoFlush = true;

        var sawStderr = false;
        string? firstStderrLine = null;
        var stderrTask = Task.Run(async () =>
        {
            try
            {
                string? errLine;
                while ((errLine = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (!string.IsNullOrWhiteSpace(errLine))
                    {
                        if (_verboseLogging)
                        {
                            AppendCliLog($"System: MCP '{server.Name}' stderr: {errLine}");
                        }
                        else
                        {
                            if (!sawStderr)
                                firstStderrLine = errLine;
                            sawStderr = true;
                        }
                    }
                }
            }
            catch { }
        });

        var stdout = process.StandardOutput;
        var stdin = process.StandardInput;

        var timeoutSeconds = server.StartupTimeoutSec.HasValue && server.StartupTimeoutSec > 0
            ? server.StartupTimeoutSec.Value
            : 15.0;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await SendJsonAsync(stdin, new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "initialize",
                ["params"] = new JObject
                {
                    ["clientInfo"] = new JObject
                    {
                        ["name"] = "semantic-developer",
                        ["version"] = typeof(SessionView).Assembly?.GetName().Version?.ToString() ?? "dev"
                    },
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JObject()
                }
            }, timeoutCts.Token).ConfigureAwait(false);

            var initResponse = await ReadResponseAsync(stdout, 1, timeoutCts.Token).ConfigureAwait(false);
            if (initResponse? ["error"] != null)
            {
                var message = initResponse["error"]?["message"]?.ToString() ?? "unknown error";
                AppendCliLog($"System: MCP '{server.Name}' init failed: {message}");
                return tools;
            }

            await SendJsonAsync(stdin, new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/initialized",
                ["params"] = new JObject()
            }, timeoutCts.Token).ConfigureAwait(false);

            await SendJsonAsync(stdin, new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/list",
                ["params"] = new JObject()
            }, timeoutCts.Token).ConfigureAwait(false);

            var listResponse = await ReadResponseAsync(stdout, 2, timeoutCts.Token).ConfigureAwait(false);
            if (listResponse? ["error"] != null)
            {
                var message = listResponse["error"]?["message"]?.ToString() ?? "unknown error";
                AppendCliLog($"System: MCP '{server.Name}' list tools failed: {message}");
                return tools;
            }

            if (listResponse? ["result"]?["tools"] is JArray toolArray)
            {
                foreach (var tool in toolArray.OfType<JObject>())
                {
                    var name = tool["name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name) && !tools.Contains(name, StringComparer.OrdinalIgnoreCase))
                        tools.Add(name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            AppendCliLog($"System: MCP '{server.Name}' tool probe timed out after {timeoutSeconds:F0}s.");
        }
        catch (Exception ex)
        {
            AppendCliLog($"System: MCP '{server.Name}' tool probe error: {ex.Message}");
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    try
                    {
                        await SendJsonAsync(stdin, new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["method"] = "notifications/shutdown",
                            ["params"] = new JObject()
                        }, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { }

                    try { process.Kill(true); } catch { }
                }
            }
            catch { }
            finally
            {
                try { await process.WaitForExitAsync(); } catch { }
                try { await stderrTask.ConfigureAwait(false); } catch { }
                if (!_verboseLogging && sawStderr)
                {
                    var trimmed = firstStderrLine?.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed) && trimmed.Length > 120)
                        trimmed = trimmed.Substring(0, 120) + "…";
                    var suffix = string.IsNullOrWhiteSpace(trimmed) ? string.Empty : $" (first line: {trimmed})";
                    AppendCliLog($"System: MCP '{server.Name}' emitted stderr output{suffix}. Enable verbose logging for full details.");
                }
            }
        }

        return tools;
    }

    private static async Task SendJsonAsync(TextWriter writer, JObject payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = payload.ToString(Formatting.None);
        await writer.WriteLineAsync(json).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<JObject?> ReadResponseAsync(StreamReader reader, long targetId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await ReadJsonMessageAsync(reader, cancellationToken).ConfigureAwait(false);
            if (message is null)
                return null;

            if (message.TryGetValue("id", out var idToken) && idToken != null && idToken.Type != JTokenType.Null)
            {
                if (TryGetRequestId(idToken, out var id) && id == targetId)
                    return message;
            }
        }
    }

    private static async Task<JObject?> ReadJsonMessageAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await ReadLineWithCancellationAsync(reader, cancellationToken).ConfigureAwait(false);
            if (line is null)
                return null;
            line = line.Trim();
            if (line.Length == 0)
                continue;
            try
            {
                return JObject.Parse(line);
            }
            catch
            {
                continue;
            }
        }
    }

    private static async Task<string?> ReadLineWithCancellationAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var readTask = reader.ReadLineAsync();
        var completed = await Task.WhenAny(readTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        if (completed != readTask)
            throw new OperationCanceledException(cancellationToken);
        return await readTask.ConfigureAwait(false);
    }

    private static string SanitizeServerName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return string.Empty;
        return new string(rawName.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '-').ToArray());
    }

    private static string ParseServerNameFromTool(string toolName)
    {
        var parts = SplitToolIdentifier(toolName);
        return parts.Server;
    }

    private static (string Server, string Short) SplitToolIdentifier(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return (string.Empty, string.Empty);
        // Prefer dot separator: server.tool
        var idx = id.IndexOf('.');
        if (idx > 0 && idx + 1 < id.Length)
        {
            return (id.Substring(0, idx), id.Substring(idx + 1));
        }
        // Fallback: double underscore server__tool
        var sep = "__";
        var i2 = id.IndexOf(sep, StringComparison.Ordinal);
        if (i2 > 0 && i2 + sep.Length < id.Length)
        {
            return (id.Substring(0, i2), id.Substring(i2 + sep.Length));
        }
        // As-is: treat whole string as short name with no server
        return (string.Empty, id);
    }

    public class McpServerEntry : INotifyPropertyChanged
    {
        private bool _selected;
        public string Name { get; set; } = string.Empty;
        public bool Selected
        {
            get => _selected;
            set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); }
        }
        public ObservableCollection<ToolItem> Tools { get; } = new();
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class ToolItem
    {
        public string Short { get; set; } = string.Empty;
        public string Full { get; set; } = string.Empty;
    }

    private class McpServerDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string? Command { get; set; }
        public List<string> Args { get; } = new();
        public string? Cwd { get; set; }
        public Dictionary<string, string> Env { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Url { get; set; }
        public string? BearerToken { get; set; }
        public double? StartupTimeoutSec { get; set; }
        public double? ToolTimeoutSec { get; set; }

        public McpServerDefinition Clone()
        {
            var clone = new McpServerDefinition
            {
                Name = Name,
                Command = Command,
                Cwd = Cwd,
                Url = Url,
                BearerToken = BearerToken,
                StartupTimeoutSec = StartupTimeoutSec,
                ToolTimeoutSec = ToolTimeoutSec
            };
            clone.Args.AddRange(Args);
            foreach (var kvp in Env)
                clone.Env[kvp.Key] = kvp.Value;
            return clone;
        }
    }

    // McpServerToolGroup removed; combined into McpServerEntry

    private async Task HandleUnauthorizedAsync()
    {
        // If user opted to use an API key, attempt non-interactive login with it
        if (_cli.UseApiKey && !string.IsNullOrWhiteSpace(_cli.ApiKey))
        {
            if (_loginInProgress) return;
            if (_loginAttempted) return;
            _loginInProgress = true;
            try
            {
                _loginAttempted = true;
                AppendCliLog("System: 401 detected. Attempting API key login…");
                // Stop current session before login
                try { await InterruptCliAsync(); } catch { }
                var exitApi = await RunCodexLoginWithApiKeyAsync(_cli.ApiKey!);
                if (exitApi == 0)
                {
                    AppendCliLog("System: API key login succeeded. Restarting session…");
                    await RestartCliAsync();
                }
                else
                {
                    AppendCliLog($"System: API key login failed (exit {exitApi}).");
                    SetStatusSafe("error");
                }
            }
            finally
            {
                _loginInProgress = false;
            }
            return;
        }
        if (_loginInProgress) return;
        // Avoid spamming prompts on repeated stream_error lines in one turn
        if (_loginAttempted) return;
        _loginInProgress = true;
        try
        {
            _loginAttempted = true;
            AppendCliLog("System: 401 Unauthorized detected. Codex CLI login required (API key not in use).");
            var ok = await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var dlg = new ConfirmDialog
                {
                    Title = "Login Required",
                    Message = "Received 401 Unauthorized. You are not logged in.\nRun 'codex auth login' to authenticate now?"
                };
                var window = GetHostWindow();
                if (window is null)
                    return false;
                return await dlg.ShowDialog<bool>(window);
            });
            if (!ok)
            {
                AppendCliLog("System: Login canceled by user.");
                return;
            }

            // Stop current proto session to keep the log focused during login
            try { await InterruptCliAsync(); } catch { }

            AppendCliLog("System: Starting Codex CLI login…");
            var exit = await RunCodexAuthLoginAsync();
            if (exit == 0)
            {
                AppendCliLog("System: Login successful. Restarting session…");
                await RestartCliAsync();
            }
            else
            {
                AppendCliLog($"System: Login failed (exit {exit}). You can set an API key in CLI Settings or retry login.");
                SetStatusSafe("error");
            }
        }
        finally
        {
            _loginInProgress = false;
        }
    }

    private async Task<int> RunCodexAuthLoginAsync()
    {
        var cwd = HasWorkspace && !string.IsNullOrWhiteSpace(CurrentWorkspacePath)
            ? CurrentWorkspacePath!
            : Directory.GetCurrentDirectory();

        async Task<int> RunAsync(params string[] commandArgs)
        {
            try
            {
                var psi = await _cli.BuildProcessStartInfoAsync(cwd, commandArgs, redirectStdIn: true);
                using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

                void HandleLine(string? line)
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    AppendCliLog(line);
                    // Best-effort: auto-open any login URL we see
                    try
                    {
                        var idx = line.IndexOf("http", StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                        {
                            var urlCandidate = ExtractUrl(line.Substring(idx));
                            if (!string.IsNullOrWhiteSpace(urlCandidate))
                            {
                                TryOpenUrl(urlCandidate!);
                            }
                        }
                    }
                    catch { }

                    // If CLI asks for a verification code, prompt the user and pass it to stdin
                    if (line.Contains("verification code", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("enter code", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("paste code", StringComparison.OrdinalIgnoreCase))
                    {
                        Dispatcher.UIThread.Post(async () =>
                        {
                            try
                            {
                                var dlg = new InputDialog { Title = "Enter Verification Code" };
                                dlg.Prompt = "Paste the verification code from the browser:";
                                dlg.Input = string.Empty;
                                var window = GetHostWindow();
                                if (window is null)
                                    return;
                                var result = await dlg.ShowDialog<InputDialogResult?>(window);
                                var code = result?.Text;
                                if (!string.IsNullOrWhiteSpace(code))
                                {
                                    try { await p.StandardInput.WriteLineAsync(code); } catch { }
                                }
                            }
                            catch { }
                        });
                    }
                }

                p.OutputDataReceived += (_, ev) => { if (ev.Data != null) HandleLine(ev.Data); };
                p.ErrorDataReceived += (_, ev) => { if (ev.Data != null) HandleLine(ev.Data); };

                if (!p.Start()) return -1;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                await p.WaitForExitAsync();
                return p.ExitCode;
            }
            catch (Exception ex)
            {
                AppendCliLog("System: Login command error: " + ex.Message);
                return -1;
            }
        }

        // Try modern subcommand first, then fallback
        var exit = await RunAsync("auth", "login");
        if (exit == 0) return exit;
        // Fallback: some versions may use `login`
        return await RunAsync("login");
    }

    private async Task<int> RunCodexLoginWithApiKeyAsync(string apiKey)
    {
        var cwd = HasWorkspace && !string.IsNullOrWhiteSpace(CurrentWorkspacePath)
            ? CurrentWorkspacePath!
            : Directory.GetCurrentDirectory();
        try
        {
            var psi = await _cli.BuildProcessStartInfoAsync(cwd, new[] { "login", "--with-api-key" }, redirectStdIn: true);

            using (var p = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                p.OutputDataReceived += (_, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) AppendCliLog(ev.Data!); };
                p.ErrorDataReceived += (_, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) AppendCliLog(ev.Data!); };
                if (!p.Start()) return -1;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                try
                {
                    p.StandardInput.NewLine = "\n";
                    await p.StandardInput.WriteLineAsync(apiKey);
                    await p.StandardInput.FlushAsync();
                    p.StandardInput.Close();
                }
                catch { }
                await p.WaitForExitAsync();
                if (p.ExitCode == 0) return 0;
            }

            // Fallback: codex auth login --with-api-key
            var psi2 = await _cli.BuildProcessStartInfoAsync(cwd, new[] { "auth", "login", "--with-api-key" }, redirectStdIn: true);

            using var p2 = new Process { StartInfo = psi2, EnableRaisingEvents = true };
            p2.OutputDataReceived += (_, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) AppendCliLog(ev.Data!); };
            p2.ErrorDataReceived += (_, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) AppendCliLog(ev.Data!); };
            if (!p2.Start()) return -1;
            p2.BeginOutputReadLine();
            p2.BeginErrorReadLine();
            try
            {
                p2.StandardInput.NewLine = "\n";
                await p2.StandardInput.WriteLineAsync(apiKey);
                await p2.StandardInput.FlushAsync();
                p2.StandardInput.Close();
            }
            catch { }
            await p2.WaitForExitAsync();
            return p2.ExitCode;
        }
        catch (Exception ex)
        {
            AppendCliLog("System: API key login command error: " + ex.Message);
            return -1;
        }
    }

    private static string? ExtractUrl(string text)
    {
        try
        {
            // Very loose URL extraction: take up to first whitespace
            var s = text.Trim();
            int end = s.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '"', '\'', ')' });
            var url = end > 0 ? s.Substring(0, end) : s;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return url;
        }
        catch { }
        return null;
    }

    private void TryOpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start("xdg-open", url);
            }
        }
        catch { }
    }

    // UI handlers
    private async void OnSelectWorkspaceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await SelectWorkspaceAsync();

    private async void OnRestartCliClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await RestartCliAsync();

    private async void OnStopCliClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await InterruptCliAsync();

    private void OnClearLogClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CliLog = string.Empty;
        try { CliLogDocument.Text = string.Empty; } catch { }
        _logAutoScroll = true;
        try { _logEditor?.ScrollToHome(); } catch { }
    }

    private void OnOpenInFileManagerClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!HasWorkspace || CurrentWorkspacePath is null) return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer", CurrentWorkspacePath) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", CurrentWorkspacePath);
            }
            else
            {
                Process.Start("xdg-open", CurrentWorkspacePath);
            }
        }
        catch (Exception ex)
        {
            AppendCliLog("System: Failed to open file manager: " + ex.Message);
        }
    }

    public async Task<AppSettings?> ShowCliSettingsDialogAsync(AppSettings? seed = null)
    {
        var window = GetHostWindow();
        if (window is null)
            return null;
        var dialog = new CliSettingsDialog();
        var baseSettings = seed is not null ? CloneSettings(seed) : CloneSettings(_settings);
        var vm = new CliSettings
        {
            Command = baseSettings.Command,
            VerboseLoggingEnabled = baseSettings.VerboseLoggingEnabled,
            McpEnabled = baseSettings.McpEnabled,
            UseApiKey = baseSettings.UseApiKey,
            ApiKey = baseSettings.ApiKey,
            AllowNetworkAccess = baseSettings.AllowNetworkAccess,
            ShowMcpResultsInLog = baseSettings.ShowMcpResultsInLog,
            ShowMcpResultsOnlyWhenNoEdits = baseSettings.ShowMcpResultsOnlyWhenNoEdits,
            Profiles = Services.CodexConfigService.TryGetProfiles(),
            SelectedProfile = baseSettings.SelectedProfile,
            UseWsl = baseSettings.UseWsl,
            CanUseWsl = OperatingSystem.IsWindows()
        };
        dialog.DataContext = vm;
        var result = await dialog.ShowDialog<CliSettings?>(window);
        if (result is null) return null;

        var updated = CloneSettings(baseSettings);
        updated.Command = result.Command;
        updated.VerboseLoggingEnabled = result.VerboseLoggingEnabled;
        updated.McpEnabled = result.McpEnabled;
        updated.UseApiKey = result.UseApiKey;
        updated.ApiKey = result.ApiKey ?? string.Empty;
        updated.AllowNetworkAccess = result.AllowNetworkAccess;
        updated.ShowMcpResultsInLog = result.ShowMcpResultsInLog;
        updated.ShowMcpResultsOnlyWhenNoEdits = result.ShowMcpResultsOnlyWhenNoEdits;
        updated.SelectedProfile = result.SelectedProfile ?? string.Empty;
        updated.UseWsl = OperatingSystem.IsWindows() && result.UseWsl;

        ApplyAppSettingsInternal(updated, persist: true, logPrefix: "System: CLI settings updated: ", logSuffix: " (--proto enabled)");
        return CloneSettings(_settings);
    }

    private void OnOpenMcpConfigClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var path = Services.McpConfigService.GetConfigPath();
            Services.McpConfigService.EnsureConfigExists();
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", path);
            else
                Process.Start("xdg-open", path);
        }
        catch (Exception ex)
        {
            AppendCliLog("System: Failed to open config: " + ex.Message);
        }
    }

    private void OnMcpRefreshConfigClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            LoadMcpServersFromConfig();
            AppendCliLogVerbose("System: MCP servers reloaded from config.");
        }
        catch (Exception ex)
        {
            AppendCliLog("System: Failed to reload servers: " + ex.Message);
        }
    }

    // Run App button
    private async void OnRunAppClick(object? sender, RoutedEventArgs e)
    {
        if (!HasWorkspace || string.IsNullOrWhiteSpace(CurrentWorkspacePath) || !Directory.Exists(CurrentWorkspacePath))
        {
            AppendCliLog("System: No workspace selected.");
            return;
        }

        AppendCliLog("System: Scanning workspace for runnable targets…");
        List<RunCandidate> candidates;
        try
        {
            candidates = await Task.Run(() => DetectRunCandidates(CurrentWorkspacePath!));
        }
        catch (Exception ex)
        {
            AppendCliLog("System: Scan failed: " + ex.Message);
            return;
        }

        if (candidates.Count == 0)
        {
            AppendCliLog("System: No runnable targets found.");
            return;
        }

        RunCandidate chosen;
        if (candidates.Count == 1)
        {
            chosen = candidates[0];
        }
        else
        {
            try
            {
                var window = GetHostWindow();
                if (window is null)
                    return;
                var dialog = new SelectOptionDialog();
                dialog.Title = "Run App";
                dialog.Prompt = "Select what to run:";
                dialog.Options = candidates.Select(c => c.Label).ToList();
                var index = await dialog.ShowDialog<int?>(window);
                if (!index.HasValue || index.Value < 0 || index.Value >= candidates.Count)
                {
                    AppendCliLog("System: Run canceled.");
                    return;
                }
                chosen = candidates[index.Value];
            }
            catch (Exception ex)
            {
                AppendCliLog("System: Selection failed: " + ex.Message);
                return;
            }
        }

        await LaunchRunCandidateAsync(chosen);
    }

    private sealed class RunCandidate
    {
        public required string Label { get; init; }
        public required string WorkingDir { get; init; }
        public string? FileName { get; init; }
        public string? Arguments { get; init; }
        public bool OpenInBrowser { get; init; }
        public string? BrowserPath { get; init; }
        // Optional build steps to run before launching
        public List<ProcSpec> Pre { get; init; } = new();
        // Optional: resolve a runnable jar after pre-steps
        public string? ResolveJarFromDir { get; init; }
        public string? ResolveJarSearchPattern { get; init; }
    }

    private sealed class ProcSpec
    {
        public required string WorkingDir { get; init; }
        public required string FileName { get; init; }
        public string? Arguments { get; init; }
        public string? Heading { get; init; }
    }

    private async Task LaunchRunCandidateAsync(RunCandidate c)
    {
        try
        {
            // Pre-build steps
            foreach (var step in c.Pre)
            {
                var stepHeading = step.Heading ?? (step.FileName + (string.IsNullOrWhiteSpace(step.Arguments) ? string.Empty : (" " + step.Arguments)));
                var ok = await RunProcessAndLogAsync(step.WorkingDir, step.FileName, step.Arguments ?? string.Empty, stepHeading, stopOnError: true);
                if (!ok)
                {
                    AppendCliLog("System: Build step failed. Aborting run.");
                    return;
                }
            }

            if (c.OpenInBrowser && !string.IsNullOrWhiteSpace(c.BrowserPath))
            {
                var path = c.BrowserPath!;
                if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
                AppendCliLog("Run: open " + MakeRel(path));
                TryOpenUrl(path);
                return;
            }

            // Handle Java jar resolution if needed
            if (string.IsNullOrWhiteSpace(c.FileName))
            {
                if (!string.IsNullOrWhiteSpace(c.ResolveJarFromDir))
                {
                    var dir = c.ResolveJarFromDir!;
                    var pattern = string.IsNullOrWhiteSpace(c.ResolveJarSearchPattern) ? "*.jar" : c.ResolveJarSearchPattern!;
                    string? jar = TryResolveJar(dir, pattern);
                    if (jar == null)
                    {
                        AppendCliLog("System: Could not find runnable jar after build.");
                        return;
                    }
                    var headingJar = $"java -jar \"{jar}\"";
                    await RunProcessAndLogAsync(Path.GetDirectoryName(jar) ?? c.WorkingDir, GetJavaCommand(), $"-jar \"{jar}\"", headingJar);
                    return;
                }
                else
                {
                    AppendCliLog("System: Invalid run target.");
                    return;
                }
            }

            var file = c.FileName!;
            var args = c.Arguments ?? string.Empty;
            var heading = file + (string.IsNullOrWhiteSpace(args) ? string.Empty : (" " + args));
            await RunProcessAndLogAsync(c.WorkingDir, file, args, heading);
        }
        catch (Exception ex)
        {
            AppendCliLog("System: Run failed: " + ex.Message);
        }
    }

    private async Task<bool> RunProcessAndLogAsync(string workingDir, string fileName, string arguments, string heading, bool stopOnError = false)
    {
        if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
        AppendCliLog("Run:");
        AppendCliLog($"{workingDir}$ {heading}");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, ev) => { if (!string.IsNullOrEmpty(ev.Data)) AppendCliInline(ev.Data + Environment.NewLine); };
            p.ErrorDataReceived += (_, ev) => { if (!string.IsNullOrEmpty(ev.Data)) AppendCliInline(ev.Data + Environment.NewLine); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            AppendCliLog($"System: (run exit {p.ExitCode})");
            if (stopOnError && p.ExitCode != 0) return false;
            return true;
        }
        catch (Exception ex)
        {
            AppendCliLog("System: Run error: " + ex.Message);
            return false;
        }
    }

    private string MakeRel(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(CurrentWorkspacePath)) return path;
            var rel = Path.GetRelativePath(CurrentWorkspacePath!, path);
            return string.IsNullOrWhiteSpace(rel) ? path : rel;
        }
        catch { return path; }
    }

    private static bool ShouldSkipDir(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var n = name.ToLowerInvariant();
        return n is ".git" or "bin" or "obj" or "node_modules" or "target" or "dist" or "build" or "out" or "logs" or ".idea" or ".vs";
    }

    private List<RunCandidate> DetectRunCandidates(string root)
    {
        var results = new List<RunCandidate>();
        var maxPerType = 6;
        var seenCsproj = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        // Dotnet solutions (*.sln) — extract projects and add candidates
        var slns = new List<string>();
        Recurse(root, 0, 5, d =>
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(d, "*.sln", SearchOption.TopDirectoryOnly))
                {
                    slns.Add(f);
                    if (slns.Count >= maxPerType) break;
                }
            }
            catch { }
        });
        foreach (var sln in slns)
        {
            var slnName = Path.GetFileNameWithoutExtension(sln);
            foreach (var proj in ExtractCsprojFromSln(sln))
            {
                var abs = Path.GetFullPath(proj);
                if (seenCsproj.Add(abs))
                {
                    results.Add(new RunCandidate
                    {
                        Label = $"dotnet run — {MakeRel(abs)} (sln: {slnName})",
                        WorkingDir = Path.GetDirectoryName(abs) ?? root,
                        FileName = "dotnet",
                        Arguments = $"run --project \"{abs}\"",
                        Pre = new List<ProcSpec>
                        {
                            new ProcSpec { WorkingDir = Path.GetDirectoryName(abs) ?? root, FileName = "dotnet", Arguments = $"build \"{abs}\"", Heading = $"dotnet build \"{MakeRel(abs)}\"" }
                        },
                        OpenInBrowser = false
                    });
                }
            }
        }

        // Dotnet projects (*.csproj)
        var csprojs = new List<string>();
        Recurse(root, 0, 5, d =>
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(d, "*.csproj", SearchOption.TopDirectoryOnly))
                {
                    var abs = Path.GetFullPath(f);
                    if (seenCsproj.Add(abs))
                    {
                        csprojs.Add(abs);
                        if (csprojs.Count >= maxPerType) break;
                    }
                }
            }
            catch { }
        });
        foreach (var proj in csprojs)
        {
            results.Add(new RunCandidate
            {
                Label = $"dotnet run — {MakeRel(proj)}",
                WorkingDir = Path.GetDirectoryName(proj) ?? root,
                FileName = "dotnet",
                Arguments = $"run --project \"{proj}\"",
                Pre = new List<ProcSpec>
                {
                    new ProcSpec { WorkingDir = Path.GetDirectoryName(proj) ?? root, FileName = "dotnet", Arguments = $"build \"{proj}\"", Heading = $"dotnet build \"{MakeRel(proj)}\"" }
                },
                OpenInBrowser = false
            });
        }

        // Node (package.json with dev/start) — support npm, yarn, pnpm
        var nodeDirs = new List<(string Dir, string Tool, string Script)>();
        Recurse(root, 0, 5, d =>
        {
            try
            {
                var pkg = Path.Combine(d, "package.json");
                if (File.Exists(pkg))
                {
                    try
                    {
                        var txt = File.ReadAllText(pkg);
                        var obj = JObject.Parse(txt);
                        var scripts = obj["scripts"] as JObject;
                        if (scripts != null)
                        {
                            var script = scripts["dev"]?.ToString();
                            if (string.IsNullOrWhiteSpace(script)) script = scripts["start"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(script))
                            {
                                string tool = DetectNodeTool(d);
                                nodeDirs.Add((d, tool, scripts["dev"] != null ? "dev" : "start"));
                                if (nodeDirs.Count >= maxPerType) return; // cap
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        });
        foreach (var (dir, tool, script) in nodeDirs)
        {
            results.Add(new RunCandidate
            {
                Label = tool switch
                {
                    "yarn" => $"yarn {script} — {MakeRel(dir)}",
                    "pnpm" => $"pnpm {script} — {MakeRel(dir)}",
                    _ => $"npm run {script} — {MakeRel(dir)}"
                },
                WorkingDir = dir,
                FileName = tool,
                Arguments = tool switch
                {
                    "yarn" => script,
                    "pnpm" => script,
                    _ => (script == "dev" ? "run dev" : "start")
                },
                Pre = HasNodeBuildScript(dir) ? new List<ProcSpec>
                {
                    new ProcSpec { WorkingDir = dir, FileName = tool, Arguments = tool switch { "yarn" => "build", "pnpm" => "build", _ => "run build" }, Heading = tool switch { "yarn" => "yarn build", "pnpm" => "pnpm build", _ => "npm run build" } }
                } : new List<ProcSpec>(),
                OpenInBrowser = false
            });
        }

        // Rust (Cargo.toml)
        var cargoDirs = new List<string>();
        Recurse(root, 0, 5, d =>
        {
            try
            {
                var cargo = Path.Combine(d, "Cargo.toml");
                if (File.Exists(cargo))
                {
                    cargoDirs.Add(d);
                    if (cargoDirs.Count >= maxPerType) return;
                }
            }
            catch { }
        });
        foreach (var dir in cargoDirs)
        {
            results.Add(new RunCandidate
            {
                Label = $"cargo run — {MakeRel(dir)}",
                WorkingDir = dir,
                FileName = "cargo",
                Arguments = "run",
                Pre = new List<ProcSpec> { new ProcSpec { WorkingDir = dir, FileName = "cargo", Arguments = "build", Heading = "cargo build" } },
                OpenInBrowser = false
            });
        }

        // Python (main.py or app.py) — cross-platform python selector
        var pyFiles = new List<string>();
        Recurse(root, 0, 4, d =>
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(d, "*.py", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(f).ToLowerInvariant();
                    if (name is "main.py" or "app.py")
                    {
                        pyFiles.Add(f);
                        if (pyFiles.Count >= maxPerType) break;
                    }
                }
            }
            catch { }
        });
        foreach (var f in pyFiles)
        {
            var py = GetPythonCommand();
            results.Add(new RunCandidate
            {
                Label = $"{py} {MakeRel(f)}",
                WorkingDir = Path.GetDirectoryName(f) ?? root,
                FileName = py,
                Arguments = $"\"{f}\"",
                OpenInBrowser = false
            });
        }

        // Go (go.mod)
        var goDirs = new List<string>();
        Recurse(root, 0, 4, d =>
        {
            try
            {
                var gomod = Path.Combine(d, "go.mod");
                if (File.Exists(gomod))
                {
                    goDirs.Add(d);
                    if (goDirs.Count >= maxPerType) return;
                }
            }
            catch { }
        });
        foreach (var dir in goDirs)
        {
            results.Add(new RunCandidate
            {
                Label = $"go run . — {MakeRel(dir)}",
                WorkingDir = dir,
                FileName = "go",
                Arguments = "run .",
                Pre = new List<ProcSpec> { new ProcSpec { WorkingDir = dir, FileName = "go", Arguments = "build", Heading = "go build" } },
                OpenInBrowser = false
            });
        }

        // HTML (index.html)
        var htmlFiles = new List<string>();
        Recurse(root, 0, 3, d =>
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(d, "index.html", SearchOption.TopDirectoryOnly))
                {
                    htmlFiles.Add(f);
                    if (htmlFiles.Count >= maxPerType) break;
                }
            }
            catch { }
        });
        foreach (var f in htmlFiles)
        {
            results.Add(new RunCandidate
            {
                Label = $"Open {MakeRel(f)} in browser",
                WorkingDir = Path.GetDirectoryName(f) ?? root,
                OpenInBrowser = true,
                BrowserPath = f
            });
        }

        // Java: Maven (pom.xml)
        var mavenDirs = new List<string>();
        Recurse(root, 0, 5, d =>
        {
            try
            {
                var pom = Path.Combine(d, "pom.xml");
                if (File.Exists(pom))
                {
                    mavenDirs.Add(d);
                    if (mavenDirs.Count >= maxPerType) return;
                }
            }
            catch { }
        });
        foreach (var dir in mavenDirs)
        {
            var mvn = DetectMavenTool(dir);
            results.Add(new RunCandidate
            {
                Label = $"maven package + java -jar — {MakeRel(dir)}",
                WorkingDir = dir,
                FileName = null,
                Arguments = null,
                Pre = new List<ProcSpec> { new ProcSpec { WorkingDir = dir, FileName = mvn, Arguments = "package", Heading = $"{mvn} package" } },
                ResolveJarFromDir = Path.Combine(dir, "target"),
                ResolveJarSearchPattern = "*.jar"
            });
        }

        // Java: Gradle (build.gradle / build.gradle.kts)
        var gradleDirs = new List<string>();
        Recurse(root, 0, 5, d =>
        {
            try
            {
                if (File.Exists(Path.Combine(d, "build.gradle")) || File.Exists(Path.Combine(d, "build.gradle.kts")) || File.Exists(Path.Combine(d, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew")))
                {
                    gradleDirs.Add(d);
                    if (gradleDirs.Count >= maxPerType) return;
                }
            }
            catch { }
        });
        foreach (var dir in gradleDirs)
        {
            var gradle = DetectGradleTool(dir);
            results.Add(new RunCandidate
            {
                Label = $"{gradle} build + run — {MakeRel(dir)}",
                WorkingDir = dir,
                FileName = gradle,
                Arguments = "run",
                Pre = new List<ProcSpec> { new ProcSpec { WorkingDir = dir, FileName = gradle, Arguments = "build", Heading = $"{gradle} build" } },
                OpenInBrowser = false
            });
        }

        // Prefer order: npm -> dotnet -> cargo -> python -> go -> html
        return results
            .OrderByDescending(c => c.Label.StartsWith("npm ") || c.Label.StartsWith("yarn ") || c.Label.StartsWith("pnpm "))
            .ThenByDescending(c => c.Label.StartsWith("dotnet "))
            .ThenByDescending(c => c.Label.StartsWith("cargo "))
            .ThenByDescending(c => c.Label.StartsWith("python") || c.Label.StartsWith("python3"))
            .ThenByDescending(c => c.Label.StartsWith("go "))
            .ThenBy(c => c.Label)
            .ToList();
    }

    private static string DetectMavenTool(string dir)
    {
        try
        {
            var mvnw = Path.Combine(dir, OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw");
            if (File.Exists(mvnw)) return mvnw;
        }
        catch { }
        return "mvn";
    }

    private static string DetectGradleTool(string dir)
    {
        try
        {
            var wrapper = Path.Combine(dir, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew");
            if (File.Exists(wrapper)) return wrapper;
        }
        catch { }
        return "gradle";
    }

    private static string GetJavaCommand() => "java";

    private string? TryResolveJar(string dir, string pattern)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var files = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly)
                .Where(f => !f.EndsWith("-sources.jar", StringComparison.OrdinalIgnoreCase) && !f.EndsWith("-javadoc.jar", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (files.Count == 0) return null;
            if (files.Count == 1) return files[0];
            // Prefer fat jars (shadow/uber)
            var preferred = files.FirstOrDefault(f => f.Contains("-all") || f.Contains("-uber") || f.Contains("-shadow")) ?? files[0];
            // Ask user if multiple and not obvious
            if (preferred != files[0]) return preferred;
            var dialog = new SelectOptionDialog
            {
                Title = "Select Jar to Run",
                Prompt = "Multiple jars found. Choose one to run:"
            };
            dialog.Options = files.Select(MakeRel).ToList();
            var window = GetHostWindow();
            if (window is null) return null;
            var idx = dialog.ShowDialog<int?>(window).GetAwaiter().GetResult();
            if (!idx.HasValue || idx.Value < 0 || idx.Value >= files.Count) return null;
            return files[idx.Value];
        }
        catch { return null; }
    }

    private static string DetectNodeTool(string dir)
    {
        try
        {
            var yarnLock = Path.Combine(dir, "yarn.lock");
            if (File.Exists(yarnLock)) return "yarn";
            var pnpmLock = Path.Combine(dir, "pnpm-lock.yaml");
            if (File.Exists(pnpmLock)) return "pnpm";
        }
        catch { }
        return "npm";
    }

    private static string GetPythonCommand()
    {
        if (OperatingSystem.IsWindows()) return "python";
        // On macOS/Linux, prefer python3; if it fails at runtime, the user will see the error in the log.
        return "python3";
    }

    private static bool HasNodeBuildScript(string dir)
    {
        try
        {
            var pkg = Path.Combine(dir, "package.json");
            if (!File.Exists(pkg)) return false;
            var txt = File.ReadAllText(pkg);
            var obj = JObject.Parse(txt);
            var scripts = obj["scripts"] as JObject;
            var build = scripts? ["build"]?.ToString();
            return !string.IsNullOrWhiteSpace(build);
        }
        catch { return false; }
    }

    private static IEnumerable<string> ExtractCsprojFromSln(string slnPath)
    {
        var list = new List<string>();
        try
        {
            var root = Path.GetDirectoryName(slnPath) ?? Directory.GetCurrentDirectory();
            var lines = File.ReadAllLines(slnPath);
            foreach (var line in lines)
            {
                // Project("{GUID}") = "Name", "relative/path.csproj", "{GUID}"
                var idx = line.IndexOf(".csproj\"");
                if (idx < 0) continue;
                // backtrack to first quote before the path
                int q = line.LastIndexOf('"', idx);
                if (q < 0) continue;
                int q2 = line.IndexOf('"', q + 1);
                if (q2 <= q) continue;
                var rel = line.Substring(q + 1, q2 - q - 1);
                if (!rel.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) continue;
                var abs = Path.GetFullPath(Path.Combine(root, rel.Replace('\\', Path.DirectorySeparatorChar)));
                if (File.Exists(abs)) list.Add(abs);
            }
        }
        catch { }
        return list;
    }

    private void Recurse(string dir, int depth, int maxDepth, Action<string> atDir)
    {
        if (depth > maxDepth) return;
        try { atDir(dir); } catch { }
        if (depth == maxDepth) return;
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                try
                {
                    if (ShouldSkipDir(Path.GetFileName(sub))) continue;
                    Recurse(sub, depth + 1, maxDepth, atDir);
                }
                catch { }
            }
        }
        catch { }
    }

    // Shell flyout handlers
    private void OnOpenShellClick(object? sender, RoutedEventArgs e)
    {
        IsShellPanelOpen = true;
        UpdateShellPanelPosition();
        // focus the input shortly after showing
        Dispatcher.UIThread.Post(() =>
        {
            try { this.FindControl<TextBox>("ShellInputTextBox")?.Focus(); } catch { }
        });
    }
    private async void OnRunShellClick(object? sender, RoutedEventArgs e)
    {
        var tb = this.FindControl<TextBox>("ShellInputTextBox");
        var cmd = tb?.Text ?? string.Empty;
        var trimmed = cmd.Trim();
        if (string.Equals(trimmed, "exit", StringComparison.OrdinalIgnoreCase))
        {
            IsShellPanelOpen = false;
            try { tb!.Text = string.Empty; } catch { }
            return;
        }
        if (TryHandleShellBuiltins(cmd))
        {
            try { tb!.Text = string.Empty; } catch { }
            return;
        }
        if (string.IsNullOrWhiteSpace(cmd)) return;
        AddShellHistory(cmd);
        _shellHistoryIndex = -1;
        await RunShellCommandAsync(cmd);
        try { tb!.Text = string.Empty; } catch { }
    }

    // MCP panel handlers
    // MCP mux UI removed

    private void OnCloseShellClick(object? sender, RoutedEventArgs e)
    {
        IsShellPanelOpen = false;
    }

    private async void OnShellInputKeyDown(object? sender, KeyEventArgs e)
    {
        var tb = sender as TextBox;
        if (e.Key == Key.Up)
        {
            e.Handled = true;
            NavigateShellHistory(tb, -1);
            return;
        }
        if (e.Key == Key.Down)
        {
            e.Handled = true;
            NavigateShellHistory(tb, +1);
            return;
        }
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            var cmd = tb?.Text ?? string.Empty;
            var trimmed = cmd.Trim();
            if (string.Equals(trimmed, "exit", StringComparison.OrdinalIgnoreCase))
            {
                IsShellPanelOpen = false;
                try { if (tb != null) tb.Text = string.Empty; } catch { }
                return;
            }
            if (TryHandleShellBuiltins(cmd))
            {
                try { if (tb != null) tb.Text = string.Empty; } catch { }
                return;
            }
            if (string.IsNullOrWhiteSpace(cmd)) return;
            AddShellHistory(cmd);
            _shellHistoryIndex = -1;
            await RunShellCommandAsync(cmd);
            try { tb!.Text = string.Empty; } catch { }
        }
    }

    private async Task RunShellCommandAsync(string command)
    {
        var cwd = _shellCwd ?? CurrentWorkspacePath;
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
        {
            AppendCliLog("System: No workspace selected; cannot run shell command.");
            return;
        }

        if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
        AppendCliLog("Shell:");
        AppendCliLog($"{cwd}$ {command}");

        try
        {
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (OperatingSystem.IsWindows())
            {
                // Use PowerShell on Windows
                psi.FileName = "powershell"; // falls back to Windows PowerShell if pwsh not present
                var psCmd = command.Replace("`", "``").Replace("\"", "`\"");
                // -Command "& { <cmd> }" — allows arbitrary pipeline/script syntax
                psi.Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"& {{ {psCmd} }}\"";
            }
            else if (OperatingSystem.IsMacOS())
            {
                // Use zsh on macOS (default login shell)
                psi.FileName = "/bin/zsh";
                psi.Arguments = "-lc \"" + command.Replace("\"", "\\\"") + "\"";
            }
            else
            {
                // Linux: use bash
                psi.FileName = "/bin/bash";
                psi.Arguments = "-lc \"" + command.Replace("\"", "\\\"") + "\"";
            }

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, ev) => { if (!string.IsNullOrEmpty(ev.Data)) AppendCliInline(ev.Data + Environment.NewLine); };
            p.ErrorDataReceived += (_, ev) => { if (!string.IsNullOrEmpty(ev.Data)) AppendCliInline(ev.Data + Environment.NewLine); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            AppendCliLog($"System: (shell exit {p.ExitCode})");
        }
        catch (Exception ex)
        {
            AppendCliLog("System: Shell error: " + ex.Message);
        }
    }

    private bool _isShellPanelOpen;
    public bool IsShellPanelOpen
    {
        get => _isShellPanelOpen;
        set
        {
            if (_isShellPanelOpen == value) return;
            _isShellPanelOpen = value;
            OnPropertyChanged();
            if (value) UpdateShellPanelPosition();
        }
    }

    private string? _shellCwd;
    private string? _shellPrevCwd;

    private bool TryHandleShellBuiltins(string command)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            var trimmed = command.Trim();
            if (!trimmed.StartsWith("cd", StringComparison.OrdinalIgnoreCase)) return false;

            // Echo the command like a real shell
            var cwdEcho = _shellCwd ?? CurrentWorkspacePath ?? string.Empty;
            if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
            AppendCliLog("Shell:");
            AppendCliLog($"{cwdEcho}$ {command}");

            // Parse path argument
            var rest = trimmed.Length > 2 ? trimmed.Substring(2) : string.Empty; // after 'cd'
            var arg = rest.Trim();
            string target;
            if (string.IsNullOrEmpty(arg))
            {
                target = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            else if (arg == "-")
            {
                // Toggle to previous directory
                if (string.IsNullOrEmpty(_shellPrevCwd))
                {
                    AppendCliLog("System: cd: OLDPWD not set");
                    return true;
                }
                target = _shellPrevCwd!;
            }
            else
            {
                // Strip surrounding quotes
                if ((arg.StartsWith("\"") && arg.EndsWith("\"")) || (arg.StartsWith("'") && arg.EndsWith("'")))
                    arg = arg.Substring(1, arg.Length - 2);

                // Expand ~ and environment variables
                if (arg.StartsWith("~"))
                {
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    arg = Path.Combine(home, arg.Substring(1).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }
                arg = Environment.ExpandEnvironmentVariables(arg);

                // Resolve relative to current shell cwd
                var baseDir = _shellCwd ?? CurrentWorkspacePath ?? Directory.GetCurrentDirectory();
                target = Path.IsPathRooted(arg) ? arg : Path.GetFullPath(Path.Combine(baseDir, arg));
            }

            if (Directory.Exists(target))
            {
                // Update previous and current cwd, enabling 'cd -'
                var old = _shellCwd ?? CurrentWorkspacePath ?? Directory.GetCurrentDirectory();
                _shellPrevCwd = old;
                _shellCwd = Path.GetFullPath(target);
                OnPropertyChanged(nameof(ShellPromptPath));
                AppendCliLog($"System: (cd) now in '{_shellCwd}'");
            }
            else
            {
                AppendCliLog($"System: cd: no such directory: {target}");
            }

            return true;
        }
        catch (Exception ex)
        {
            AppendCliLog("System: cd error: " + ex.Message);
            return true; // considered handled
        }
    }
    // Shell command history (session-scoped)
    private readonly System.Collections.Generic.List<string> _shellHistory = new();
    private int _shellHistoryIndex = -1; // -1 means not navigating; otherwise index into _shellHistory

    private void AddShellHistory(string cmd)
    {
        try
        {
            cmd = cmd.Trim();
            if (string.IsNullOrEmpty(cmd)) return;
            // Avoid duplicate consecutive entries
            if (_shellHistory.Count == 0 || !string.Equals(_shellHistory[^1], cmd, StringComparison.Ordinal))
            {
                _shellHistory.Add(cmd);
                // Optional cap to avoid unbounded growth
                const int Max = 200;
                if (_shellHistory.Count > Max)
                {
                    _shellHistory.RemoveRange(0, _shellHistory.Count - Max);
                }
            }
        }
        catch { }
    }

    private void NavigateShellHistory(TextBox? tb, int direction)
    {
        if (tb is null) return;
        if (_shellHistory.Count == 0) return;

        if (direction < 0)
        {
            if (_shellHistoryIndex == -1) _shellHistoryIndex = _shellHistory.Count - 1;
            else if (_shellHistoryIndex > 0) _shellHistoryIndex--;
        }
        else if (direction > 0)
        {
            if (_shellHistoryIndex == -1) return;
            if (_shellHistoryIndex < _shellHistory.Count - 1) _shellHistoryIndex++;
            else { _shellHistoryIndex = -1; tb.Text = string.Empty; return; }
        }

        if (_shellHistoryIndex >= 0 && _shellHistoryIndex < _shellHistory.Count)
        {
            tb.Text = _shellHistory[_shellHistoryIndex];
            try { tb.CaretIndex = tb.Text?.Length ?? 0; } catch { }
        }
    }

    private void OnRightPaneSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateShellPanelPosition();
    }

    private void UpdateShellPanelPosition()
    {
        try
        {
            if (!IsShellPanelOpen) return;
            var right = this.FindControl<Grid>("RightPaneGrid");
            var shell = this.FindControl<Border>("ShellPanel");
            if (right == null || shell == null) return;
            const double margin = 10.0;
            // Fit the shell panel to the width of the CLI pane (minus margins)
            var size = right.Bounds.Size;
            double w = Math.Max(0, size.Width - (margin * 2));
            double h = shell.Height; // fixed height
            shell.Width = w;
            double left = margin;
            double top = Math.Max(0, size.Height - h - margin);
            Canvas.SetLeft(shell, left);
            Canvas.SetTop(shell, top);
        }
        catch { }
    }

    private async void OnSendCliInputClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await SendCliInputAsync();

    private async void OnCliInputKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter/Return sends; Shift+Enter inserts newline (AcceptsReturn=true)
        bool isEnter = e.Key == Key.Enter || e.Key == Key.Return;
        if (isEnter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            // Ensure we capture the latest text from the TextBox even if binding hasn't updated yet
            if (sender is TextBox tb)
            {
                CliInput = tb.Text ?? string.Empty;
            }
            await SendCliInputAsync();
        }
    }

    private async void OnGitCommitClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!HasWorkspace || CurrentWorkspacePath is null || !IsGitRepo) return;
        var window = GetHostWindow();
        if (window is null)
            return;
        var dlg = new InputDialog
        {
            Title = "Commit Changes"
        };
        dlg.Prompt = "Commit message:";
        dlg.Input = string.Empty;
        dlg.ShowCreatePullRequestOption = true;
        var result = await dlg.ShowDialog<InputDialogResult?>(window);
        if (result is null) return;

        var message = result.Text;
        var (ok, output, error) = GitHelper.TryCommitAll(CurrentWorkspacePath, message);
        if (ok)
        {
            AppendCliLog(output ?? "Committed.");
            LoadTree(CurrentWorkspacePath);

            var (pushOk, pushOutput, pushError) = GitHelper.TryPushCurrentBranch(CurrentWorkspacePath);
            if (pushOk)
            {
                if (!string.IsNullOrWhiteSpace(pushOutput)) AppendCliLog(pushOutput);
                else AppendCliLog("Pushed changes to remote.");

                if (result.CreatePullRequest)
                {
                    TryLaunchCreatePullRequest();
                }
            }
            else
            {
                AppendCliLog("Git push failed: " + (pushError ?? "unknown error"));
                if (result.CreatePullRequest)
                {
                    AppendCliLog("System: Skipping pull request shortcut because push did not succeed.");
                }
            }
        }
        else
        {
            AppendCliLog("Git commit failed: " + (error ?? "unknown error"));
        }
    }

    private void TryLaunchCreatePullRequest()
    {
        if (!HasWorkspace || CurrentWorkspacePath is null) return;
        var (ok, url, error) = GitHelper.TryBuildPullRequestUrl(CurrentWorkspacePath);
        if (ok && !string.IsNullOrWhiteSpace(url))
        {
            AppendCliLog("System: Opening browser to create pull request…");
            TryOpenUrl(url!);
        }
        else
        {
            AppendCliLog("System: Unable to prepare pull request: " + (error ?? "unknown error"));
        }
    }

    private async void OnGitNewBranchClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!HasWorkspace || CurrentWorkspacePath is null || !IsGitRepo) return;
        var window = GetHostWindow();
        if (window is null)
            return;
        var dlg = new InputDialog
        {
            Title = "Create New Branch"
        };
        dlg.Prompt = "Branch name:";
        dlg.Input = string.Empty;
        var result = await dlg.ShowDialog<InputDialogResult?>(window);
        var name = result?.Text;
        if (name is null) return;
        // Create branch based on default branch (main/master) and fetch origin first
        var (ok, output, error) = GitHelper.TryCreateAndCheckoutBranch(CurrentWorkspacePath, name, baseOnDefaultBranch: true, fetchOriginFirst: true);
        if (ok)
        {
            AppendCliLog(output ?? $"Checked out '{name}'.");
            RefreshGitUi();
            LoadTree(CurrentWorkspacePath);
        }
        else
        {
            AppendCliLog("Git branch failed: " + (error ?? "unknown error"));
        }
    }

    private async void OnGitGetLatestClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!HasWorkspace || CurrentWorkspacePath is null || !IsGitRepo) return;
        AppendCliLog("System: Fetching latest changes…");
        var (ok, output, error) = await Task.Run(() => GitHelper.TryFetchAndFastForward(CurrentWorkspacePath));
        if (ok)
        {
            AppendCliLog(output ?? "Already up to date.");
            RefreshGitUi();
            LoadTree(CurrentWorkspacePath);
        }
        else
        {
            AppendCliLog("Git update failed: " + (error ?? "unknown error"));
        }
    }

    private void OnGitRefreshClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RefreshGitUi();
        if (HasWorkspace && CurrentWorkspacePath is not null)
            LoadTree(CurrentWorkspacePath);
    }

    private async void OnGitSwitchBranchClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!HasWorkspace || CurrentWorkspacePath is null || !IsGitRepo) return;
        var window = GetHostWindow();
        if (window is null)
            return;
        var dlg = new InputDialog { Title = "Switch Branch" };
        dlg.Prompt = "Branch name:";
        dlg.Input = CurrentBranch;
        var result = await dlg.ShowDialog<InputDialogResult?>(window);
        var name = result?.Text;
        if (string.IsNullOrWhiteSpace(name)) return;
        var (ok, output, error) = GitHelper.TryCheckoutBranch(CurrentWorkspacePath, name);
        if (ok)
        {
            AppendCliLog(output ?? $"Checked out '{name}'.");
            RefreshGitUi();
            LoadTree(CurrentWorkspacePath);
        }
        else
        {
            AppendCliLog("Git checkout failed: " + (error ?? "unknown error"));
        }
    }

    private async void OnGitRollbackClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!HasWorkspace || CurrentWorkspacePath is null || !IsGitRepo) return;
        var window = GetHostWindow();
        if (window is null)
            return;
        var confirm = new ConfirmDialog
        {
            Title = "Rollback Changes",
            Message = "This will discard ALL local changes and delete untracked files to match HEAD.\nThis action cannot be undone. Continue?"
        };
        var ok = await confirm.ShowDialog<bool>(window);
        if (!ok) return;

        AppendCliLog("Rolling back working directory…");
        var (rok, output, error) = GitHelper.TryRollbackAllChanges(CurrentWorkspacePath);
        if (rok)
        {
            AppendCliLog(output ?? "Rollback complete.");
            RefreshGitUi();
            if (HasWorkspace && CurrentWorkspacePath is not null)
                LoadTree(CurrentWorkspacePath);
        }
        else
        {
            AppendCliLog("Git rollback failed: " + (error ?? "unknown error"));
        }
    }

    private async void OnGitInitClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!HasWorkspace || CurrentWorkspacePath is null) return;
        var available = await GitHelper.IsGitAvailableAsync();
        if (!available)
        {
            AppendCliLog("Git library unavailable. Cannot initialize repository.");
            return;
        }
        var confirm = new ConfirmDialog
        {
            Title = "Initialize Git",
            Message = "Initialize a new Git repository in this folder?"
        };
        var window = GetHostWindow();
        if (window is null)
            return;
        var ok = await confirm.ShowDialog<bool>(window);
        if (!ok) return;
        AppendCliLog($"Initializing git in {CurrentWorkspacePath}…");
        var result = await GitHelper.InitializeRepositoryAsync(CurrentWorkspacePath);
        if (result.Success)
        {
            AppendCliLog(result.Output.Trim());
        }
        else
        {
            AppendCliLog("Git init failed: " + result.Error.Trim());
        }
        RefreshGitUi();
        LoadTree(CurrentWorkspacePath);
    }

    private async Task SendCliInputAsync()
    {
        if (!_cli.IsRunning || string.IsNullOrWhiteSpace(CliInput)) return;
        if (string.IsNullOrWhiteSpace(_conversationId))
        {
            AppendCliLog("System: Conversation not ready yet.");
            return;
        }

        var line = CliInput;
        CliInput = string.Empty;
        _pendingUserInput = line;
        if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
        AppendCliLog("You:");
        AppendCliLog(line);

        try
        {
            var items = new JArray
            {
                new JObject
                {
                    ["type"] = "text",
                    ["data"] = new JObject { ["text"] = line }
                }
            };

            var sandbox = new JObject
            {
                ["mode"] = "workspace-write",
                ["writable_roots"] = new JArray(),
                ["network_access"] = _allowNetworkAccess,
                ["exclude_tmpdir_env_var"] = false,
                ["exclude_slash_tmp"] = false
            };

            var cwd = HasWorkspace && !string.IsNullOrWhiteSpace(CurrentWorkspacePath)
                ? CurrentWorkspacePath!
                : Directory.GetCurrentDirectory();

            var model = string.IsNullOrWhiteSpace(_currentModel) ? "gpt-5-codex" : _currentModel;

            var turnParams = new JObject
            {
                ["conversationId"] = _conversationId,
                ["items"] = items,
                ["cwd"] = cwd,
                ["approvalPolicy"] = "on-request",
                ["sandboxPolicy"] = sandbox,
                ["model"] = model,
                ["effort"] = "medium",
                ["summary"] = "auto"
            };

            AppendCliLog($"System: Turn context • network={( _allowNetworkAccess ? "on" : "off")}");
            await SendRequestAsync("sendUserTurn", turnParams);
        }
        catch (Exception ex)
        {
            AppendCliLog("Failed to send input: " + ex.Message);
        }
    }

    private static string? TryExtractModelFromArgs(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return null;
        try
        {
            var parts = args.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                if (p.StartsWith("model="))
                    return p.Substring("model=".Length);
                if (p.StartsWith("--model="))
                    return p.Substring("--model=".Length);
                if (p == "model" && i + 1 < parts.Length)
                    return parts[i + 1];
            }
        }
        catch { }
        return null;
    }

}
