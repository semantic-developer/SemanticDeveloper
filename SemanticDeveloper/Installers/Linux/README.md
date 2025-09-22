SemanticDeveloper — Linux Installer Project (Debian/Ubuntu)

Overview
- Produces a `.deb` package for Debian/Ubuntu following Avalonia guidance.

Prerequisites (run on Debian/Ubuntu)
- .NET 8 SDK
- `dpkg-deb`, `fakeroot`

Build steps
- Open a terminal in this folder.
- Make the script executable: `chmod +x build_deb.sh`
- x64: `./build_deb.sh linux-x64`
- arm64: `./build_deb.sh linux-arm64`
- Result: `dist/semantic-developer_<version>_<arch>.deb`

Notes
- Installs under `/opt/semantic-developer` and adds launcher under `/usr/share/applications`.

References
- Avalonia deployment (Debian/Ubuntu): https://docs.avaloniaui.net/docs/deployment/debian-ubuntu

