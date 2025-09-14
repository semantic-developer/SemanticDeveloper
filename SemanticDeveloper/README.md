# Semantic Developer

A cross‑platform desktop UI (Avalonia/.NET 8) for driving the Codex CLI using its JSON protocol. It lets you:

- Select a workspace folder and browse files via a lazy file tree
- Start a Codex session and stream assistant output in real time
- Send user input that is wrapped as protocol `Submission`s (proto)
- Auto‑approve exec/patch requests (toggleable)
- Configure CLI command and additional args
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
   - `Command` (default: `codex`)
   - `Additional Arguments` (e.g., `--model=gpt-5-high` or `-c model=gpt-5-high`)
   - Verbose logging (show suppressed output)
   - Use API Key for Codex CLI (stores and injects `OPENAI_API_KEY` when starting the CLI)

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
  - New Branch…
    - Creates and checks out a new branch based on the default branch when available.
    - Behavior details:
      - Performs a best‑effort `fetch` from `origin` first (no merge/rebase into your working copy).
      - Bases the new branch on, in order of preference: `origin/main`, `origin/master`, local `main`, local `master`, then current `HEAD`.
      - Example log: `Created and checked out 'feature-x' (based on origin/main).`
  - Switch Branch…
    - Checks out an existing branch by name (no automatic fetch/merge).
  - Rollback Changes…
    - Hard‑resets the working directory to `HEAD` and deletes untracked files.
    - Prompts for confirmation since this discards local changes.
  - Refresh
    - Refreshes the branch label and the file tree’s Git status coloring.

- Initialize Git…
  - When the workspace is not a Git repo, an “Initialize Git…” button appears in the header.
  - Initializes a repository in the selected folder, stages files, and attempts an initial commit (best‑effort).
  - This is the same capability offered right after selecting a non‑repo folder.

Notes
- Operations are local and safe: no network is used except the optional `fetch` for “New Branch…”.
- Pull/push are not exposed in the UI. If desired, they can be added later.
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

## Troubleshooting

- “Failed to start 'codex'”: Ensure the CLI is installed and on `PATH`. Test with `codex --help` and `codex proto --help`.
- Model selection: Provide a model via `Additional Arguments`, e.g., `--model=gpt-5-high`.
  - If you pass a `-low`, `-medium`, or `-high` suffix (e.g., `gpt-5-high`), the app normalizes it to `model=gpt-5` + `effort=high` in the submission payload.
- Git init issues: The app uses LibGit2Sharp (no Git CLI needed). If the native lib fails to load, the app skips initialization. Commits use your configured name/email if available; otherwise a fallback signature is used.

## Project Layout

- `SemanticDeveloper/` — App source (UI, services, models). This README lives here.
- `proto-reference/` — Rust protocol reference for Codex (not built by this project).

## Notes

- Proto mode is enforced in code; the app does not fall back to non‑proto modes.
- Settings are stored under the OS‑specific application data directory and loaded on startup.
- The log view uses AvaloniaEdit + TextMate (Dark+) for better legibility and simple JSON syntax coloring.
- Token stats in the header are derived from `token_count` events. The percent remaining is estimated (using a baseline of ~12k tokens for fixed prompts) and may be approximate.
