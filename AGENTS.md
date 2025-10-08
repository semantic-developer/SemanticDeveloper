# Repository Guidelines

## Project Structure & Module Organization
- Root solution lives in `SemanticDeveloper/SemanticDeveloper.sln`; the primary desktop app sits under `SemanticDeveloper/SemanticDeveloper/`.
- UI resources are split between Avalonia XAML (`*.axaml`) and backing C# files (`*.axaml.cs`); dialogs reside in `Views/`, shared models in `Models/`, and service logic (Codex, Git, MCP, settings) in `Services/`.
- Static assets (icons, images) are in `SemanticDeveloper/SemanticDeveloper/Images/`; installer scaffolding lives in `SemanticDeveloper/Installers/`.

## Build, Test, and Development Commands
- Restore & compile the app: `dotnet build SemanticDeveloper/SemanticDeveloper/SemanticDeveloper.csproj` (the installer projects lack entry points).
- Run the desktop app: `dotnet run --project SemanticDeveloper/SemanticDeveloper`.
- Update NuGet dependencies: `dotnet restore SemanticDeveloper/SemanticDeveloper/SemanticDeveloper.csproj`.

## Coding Style & Naming Conventions
- Follow standard C# conventions: 4-space indentation, PascalCase for types, camelCase for locals/fields (prefix private fields with `_` when mutable).
- Keep Avalonia XAML tidy: align attributes, prefer named handlers declared in the paired code-behind.
- Favor expression-bodied members for single-line getters and avoid trailing whitespace; run `dotnet format` before large refactors.
- When logging to the CLI pane, use `AppendCliLog` for line entries and prefer the existing `System:` prefixes for system-generated lines.

## Testing Guidelines
- No automated test project exists yet; add new tests alongside features if appropriate.
- For manual verification, exercise key flows: workspace selection, MCP server loading, Codex login (`codex auth login`), and session restarts.
- If you introduce automated tests, place them under a `Tests/` sibling folder and document the execution command (e.g., `dotnet test`).

## Commit & Pull Request Guidelines
- Write concise, imperative commit subjects (`Add MCP startup summary`, `Fix Codex auth probe`), with optional body paragraphs for context.
- Reference issue IDs in the body when applicable and squash trivial commits before submitting a PR.
- Pull requests should include: a brief summary, testing notes, screenshots/GIFs for UI changes, and links to relevant issues or discussions.

## Configuration & Security Notes
- User-specific MCP servers live at `~/.config/SemanticDeveloper/mcp_servers.json` (or `%AppData%\SemanticDeveloper\mcp_servers.json` on Windows); avoid committing sample credentials.
- API keys are configured through the app settings; never hard-code secrets—use environment variables or the settings dialog.
