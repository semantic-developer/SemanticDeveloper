# Semantic Developer

A cross‑platform desktop UI (Avalonia/.NET 8) for driving the Codex CLI using its JSON protocol. It lets you:

- Select a workspace folder and browse files via a lazy file tree
- Start a Codex session and stream assistant output in real time
- Send user input that is wrapped as protocol `Submission`s (proto)
- Auto‑approve exec/patch requests (automatic)
- Select a Codex profile (from `config.toml`) and load MCP servers from a JSON config
– See live token usage and estimated context remaining in the header

> Important: This app always runs Codex in proto mode via the `proto` subcommand.

## Requirements

- .NET SDK 8.0+
- Codex CLI installed and on `PATH`
  - Verify with: `codex proto --help`
- No external Git required — uses LibGit2Sharp for repo init/staging/commit

## Build & Run

- Restore/build:
  - `dotnet build SemanticDeveloper/SemanticDeveloper.sln`
- Run the app:
  - `dotnet run --project SemanticDeveloper/SemanticDeveloper`

## Usage

1. Open the app, click “Select Workspace…” and choose a folder.
   - If it isn’t a git repo and the Git library is available, you’ll be prompted to initialize one.
   - You can also initialize later from the header via “Initialize Git…”.
2. Click “Restart Session” to launch `codex proto` in the workspace directory (a session also starts automatically after you select a workspace).
3. Type into the input box and press Enter to send. Output appears in the right panel.
4. “CLI Settings” lets you change:
   - Profile (from Codex `config.toml`) — passed via `-c profile=<name>`
     - `config.toml` path: `$CODEX_HOME/config.toml` (defaults to `~/.codex/config.toml`)
   - Verbose logging (show suppressed output)
   - Enable MCP support (loads MCP servers from your JSON config and passes them directly to Codex)
     - Config path: `~/.config/SemanticDeveloper/mcp_servers.json` (Linux/macOS) or `%AppData%/SemanticDeveloper/mcp_servers.json` (Windows)
  - Use API Key for Codex CLI (runs `codex login --api-key <key>` before sessions; does not rely on existing CLI auth)
  - Allow network access for tools (sets sandbox_policy.network_access=true on turns so MCP tools can reach the network)
  - Without API key enabled, the app proactively authenticates with `codex auth login` (falling back to `codex login`) before sessions so your chat/GPT token is used.

### Profiles (config.toml) example

Example `config.toml` profiles:

```toml
[profiles.gpt-5-high]
model = "gpt-5"
model_provider = "openai"
approval_policy = "never"
model_reasoning_effort = "high"
model_reasoning_summary = "auto"

[profiles.gpt-5-medium]
model = "gpt-5"
model_provider = "openai"
approval_policy = "never"
model_reasoning_effort = "medium"
model_reasoning_summary = "auto"

[profiles.gpt-5-low]
model = "gpt-5"
model_provider = "openai"
approval_policy = "never"
model_reasoning_effort = "low"
model_reasoning_summary = "auto"

[profiles.gpt-5-codex-high]
model = "gpt-5-codex"
model_provider = "openai"
approval_policy = "never"
model_reasoning_effort = "high"
model_reasoning_summary = "auto"

[profiles.gpt-5-codex-medium]
model = "gpt-5-codex"
model_provider = "openai"
approval_policy = "never"
model_reasoning_effort = "medium"
model_reasoning_summary = "auto"
```


5. The left file tree and right log pane are resizable using the vertical splitter between them.

6. The header shows:
   - Current status: `idle`, `thinking…`, `responding…`, `applying patch…`, `starting…`, or `error`.
   - A soft indeterminate progress bar while busy.
   - Token stats (when available): `tokens <blended-total> • <percent> left`.
     The percent remaining is an estimate based on the model’s context window and may differ slightly from the server’s internal view.
   - When inside a Git repository: current branch and a small Git menu for quick actions.

## Git Integration

The app integrates basic Git operations directly in the header. All actions use LibGit2Sharp (embedded libgit2); the system `git` command is not required.

- Branch indicator
  - Shows the current branch (e.g., `main`) after the workspace path when the selected folder is inside a Git repo.

- Git menu (Git ▾)
  - Commit…
    - Stages all changes (`*`) and creates a commit with the provided message.
    - Uses your Git config for name/email if available; otherwise falls back to a local signature like `<user>@local`.
    - If there are no changes, you’ll get a friendly “No changes to commit.” notice.
    - Automatically pushes the current branch to its tracked remote (defaults to `origin`).
    - Optional: tick **Create Pull Request** to open your browser to a GitHub compare page after a successful push.
  - New Branch…
    - Creates and checks out a new branch based on the default branch when available.
    - Behavior details:
      - Performs a best‑effort `fetch` from `origin` first (no merge/rebase into your working copy).
      - Bases the new branch on, in order of preference: `origin/main`, `origin/master`, local `main`, local `master`, then current `HEAD`.
      - Example log: `Created and checked out 'feature-x' (based on origin/main).`
  - Switch Branch…
    - Checks out an existing branch by name (no automatic fetch/merge).
  - Get Latest
    - Fetches from the tracked remote (defaults to `origin`) and fast-forwards the current branch when possible.
    - Requires the branch to track a remote counterpart; otherwise a helpful log message is shown.
    - Stops early if a merge or rebase would be required (fast-forward only).
  - Rollback Changes…
    - Hard‑resets the working directory to `HEAD` and deletes untracked files.
    - Prompts for confirmation since this discards local changes.
  - Refresh
    - Refreshes the branch label and the file tree’s Git status coloring.

Example workflow
1. Switch to an existing base branch (e.g., `main` or `master`).
2. Choose **Git ▾ → Get Latest** to fast-forward your local branch.
3. Use **Git ▾ → New Branch…** with your preferred naming convention (e.g., `feature/login-form`).
4. After making changes, select **Commit…**, enter a message, let the app push the branch for you, and optionally enable **Create Pull Request** to jump straight to GitHub once the push completes.

- Initialize Git…
  - When the workspace is not a Git repo, an “Initialize Git…” button appears in the header.
  - Initializes a repository in the selected folder, stages files, and attempts an initial commit (best‑effort).
  - This is the same capability offered right after selecting a non‑repo folder.

Notes
- Operations are local unless a remote call is required (the optional `fetch` during “New Branch…”, the fast-forward fetch performed by “Get Latest”, and the push that runs after each commit).
- Open your workspace at the root of the Git repository (the folder containing `.git/`) so the app can detect and enable Git features; selecting a subdirectory skips the Git UI.
- Pull support is limited to fast-forwarding via **Get Latest**; pushing is still not exposed in the UI.
- On some Linux distros, libgit2 may require additional native dependencies. If the Git library can’t load, the UI will hide Git actions and log a helpful message.

## Conversation & Protocol Behavior

- Always uses proto mode: the app starts the CLI with `codex proto`.
- User input is wrapped as a protocol `Submission` with a new `id` and an `op` payload:
  - Defaults to `user_input` with `items: [{ type: "text", text: "..." }]`.
  - When the app infers that a full turn is required, it sends `user_turn` and includes
    `cwd`, `approval_policy` (defaults to `on-request`), `sandbox_policy` (defaults to
    `workspace-write`), optional `model`, and default reasoning fields.
- Auto‑approval: when the CLI emits an `exec_approval_request` or `apply_patch_approval_request`,
  the app automatically approves the request. There is no setting to toggle this.

- Conversation rendering (right pane):
  - Messages are labeled and colorized: `You:`, `Assistant:`, and `System:`.
  - Streaming assistant deltas append inline under a single `Assistant:` header.
  - Noisy protocol JSON, unified diffs, and patch bodies are suppressed; concise system lines are logged instead (e.g., `System: Applying patch…`, `System: Patch applied`).
  - “System” lines denote app/system events and CLI housekeeping, so you can distinguish them from assistant content.

- Status behavior during a turn:
  - `thinking…` while the model reasons or patches are being applied.
  - `responding…` while streaming assistant deltas.
  - Returns to `idle` only when the server signals `task_complete` (or `turn_aborted`).

- Stop vs. Restart:
  - Stop sends a proto `interrupt` to abort the current turn (like pressing Esc in the CLI) without killing the session; it falls back to terminating the process if needed.
  - Restart ends the current process and starts a fresh session in the same workspace.

- Clear Log clears both the on‑screen log and the underlying editor document; it does not affect the session.

## MCP Servers Panel

- The left pane includes an MCP section below the file tree:
  - Servers list: a checkbox per server from `mcp_servers.json`. Only selected servers are injected at session start.
  - Tools list: after session starts, tools are grouped under their server names using short identifiers (the full identifier is available as a tooltip).
  - Header buttons:
    - ⚙ opens `mcp_servers.json` in your editor.
    - ↻ reloads the config and updates the server list.
- Only local stdio servers are supported (command/args/cwd/env). Remote transports (e.g., SSE) are not injected.

Config file location:
- Linux/macOS: `~/.config/SemanticDeveloper/mcp_servers.json`
- Windows: `%AppData%/SemanticDeveloper/mcp_servers.json`

Selection behavior:
- The checkbox state in the MCP pane determines which servers are passed to Codex at session start.
- Change selections, then click “Restart Session” to apply.
## Troubleshooting

- “Failed to start 'codex'”: Ensure the CLI is installed and on `PATH`. Test with `codex --help` and `codex proto --help`.
- Model selection: Prefer using `config.toml` (via Profiles). You can set `model`, `model_provider`, and related options per the Codex docs.
- Git init issues: The app uses LibGit2Sharp (no Git CLI needed). If the native lib fails to load, the app skips initialization. Commits use your configured name/email if available; otherwise a fallback signature is used.

- Authentication:
  - If you are not using an API key and the Codex CLI is not logged in (no `~/.codex/auth.json`), the proto stream returns 401. The app detects this and prompts to run `codex auth login` for you. Follow the browser flow; on success the app restarts the proto session automatically.
  - If your CLI version doesn’t support `auth login`, the app falls back to `codex login`.
  - When “Use API Key” is enabled in CLI Settings, the app attempts a non‑interactive `codex login --api-key <key>` before sessions and on 401. If login succeeds, it restarts the session automatically.

## Run App

- The bottom-right pane has a `Run App` button (next to Shell) that scans the workspace for common runnable targets and either runs the single best candidate or lets you choose among multiple options.
- Detection heuristics (depth-limited, skipping heavy folders like `node_modules/`, `bin/`, `obj/`):
  - Node: `package.json` with `dev` or `start` → prefers `yarn` (when `yarn.lock`), `pnpm` (when `pnpm-lock.yaml`), else `npm`. Builds first when a `build` script exists.
  - .NET: `*.sln` and `*.csproj` → enumerates projects and runs `dotnet build` then `dotnet run --project <csproj>` for the selected one.
  - Rust: `Cargo.toml` → builds with `cargo build` then runs `cargo run`.
  - Python: `main.py` or `app.py` → `python3`/`python`.
  - Go: `go.mod` → builds with `go build` then runs `go run .`.
  - Java: `pom.xml` (Maven) → runs `mvn package` then runs the jar from `target/`. `build.gradle`/`gradlew` (Gradle) → `gradle build` then `gradle run`.
  - HTML: `index.html` → opens in your default browser.
- Output from commands streams into the log. For long-running dev servers, the process runs until you close it from its own console or terminate externally.

## Project Layout

- `SemanticDeveloper/` — App source (UI, services, models). This README lives here.
- `proto-reference/` — Rust protocol reference for Codex (not built by this project). Includes MCP/client protocol shapes.

## Notes

- Proto mode is enforced in code; the app does not fall back to non‑proto modes.
- Settings are stored under the OS‑specific application data directory and loaded on startup.
- The log view uses AvaloniaEdit + TextMate (Dark+) for better legibility and simple JSON syntax coloring.

## Installers

- Windows: see `SemanticDeveloper/Installers/Windows` for `build.ps1` to produce a ZIP or Inno Setup installer.
- macOS: see `SemanticDeveloper/Installers/macOS` for `create_dmg.sh` to produce a `.app` and `.dmg`.
- Linux (Debian/Ubuntu): see `SemanticDeveloper/Installers/Linux` for `build_deb.sh` to produce a `.deb`.

Each installer project contains detailed prerequisites and step-by-step instructions.
- Token stats in the header are derived from `token_count` events. The percent remaining is estimated (using a baseline of ~12k tokens for fixed prompts) and may be approximate.
