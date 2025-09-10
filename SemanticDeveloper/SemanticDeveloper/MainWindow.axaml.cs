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
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using SemanticDeveloper.Models;
using SemanticDeveloper.Services;
using SemanticDeveloper.Views;

namespace SemanticDeveloper;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly CodexCliService _cli = new();
    private ProtoHelper.SubmissionShape _submissionShape = ProtoHelper.SubmissionShape.NestedInternallyTagged;
    private ProtoHelper.ContentStyle _contentStyle = ProtoHelper.ContentStyle.Flattened;
    private string _defaultMsgType = "user_turn";
    private string? _currentModel;
    private bool _autoApproveEnabled = true;
    private AppSettings _settings = new();

    private string? _currentWorkspacePath;
    private string _cliLog = string.Empty;
    private bool _isCliRunning;
    public ObservableCollection<FileTreeItem> TreeRoots { get; } = new();
    private string? _lastRenderedAgentMessage;
    private bool _assistantStreaming;
    private string? _pendingUserInput;
    private string _tokenStats = string.Empty;

    public MainWindow()
    {
        InitializeComponent();

        DataContext = this;

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
        AppendCliLog($"System: Loaded CLI settings: '{_cli.Command}' {_cli.AdditionalArgs}");
        _currentModel = TryExtractModelFromArgs(_cli.AdditionalArgs);
        _autoApproveEnabled = _settings.AutoApproveEnabled;

        _cli.OutputReceived += OnCliOutput;
        _cli.Exited += (_, _) =>
        {
            IsCliRunning = false;
            SessionStatus = "stopped";
        };

        // Lazy-load file tree nodes when expanded
        AddHandler(TreeViewItem.ExpandedEvent, OnTreeItemExpanded, RoutingStrategies.Bubble);
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
        }
    }

    public bool HasWorkspace => !string.IsNullOrWhiteSpace(CurrentWorkspacePath) && Directory.Exists(CurrentWorkspacePath);

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

    private string _sessionStatus = "idle";
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
        // Hide noisy agent logs
        if (LooksLikeAgentLog(line)) return;
        // Hide verbose function call apply_patch payloads
        if (IsFunctionCallApplyPatch(line)) { SetStatusSafe("thinking…"); AppendCliLog("System: Applying patch…"); return; }

        // Prefer pretty rendering for protocol JSON events; fallback to raw text
        var handled = TryRenderProtocolEvent(line);
        if (!handled)
        {
            AppendCliLog(line);
        }
        if (_autoApproveEnabled && !handled)
        {
            try { TryHandleApprovalRequest(line); } catch { }
            // If CLI complains about missing id on a submission, try approving with last submit id
            if (line.Contains("missing field `id`"))
            {
                var sid = Services.ProtoHelper.LastSubmitId;
                if (!string.IsNullOrWhiteSpace(sid))
                {
                    var approvalJson = Services.ProtoHelper.BuildApproval("exec_approval", sid!, "approved", _submissionShape);
                    _ = _cli.SendAsync(approvalJson);
                    AppendCliLog($"System: Auto-approved exec_approval for last submission id {sid}.");
                }
            }
        }
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
                        if (!string.IsNullOrEmpty(chunkB64))
                        {
                            var bytes = Convert.FromBase64String(chunkB64);
                            var text = System.Text.Encoding.UTF8.GetString(bytes);
                            if (!string.IsNullOrEmpty(text)) AppendCliInline(text);
                        }
                    }
                    catch { }
                    return true;
                }
            case "exec_command_begin":
                {
                    var cmdArr = msg? ["command"] as Newtonsoft.Json.Linq.JArray;
                    var cmd = cmdArr is null ? string.Empty : string.Join(" ", cmdArr.Take(5).Select(t => t?.ToString() ?? string.Empty));
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
                    if (!CliLog.EndsWith("\n")) AppendCliInline(Environment.NewLine);
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
                    if (_autoApproveEnabled)
                    {
                        try
                        {
                            var eventId = root["id"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(eventId))
                            {
                                var op = lower.StartsWith("exec") ? "exec_approval" : "apply_patch_approval";
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
                    }
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

    private async void TryHandleApprovalRequest(string line)
    {
        var json = line.Trim();
        if (!json.StartsWith("{")) return;
        Newtonsoft.Json.Linq.JObject? root;
        try { root = (Newtonsoft.Json.Linq.JObject?)Newtonsoft.Json.Linq.JToken.Parse(json); }
        catch { return; }
        if (root is null) return;

        var msg = root["msg"] as Newtonsoft.Json.Linq.JObject;
        string? type = msg? ["type"]?.ToString();
        string? reqId = msg? ["id"]?.ToString() ?? msg? ["request_id"]?.ToString();
        // Fallback to top-level event id (correlates to submission)
        reqId ??= root["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(reqId))
        {
            // Try nested op object
            var inner = root["op"] as Newtonsoft.Json.Linq.JObject;
            type ??= inner? ["op"]?.ToString();
            reqId ??= inner? ["id"]?.ToString();
        }
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(reqId)) return;

        var lower = type.ToLowerInvariant();
        string? approvalOp = null;
        if (lower.Contains("exec") && lower.Contains("approval")) approvalOp = "exec_approval";
        if (lower.Contains("patch") && lower.Contains("approval")) approvalOp = "patch_approval";
        if (approvalOp is null) return;

        var approvalJson = Services.ProtoHelper.BuildApproval(approvalOp, reqId, "approved", _submissionShape);
        await _cli.SendAsync(approvalJson);
        AppendCliLog($"Auto-approved {approvalOp} for id {reqId}.");
    }

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
                AppendCliLog("Git not found on PATH. Skipping repository initialization.");
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

        await RestartCliAsync();
    }

    private void LoadTree(string rootPath)
    {
        void DoLoad()
        {
            TreeRoots.Clear();
            try
            {
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

    private void OnTreeItemExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TreeViewItem tvi && tvi.DataContext is FileTreeItem item)
        {
            item.LoadChildrenIfNeeded();
        }
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
            AutoApproveEnabled = _settings.AutoApproveEnabled
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
        _settings.AutoApproveEnabled = result.AutoApproveEnabled;
        SettingsService.Save(_settings);
        _currentModel = TryExtractModelFromArgs(_cli.AdditionalArgs);
        _autoApproveEnabled = result.AutoApproveEnabled;
    }

    private async void OnSendCliInputClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await SendCliInputAsync();

    private async void OnCliInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SendCliInputAsync();
        }
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
