SemanticDeveloper — Windows Installer Project

Overview
- Produces a distributable for Windows: either a ZIP of the self-contained publish output or an installer using Inno Setup (optional).
- Follows Avalonia guidance: publish self-contained, single-file, no trimming.

Prerequisites
- Windows 10/11
- .NET 8 SDK
- Optional (for .exe installer): Inno Setup 6 (`iscc.exe`) added to PATH

Build steps (ZIP package)
- Open a PowerShell prompt in this folder.
- Run: `./build.ps1 -Rid win-x64 -Mode Zip`
- Result: `artifacts/SemanticDeveloper-win-x64.zip`

Build steps (Inno Setup installer)
- Ensure Inno Setup is installed and `iscc.exe` is on PATH.
- Run: `./build.ps1 -Rid win-x64 -Mode Inno`
- Result: `artifacts/SemanticDeveloperSetup-win-x64.exe`

Notes
- The script publishes the app from `../..//SemanticDeveloper/SemanticDeveloper.csproj`.
- To build for ARM64, use `-Rid win-arm64`.
- You can customize app metadata in `SemanticDeveloper.iss`.

References
- Avalonia deployment (Windows): https://docs.avaloniaui.net/docs/deployment/windows

