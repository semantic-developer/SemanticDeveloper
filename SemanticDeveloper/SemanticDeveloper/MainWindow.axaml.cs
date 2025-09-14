using System;
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
                var registryOptions = new RegistryOptions(ThemeName.DarkPlus);
                var installation = editor.InstallTextMate(registryOptions);
                installation.SetGrammar("source.json");
                editor.TextArea.TextView.LineTransformers.Add(new LogPrefixColorizer());
            }
        }
        catch { }

        // Load settings and apply
        _settings = SettingsService.Load();
        _cli.Command = _settings.Command;
        _cli.AdditionalArgs = _settings.AdditionalArgs;
        _cli.UseApiKey = _settings.UseApiKey;
        _cli.ApiKey = string.IsNullOrWhiteSpace(_settings.ApiKey) ? null : _settings.ApiKey;
        AppendCliLog($"System: Loaded CLI settings: '{_cli.Command}' {_cli.AdditionalArgs}");
        _currentModel = TryExtractModelFromArgs(_cli.AdditionalArgs);
        _verboseLogging = _settings.VerboseLoggingEnabled;

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

    private void AppendCliLog(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var cleaned = TextFilter.StripAnsi(text);
        if (Dispatcher.UIThread.CheckAccess())
        {
            CliLog += cleaned + Environment.NewLine;
            CliLogDocument.Text += cleaned + Environment.NewLine;
        }
        else
        {
            Dispatcher.UIThread.Post(() => { CliLog += cleaned + Environment.NewLine; CliLogDocument.Text += cleaned + Environment.NewLine; });
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
        }
        else
        {
            Dispatcher.UIThread.Post(() => { CliLog += cleaned; CliLogDocument.Text += cleaned; });
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
                    if (!string.IsNullOrWhiteSpace(callId) && _execSuppressed.Contains(callId!))
                        AppendCliLog($"System: (exit {code}) — output suppressed");
                    else if (!string.IsNullOrWhiteSpace(callId) && _execTruncated.Contains(callId!))
                        AppendCliLog($"System: (exit {code}) — output truncated");
                    else
                        AppendCliLog($"System: (exit {code})");
                    return true;
                }
            case "patch_apply_begin":
                {
                    AppendCliLog("System: Applying patch…");
                    SetStatusSafe("thinking…");
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
                return true;
            case "task_complete":
                if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
                AppendCliLog("System: Task complete");
                SetStatusSafe("idle");
                _assistantStreaming = false;
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
        long reasoningOut = total.Value<long?>("reasoning_output_tokens") ?? 0L;
        long totalTokens = total.Value<long?>("total_tokens") ?? 0L;
        long blendedTotal = Math.Max(0, (input - cached)) + output;

        var modelCw = (info["model_context_window"]?.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
            ? info.Value<long?>("model_context_window")
            : null;

        int? percentLeft = null;
        if (modelCw.HasValue && modelCw.Value > 0)
        {
            const long BASELINE = 12000;
            long window = modelCw.Value;
            if (window > BASELINE)
            {
                long effectiveWindow = window - BASELINE;
                long tokensInWindow = Math.Max(0, totalTokens - reasoningOut);
                long used = Math.Max(0, tokensInWindow - BASELINE);
                long remaining = Math.Max(0, effectiveWindow - used);
                percentLeft = (int)Math.Clamp((remaining * 100.0 / effectiveWindow), 0, 100);
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
                    CurrentFileDocument.Text = text;
                else
                    Dispatcher.UIThread.Post(() => CurrentFileDocument.Text = text);
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
                if (_imageBase != null) { _imageBase.IsVisible = false; _imageBase.Source = null; }
                if (_editorBase != null) _editorBase.IsVisible = true;
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    BaseFileDocument.Text = string.Empty;
                    if (_imageBase != null) { _imageBase.IsVisible = false; _imageBase.Source = null; }
                    if (_editorBase != null) _editorBase.IsVisible = true;
                });
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(_selectedFilePath))
        {
            if (Dispatcher.UIThread.CheckAccess())
                BaseFileDocument.Text = string.Empty;
            else
                Dispatcher.UIThread.Post(() => BaseFileDocument.Text = string.Empty);
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
                    if (Dispatcher.UIThread.CheckAccess()) BaseFileDocument.Text = text;
                    else Dispatcher.UIThread.Post(() => BaseFileDocument.Text = text);

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
                if (Dispatcher.UIThread.CheckAccess()) BaseFileDocument.Text = $"Failed to read from git: {ex.Message}";
                else Dispatcher.UIThread.Post(() => BaseFileDocument.Text = $"Failed to read from git: {ex.Message}");
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
                ".xml" => "text.xml",
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
                ".ini" => "source.ini",
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

        CliLog = string.Empty;
        SessionStatus = "starting…";
        try
        {
            await _cli.StartAsync(CurrentWorkspacePath, CancellationToken.None);
            IsCliRunning = _cli.IsRunning;
            SessionStatus = IsCliRunning ? "idle" : "stopped";
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
            AdditionalArgs = _cli.AdditionalArgs,
            VerboseLoggingEnabled = _verboseLogging,
            UseApiKey = _settings.UseApiKey,
            ApiKey = _settings.ApiKey
        };
        dialog.DataContext = vm;
        var result = await dialog.ShowDialog<CliSettings?>(this);
        if (result is null) return;
        _cli.Command = result.Command;
        _cli.AdditionalArgs = result.AdditionalArgs;
        AppendCliLog($"System: CLI settings updated: '{_cli.Command}' {_cli.AdditionalArgs} (--proto enabled)");
        // persist settings
        _settings.Command = _cli.Command;
        _settings.AdditionalArgs = _cli.AdditionalArgs;
        _settings.VerboseLoggingEnabled = result.VerboseLoggingEnabled;
        _settings.UseApiKey = result.UseApiKey;
        _settings.ApiKey = result.ApiKey ?? string.Empty;
        _cli.UseApiKey = _settings.UseApiKey;
        _cli.ApiKey = string.IsNullOrWhiteSpace(_settings.ApiKey) ? null : _settings.ApiKey;
        SettingsService.Save(_settings);
        _currentModel = TryExtractModelFromArgs(_cli.AdditionalArgs);
        _verboseLogging = result.VerboseLoggingEnabled;
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
        var message = await dlg.ShowDialog<string?>(this);
        if (message is null) return;
        var (ok, output, error) = GitHelper.TryCommitAll(CurrentWorkspacePath, message);
        if (ok)
        {
            AppendCliLog(output ?? "Committed.");
            LoadTree(CurrentWorkspacePath);
        }
        else
        {
            AppendCliLog("Git commit failed: " + (error ?? "unknown error"));
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
        var name = await dlg.ShowDialog<string?>(this);
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
        var name = await dlg.ShowDialog<string?>(this);
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
                var (ok, prepared, error) = ProtoHelper.PrepareSubmission(line, cwd, _submissionShape, _contentStyle, _defaultMsgType, _currentModel, approvalPolicy);
                if (!ok)
                {
                    AppendCliLog("Invalid proto submission: " + error);
                    return;
                }
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
