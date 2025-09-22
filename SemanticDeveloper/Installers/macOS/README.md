SemanticDeveloper — macOS Installer Project

Overview
- Produces a macOS `.app` bundle and a `.dmg` image following Avalonia guidance.

Prerequisites (run on macOS)
- macOS 12+
- Xcode command line tools (for `hdiutil`)
- .NET 8 SDK

Build steps
- Open a terminal in this folder.
- Make the script executable: `chmod +x create_dmg.sh`
- Intel: `./create_dmg.sh osx-x64`
- Apple Silicon: `./create_dmg.sh osx-arm64`
- Result: `dist/SemanticDeveloper-osx-<arch>.dmg`

Notes
- The script creates `SemanticDeveloper.app` and then a DMG.
- You may need to sign and notarize for distribution.

References
- Avalonia deployment (macOS): https://docs.avaloniaui.net/docs/deployment/macOS
- Apple notarization: https://developer.apple.com/documentation/xcode/notarizing_macos_software_before_distribution

