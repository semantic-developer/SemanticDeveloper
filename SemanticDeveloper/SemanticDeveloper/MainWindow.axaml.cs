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
using SemanticDeveloper.Views;
using AvaloniaEdit; 
using System.Collections.Generic;

namespace SemanticDeveloper;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly CodexCliService _cli = new();
    private ProtoHelper.SubmissionShape _submissionShape = ProtoHelper.SubmissionShape.NestedInternallyTagged;
    private ProtoHelper.ContentStyle _contentStyle = ProtoHelper.ContentStyle.Flattened;
    private string _defaultMsgType = "user_turn";
    private string? _currentModel;
    // Auto-approval UI removed; approvals require manual handling
    private AppSettings _settings = new();

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

    public MainWindow()
    {
        InitializeComponent();

        DataContext = this;

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
        _settings = SettingsService.Load();
        _cli.Command = _settings.Command;
        _cli.UseApiKey = _settings.UseApiKey;
        _cli.ApiKey = string.IsNullOrWhiteSpace(_settings.ApiKey) ? null : _settings.ApiKey;
        AppendCliLog($"System: Loaded CLI settings: '{_cli.Command}'{(string.IsNullOrWhiteSpace(_settings.SelectedProfile)?"":" • profile=" + _settings.SelectedProfile)}");
        SelectedProfile = _settings.SelectedProfile;
        _currentModel = TryExtractModelFromArgs(_cli.AdditionalArgs);
        _verboseLogging = _settings.VerboseLoggingEnabled;
        IsMcpEnabled = _settings.McpEnabled;
        _allowNetworkAccess = _settings.AllowNetworkAccess;
        _showMcpResultsInLog = _settings.ShowMcpResultsInLog;
        _showMcpResultsOnlyWhenNoEdits = _settings.ShowMcpResultsOnlyWhenNoEdits;

        // Load MCP servers list from config for selection before session start
        try { LoadMcpServersFromConfig(); } catch { }

        _cli.OutputReceived += OnCliOutput;
        _cli.Exited += (_, _) =>
        {
            IsCliRunning = false;
            SessionStatus = "stopped";
        };

        // Lazy-load file tree nodes when expanded
        AddHandler(TreeViewItem.ExpandedEvent, OnTreeItemExpanded, RoutingStrategies.Bubble);

        // Ensure editor panel visibility default
        try { RefreshEditorPanelsVisibility(); } catch { }

        // Capture editor controls and attach gutters/highlighting support
        TrySetupEditors();
    }

    private void LoadMcpServersFromConfig()
    {
        var path = Services.McpConfigService.GetConfigPath();
        try
        {
            Services.McpConfigService.EnsureConfigExists();
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path);
            var json = Newtonsoft.Json.Linq.JObject.Parse(text);
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

            if (json["mcpServers"] is Newtonsoft.Json.Linq.JObject map)
            {
                foreach (var p in map.Properties()) AddOrUpdate(p.Name);
            }
            else if (json["servers"] is Newtonsoft.Json.Linq.JArray arr)
            {
                foreach (var s in arr.OfType<Newtonsoft.Json.Linq.JObject>()) AddOrUpdate(s["name"]?.ToString() ?? string.Empty);
            }

            // Remove entries no longer present
            for (int i = McpServers.Count - 1; i >= 0; i--)
            {
                if (!seen.Contains(McpServers[i].Name)) McpServers.RemoveAt(i);
            }
        }
        catch { }
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
        set { if (_mcpEnabled == value) return; _mcpEnabled = value; OnPropertyChanged(); }
    }

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
        // Hide noisy agent logs (unless verbose)
        if (!_verboseLogging && LooksLikeAgentLog(line)) return;
        // Hide verbose function call apply_patch payloads (unless verbose)
        if (!_verboseLogging && IsFunctionCallApplyPatch(line)) { SetStatusSafe("thinking…"); AppendCliLog("System: Applying patch…"); return; }

        // Prefer pretty rendering for protocol JSON events; fallback to raw text
        var handled = TryRenderProtocolEvent(line);
        if (!handled)
        {
            AppendCliLog(line);
        }
        // Auto-approval disabled; do not auto-respond to approval requests
        var l = line.ToLowerInvariant();
        if (l.Contains("expected internally tagged enum op") && _submissionShape == ProtoHelper.SubmissionShape.TopLevelInternallyTagged)
        {
            _submissionShape = ProtoHelper.SubmissionShape.NestedInternallyTagged;
            AppendCliLog("System: Switched submission shape to nested 'op' to satisfy proto parser.");
        }
        if (l.Contains("unknown field `type`") || l.Contains("missing field `type`"))
        {
            _contentStyle = ProtoHelper.ContentStyle.Flattened;
            _defaultMsgType = "user_input";
            AppendCliLog("System: Setting style=flattened and type='user_input'.");
        }
        if (l.Contains("unknown field `msg`") || l.Contains("missing field `msg`"))
        {
            _contentStyle = ProtoHelper.ContentStyle.Flattened;
            AppendCliLog("System: Switching content style to flattened (no 'msg' field).");
        }
        if (l.Contains("missing field `items`"))
        {
            _defaultMsgType = "user_turn";
            AppendCliLog("System: Setting message type to 'user_turn' with items.");
        }

        // Try capture model from session_configured messages
        TryUpdateModelFromJson(line);
    }

    private bool LooksLikeAgentLog(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        if (char.IsDigit(line.FirstOrDefault()) && line.Contains(" codex_core::")) return true;
        return false;
    }

    private bool IsFunctionCallApplyPatch(string line)
        => line.Contains("FunctionCall: shell(") && line.Contains("\"apply_patch\"");

    private bool TryRenderProtocolEvent(string line)
    {
        var json = line.Trim();
        if (!json.StartsWith("{")) return false;
        Newtonsoft.Json.Linq.JObject? root;
        try { root = (Newtonsoft.Json.Linq.JObject?)Newtonsoft.Json.Linq.JToken.Parse(json); }
        catch { return false; }
        if (root is null) return false;

        var msg = root["msg"] as Newtonsoft.Json.Linq.JObject;
        var type = msg? ["type"]?.ToString();
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
                        var inv = msg? ["invocation"] as Newtonsoft.Json.Linq.JObject;
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
                        var inv = msg? ["invocation"] as Newtonsoft.Json.Linq.JObject;
                        var server = inv? ["server"]?.ToString();
                        var tool = inv? ["tool"]?.ToString();
                        // Handle success/error across variants (is_error/isError, Err, error, success=false)
                        var res = msg? ["result"] as Newtonsoft.Json.Linq.JObject;
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
                                errorMsg = e?.Type == Newtonsoft.Json.Linq.JTokenType.String ? e?.ToString() : e?.ToString(Newtonsoft.Json.Formatting.None);
                            }
                            // Common error keys
                            if (!isError && res["error"] != null)
                            {
                                isError = true;
                                var e = res["error"];
                                errorMsg = e?.Type == Newtonsoft.Json.Linq.JTokenType.String ? e?.ToString() : e?.ToString(Newtonsoft.Json.Formatting.None);
                            }
                            // success flag
                            if (!isError && res["success"] != null && bool.TryParse(res["success"]?.ToString(), out var succ))
                                isError = !succ;
                            try
                            {
                                if (res["content"] is Newtonsoft.Json.Linq.JArray arr)
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
                                    if (resultsNode is Newtonsoft.Json.Linq.JArray arr2)
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
                                var resObj = msg? ["result"] as Newtonsoft.Json.Linq.JObject;
                                var sc = (resObj? ["structured_content"]) ?? (resObj? ["structuredContent"]);
                                var results = sc? ["results"] as Newtonsoft.Json.Linq.JArray;
                                if (results != null && results.Count > 0)
                                {
                                    // Local helpers to normalize field names across servers
                                    string? GetTitle(Newtonsoft.Json.Linq.JObject it)
                                    {
                                        return it["title"]?.ToString()
                                               ?? it["Title"]?.ToString()
                                               ?? it["item1"]?.ToString()
                                               ?? it["Item1"]?.ToString();
                                    }
                                    string? GetUrl(Newtonsoft.Json.Linq.JObject it)
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
                                        if (results[i] is Newtonsoft.Json.Linq.JObject item)
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
                                    if (contentTok is Newtonsoft.Json.Linq.JArray contentArr)
                                    {
                                        var textBlock = contentArr.FirstOrDefault(t => (t?["type"]?.ToString() ?? string.Empty).Equals("text", StringComparison.OrdinalIgnoreCase)) as Newtonsoft.Json.Linq.JObject;
                                        text = textBlock? ["text"]?.ToString();
                                    }
                                    else if (contentTok is Newtonsoft.Json.Linq.JValue v && v.Type == Newtonsoft.Json.Linq.JTokenType.String)
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
                        var tools = msg? ["tools"] as Newtonsoft.Json.Linq.JObject;
                        var toolArray = msg? ["tools"] as Newtonsoft.Json.Linq.JArray;
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
                    var cmdArr = msg? ["command"] as Newtonsoft.Json.Linq.JArray;
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
                {
                    try
                    {
                        var eventId = root["id"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(eventId))
                        {
                            var op = lower.StartsWith("exec") ? "exec_approval" : "patch_approval";
                            var approvalJson = Services.ProtoHelper.BuildApproval(op, eventId!, "approved", _submissionShape);
                            _ = _cli.SendAsync(approvalJson);
                            var summary = string.Empty;
                            if (lower.StartsWith("exec"))
                            {
                                var cmdArr = msg? ["command"] as Newtonsoft.Json.Linq.JArray;
                                if (cmdArr != null && cmdArr.Count > 0)
                                {
                                    var tokens = cmdArr.Select(t => t?.ToString() ?? string.Empty).ToList();
                                    if (tokens.Any(t => t.StartsWith("*** Begin Patch")) || tokens.Contains("apply_patch"))
                                        summary = "apply_patch";
                                    else
                                        summary = string.Join(" ", tokens.Take(5));
                                }
                            }
                            if (!string.IsNullOrEmpty(summary))
                            {
                                if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
                                AppendCliLog($"System: Auto-approved {op}: {summary}");
                            }
                        }
                    }
                    catch { }
                    return true;
                }
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

    private void TryUpdateModelFromJson(string line)
    {
        try
        {
            var json = line.Trim();
            if (!json.StartsWith("{")) return;
            var token = Newtonsoft.Json.Linq.JToken.Parse(json);
            if (token is not Newtonsoft.Json.Linq.JObject obj) return;
            var msg = obj["msg"] as Newtonsoft.Json.Linq.JObject;
            if (msg? ["type"]?.ToString() == "session_configured")
            {
                var model = msg["model"]?.ToString();
                if (!string.IsNullOrWhiteSpace(model))
                {
                    _currentModel = model;
                    AppendCliLog($"System: Detected model: {_currentModel}");
                }
            }
        }
        catch { }
    }

    private void UpdateTokenStats(Newtonsoft.Json.Linq.JObject msg)
    {
        var info = msg["info"] as Newtonsoft.Json.Linq.JObject;
        if (info is null) return;
        var total = info["total_token_usage"] as Newtonsoft.Json.Linq.JObject;
        if (total is null) return;

        long input = total.Value<long?>("input_tokens") ?? 0L;
        long cached = total.Value<long?>("cached_input_tokens") ?? 0L;
        long output = total.Value<long?>("output_tokens") ?? 0L;
        long blendedTotal = Math.Max(0, (input - cached)) + output;

        var modelCw = (info["model_context_window"]?.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
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
        var provider = this.StorageProvider;
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
                var confirmed = await confirm.ShowDialog<bool>(this);
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

            var installed = await CodexVersionService.TryGetInstalledVersionAsync(_cli.Command, CurrentWorkspacePath ?? Directory.GetCurrentDirectory());
            if (!installed.Ok || string.IsNullOrWhiteSpace(installed.Version)) return;

            if (CodexVersionService.IsNewer(latest.Version, installed.Version))
            {
                AppendCliLog($"System: Codex {latest.Version} is available (installed {installed.Version}). Run 'codex update' to upgrade.");
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

        var confirm = new Views.ConfirmDialog
        {
            Title = "Delete",
            Message = $"Delete '{targetName}'?\nThis action cannot be undone."
        };
        var result = await confirm.ShowDialog<bool>(this);
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
            // Build profile flag if selected (use config override to avoid unsupported --profile on proto)
            var prevArgs = _cli.AdditionalArgs;
            var effectiveArgs = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(_settings.SelectedProfile))
            {
                effectiveArgs.Append("-c profile=").Append(_settings.SelectedProfile).Append(' ');
                AppendCliLog($"System: Using profile '{_settings.SelectedProfile}'");
            }
            if (IsMcpEnabled)
            {
                try
                {
                    Services.McpConfigService.EnsureConfigExists();
                    var flags = BuildMcpServersFlags();
                    if (!string.IsNullOrWhiteSpace(flags))
                    {
                        effectiveArgs.Append(flags).Append(' ');
                        AppendCliLog("System: Injected MCP servers from config.");
                    }
                    else AppendCliLog("System: No MCP servers configured; skipping.");
                }
                catch (Exception ex)
                {
                    AppendCliLog("System: Failed to prepare MCP flags: " + ex.Message);
                }
            }
            // Apply args composed from profile + MCP flags only (AdditionalArgs deprecated)
            var composed = effectiveArgs.ToString().Trim();
            _cli.AdditionalArgs = composed;

            // Proactive authentication: API key login or interactive chat login
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

            await _cli.StartAsync(CurrentWorkspacePath, CancellationToken.None);
            _cli.AdditionalArgs = prevArgs; // restore original
            IsCliRunning = _cli.IsRunning;
            SessionStatus = IsCliRunning ? "idle" : "stopped";
            // Request MCP tools list once session is up (if enabled)
            if (IsMcpEnabled && IsCliRunning)
            {
                try
                {
                    var msg = Services.ProtoHelper.BuildListMcpTools();
                    await _cli.SendAsync(msg);
                    AppendCliLog("System: Requested MCP tools…");
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            AppendCliLog("System: Failed to start CLI: " + ex.Message);
            SessionStatus = "error";
        }
    }

    private async Task InterruptCliAsync()
    {
        if (!_cli.IsRunning) return;
        try
        {
            var interrupt = Services.ProtoHelper.BuildInterrupt();
            await _cli.SendAsync(interrupt);
            SetStatusSafe("idle");
        }
        catch
        {
            _cli.Stop();
            IsCliRunning = false;
            SessionStatus = "stopped";
        }
    }

    private static string EscapeForCli(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string BuildJsonArray(IEnumerable<string> items)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('[');
        bool first = true;
        foreach (var it in items)
        {
            if (!first) sb.Append(','); first = false;
            var v = it.Replace("\\", "\\\\").Replace("\"", "\\\"");
            sb.Append('"').Append(v).Append('"');
        }
        sb.Append(']');
        return sb.ToString();
    }

    private string BuildMcpServersFlags()
    {
        try
        {
            var path = Services.McpConfigService.GetConfigPath();
            if (!File.Exists(path)) return string.Empty;
            var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));

            // Support new format: { "mcpServers": { "name": { command, args, cwd?, env?, enabled? }, ... } }
            // and legacy format: { "servers": [ { name, command, args, cwd?, env?, enabled? }, ... ] }
            var parts = new List<string>();

            // Selected servers set
            var selected = new HashSet<string>(McpServers.Where(s => s.Selected).Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            bool HasSelection() => selected.Count > 0;

            if (json["mcpServers"] is Newtonsoft.Json.Linq.JObject map)
            {
                foreach (var prop in map.Properties())
                {
                    var name = prop.Name.Trim();
                    if (prop.Value is not Newtonsoft.Json.Linq.JObject s) continue;
                    if (!HasSelection() || selected.Contains(name))
                        AppendServerFlags(parts, name, s);
                }
            }
            else if (json["servers"] is Newtonsoft.Json.Linq.JArray serversArr)
            {
                foreach (var s in serversArr.OfType<Newtonsoft.Json.Linq.JObject>())
                {
                    var name = (s["name"]?.ToString() ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!HasSelection() || selected.Contains(name))
                        AppendServerFlags(parts, name, s);
                }
            }
            if (parts.Count == 0) return string.Empty;
            return string.Join(' ', parts);
        }
        catch { return string.Empty; }
    }

    private static void AppendServerFlags(List<string> parts, string rawName, Newtonsoft.Json.Linq.JObject s)
    {
        try
        {
            var enabledTok = s["enabled"];
            if (enabledTok != null && bool.TryParse(enabledTok.ToString(), out var en) && !en) return;
            if (string.IsNullOrWhiteSpace(rawName)) return;
            var name = new string(rawName.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '-').ToArray());

            // Only stdio/local servers are supported by Codex CLI: require command
            var cmd = (s["command"]?.ToString() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cmd)) return;

            // args can be array or single string
            IEnumerable<string> args = Enumerable.Empty<string>();
            if (s["args"] is Newtonsoft.Json.Linq.JArray arr)
            {
                args = arr.Select(a => a?.ToString() ?? string.Empty);
            }
            else if (s["args"] is Newtonsoft.Json.Linq.JValue val && val.Type == Newtonsoft.Json.Linq.JTokenType.String)
            {
                args = new[] { val.ToString() };
            }

            var cwd = s["cwd"]?.ToString();
            var envObj = s["env"] as Newtonsoft.Json.Linq.JObject;

            parts.Add($"-c mcp_servers.{name}.command={EscapeForCli(cmd)}");
            var argsList = args.ToList();
            if (argsList.Count > 0) parts.Add($"-c mcp_servers.{name}.args={BuildJsonArray(argsList)}");
            if (!string.IsNullOrWhiteSpace(cwd)) parts.Add($"-c mcp_servers.{name}.cwd={EscapeForCli(cwd!)}");
            if (envObj != null)
            {
                foreach (var prop in envObj.Properties())
                {
                    var k = prop.Name;
                    var v = prop.Value?.ToString() ?? string.Empty;
                    parts.Add($"-c mcp_servers.{name}.env.{k}={EscapeForCli(v)}");
                }
            }
        }
        catch { }
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
            var ok = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var dlg = new Views.ConfirmDialog
                {
                    Title = "Login Required",
                    Message = "Received 401 Unauthorized. You are not logged in.\nRun 'codex auth login' to authenticate now?"
                };
                return dlg.ShowDialog<bool>(this);
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
        var cwd = HasWorkspace ? CurrentWorkspacePath : Directory.GetCurrentDirectory();

        async Task<int> RunAsync(string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _cli.Command,
                    Arguments = args,
                    WorkingDirectory = cwd!,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

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
                                var dlg = new Views.InputDialog { Title = "Enter Verification Code" };
                                dlg.Prompt = "Paste the verification code from the browser:";
                                dlg.Input = string.Empty;
                                var result = await dlg.ShowDialog<Views.InputDialogResult?>(this);
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
        var exit = await RunAsync("auth login");
        if (exit == 0) return exit;
        // Fallback: some versions may use `login`
        return await RunAsync("login");
    }

    private async Task<int> RunCodexLoginWithApiKeyAsync(string apiKey)
    {
        var cwd = HasWorkspace ? CurrentWorkspacePath : Directory.GetCurrentDirectory();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _cli.Command,
                WorkingDirectory = cwd!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Prefer: codex login --api-key <key>
            psi.ArgumentList.Add("login");
            psi.ArgumentList.Add("--api-key");
            psi.ArgumentList.Add(apiKey);

            using (var p = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                p.OutputDataReceived += (_, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) AppendCliLog(ev.Data!); };
                p.ErrorDataReceived += (_, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) AppendCliLog(ev.Data!); };
                if (!p.Start()) return -1;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                await p.WaitForExitAsync();
                if (p.ExitCode == 0) return 0;
            }

            // Fallback: codex auth login --api-key <key>
            var psi2 = new ProcessStartInfo
            {
                FileName = _cli.Command,
                WorkingDirectory = cwd!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi2.ArgumentList.Add("auth");
            psi2.ArgumentList.Add("login");
            psi2.ArgumentList.Add("--api-key");
            psi2.ArgumentList.Add(apiKey);

            using var p2 = new Process { StartInfo = psi2, EnableRaisingEvents = true };
            p2.OutputDataReceived += (_, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) AppendCliLog(ev.Data!); };
            p2.ErrorDataReceived += (_, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) AppendCliLog(ev.Data!); };
            if (!p2.Start()) return -1;
            p2.BeginOutputReadLine();
            p2.BeginErrorReadLine();
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

    private async void OnOpenCliSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var dialog = new CliSettingsDialog();
        var vm = new CliSettings
        {
            Command = _cli.Command,
            VerboseLoggingEnabled = _verboseLogging,
            McpEnabled = _settings.McpEnabled,
            UseApiKey = _settings.UseApiKey,
            ApiKey = _settings.ApiKey,
            AllowNetworkAccess = _settings.AllowNetworkAccess,
            ShowMcpResultsInLog = _settings.ShowMcpResultsInLog,
            ShowMcpResultsOnlyWhenNoEdits = _settings.ShowMcpResultsOnlyWhenNoEdits,
            Profiles = Services.CodexConfigService.TryGetProfiles(),
            SelectedProfile = _settings.SelectedProfile
        };
        dialog.DataContext = vm;
        var result = await dialog.ShowDialog<CliSettings?>(this);
        if (result is null) return;
        _cli.Command = result.Command;
        AppendCliLog($"System: CLI settings updated: '{_cli.Command}'{(string.IsNullOrWhiteSpace(result.SelectedProfile)?"":" • profile=" + result.SelectedProfile)} (--proto enabled)");
        // persist settings
        _settings.Command = _cli.Command;
        // AdditionalArgs removed (profiles/config.toml driven)
        _settings.VerboseLoggingEnabled = result.VerboseLoggingEnabled;
        _settings.McpEnabled = result.McpEnabled;
        _settings.UseApiKey = result.UseApiKey;
        _settings.ApiKey = result.ApiKey ?? string.Empty;
        _settings.AllowNetworkAccess = result.AllowNetworkAccess;
        _settings.ShowMcpResultsInLog = result.ShowMcpResultsInLog;
        _settings.ShowMcpResultsOnlyWhenNoEdits = result.ShowMcpResultsOnlyWhenNoEdits;
        _settings.SelectedProfile = result.SelectedProfile ?? string.Empty;
        SelectedProfile = _settings.SelectedProfile;
        _cli.UseApiKey = _settings.UseApiKey;
        _cli.ApiKey = string.IsNullOrWhiteSpace(_settings.ApiKey) ? null : _settings.ApiKey;
        SettingsService.Save(_settings);
        _currentModel = TryExtractModelFromArgs(_cli.AdditionalArgs);
        _verboseLogging = result.VerboseLoggingEnabled;
        IsMcpEnabled = _settings.McpEnabled;
        _allowNetworkAccess = _settings.AllowNetworkAccess;
        _showMcpResultsInLog = _settings.ShowMcpResultsInLog;
        _showMcpResultsOnlyWhenNoEdits = _settings.ShowMcpResultsOnlyWhenNoEdits;
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
            AppendCliLog("System: MCP servers reloaded from config.");
        }
        catch (Exception ex)
        {
            AppendCliLog("System: Failed to reload servers: " + ex.Message);
        }
    }

    private void OnOpenReadmeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[] { "README.md", "README.txt", "README" };
            foreach (var name in candidates)
            {
                var p = System.IO.Path.Combine(baseDir, name);
                if (System.IO.File.Exists(p))
                {
                    if (OperatingSystem.IsWindows())
                        Process.Start(new ProcessStartInfo { FileName = p, UseShellExecute = true });
                    else if (OperatingSystem.IsMacOS())
                        Process.Start("open", p);
                    else
                        Process.Start("xdg-open", p);
                    return;
                }
            }
            AppendCliLog("System: README not found in output directory.");
        }
        catch (Exception ex)
        {
            AppendCliLog("System: Failed to open README: " + ex.Message);
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
                var dialog = new Views.SelectOptionDialog();
                dialog.Title = "Run App";
                dialog.Prompt = "Select what to run:";
                dialog.Options = candidates.Select(c => c.Label).ToList();
                var index = await dialog.ShowDialog<int?>(this);
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
                        var obj = Newtonsoft.Json.Linq.JObject.Parse(txt);
                        var scripts = obj["scripts"] as Newtonsoft.Json.Linq.JObject;
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
            var dialog = new Views.SelectOptionDialog
            {
                Title = "Select Jar to Run",
                Prompt = "Multiple jars found. Choose one to run:"
            };
            dialog.Options = files.Select(MakeRel).ToList();
            var idx = dialog.ShowDialog<int?>(this).GetAwaiter().GetResult();
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
            var obj = Newtonsoft.Json.Linq.JObject.Parse(txt);
            var scripts = obj["scripts"] as Newtonsoft.Json.Linq.JObject;
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

    private async void OnOpenAboutClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var dialog = new Views.AboutDialog();
        await dialog.ShowDialog(this);
    }

    private async void OnGitCommitClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!HasWorkspace || CurrentWorkspacePath is null || !IsGitRepo) return;
        var dlg = new Views.InputDialog
        {
            Title = "Commit Changes"
        };
        dlg.Prompt = "Commit message:";
        dlg.Input = string.Empty;
        dlg.ShowCreatePullRequestOption = true;
        var result = await dlg.ShowDialog<Views.InputDialogResult?>(this);
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
        var dlg = new Views.InputDialog
        {
            Title = "Create New Branch"
        };
        dlg.Prompt = "Branch name:";
        dlg.Input = string.Empty;
        var result = await dlg.ShowDialog<Views.InputDialogResult?>(this);
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
        var dlg = new Views.InputDialog { Title = "Switch Branch" };
        dlg.Prompt = "Branch name:";
        dlg.Input = CurrentBranch;
        var result = await dlg.ShowDialog<Views.InputDialogResult?>(this);
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
        var confirm = new Views.ConfirmDialog
        {
            Title = "Rollback Changes",
            Message = "This will discard ALL local changes and delete untracked files to match HEAD.\nThis action cannot be undone. Continue?"
        };
        var ok = await confirm.ShowDialog<bool>(this);
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
        var confirm = new Views.ConfirmDialog
        {
            Title = "Initialize Git",
            Message = "Initialize a new Git repository in this folder?"
        };
        var ok = await confirm.ShowDialog<bool>(this);
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
        var line = CliInput;
        CliInput = string.Empty;
        // Log the user request locally with a label; de-dup against server echo later
        _pendingUserInput = line;
        if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
        AppendCliLog("You:");
        AppendCliLog(line);
        try
        {
            if (_cli.UseProto)
            {
                var cwd = HasWorkspace ? CurrentWorkspacePath : null;
                var approvalPolicy = "on-request";
                var (ok, prepared, error) = ProtoHelper.PrepareSubmission(
                    line,
                    cwd,
                    _submissionShape,
                    _contentStyle,
                    _defaultMsgType,
                    _currentModel,
                    approvalPolicy,
                    allowNetworkAccess: _allowNetworkAccess);
                if (!ok)
                {
                    AppendCliLog("Invalid proto submission: " + error);
                    return;
                }
                try
                {
                    AppendCliLog($"System: Turn context • network={( _allowNetworkAccess ? "on" : "off")}");
                }
                catch { }
                await _cli.SendAsync(prepared);
            }
            else
            {
                await _cli.SendAsync(line);
            }
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
