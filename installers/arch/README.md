# Susurri - Arch Linux Installer

Install Susurri secure P2P chat application on Arch Linux.

## What's Installed

- **Susurri GUI** - Full desktop application (Avalonia-based)
- **Susurri CLI** - Terminal-based interface

## Quick Install

### Option 1: Installation Script (Recommended)

```bash
cd installers/arch
./install.sh
```

This will:
- Check and install required dependencies
- Build both GUI and CLI from source
- Install to `~/.local/share/susurri`
- Create commands: `susurri` (CLI) and `susurri-gui` (GUI)
- Add desktop entries for application menu

### Option 2: PKGBUILD (Local Development)

```bash
cd installers/arch
makepkg -si -p PKGBUILD.local
```

This installs system-wide to `/opt/susurri`.

### Option 3: System-wide Installation

```bash
cd installers/arch
sudo ./install.sh --system
```

## Requirements

**Required:**
- `dotnet-sdk` (10.0+) - For building
- `dotnet-runtime` (10.0+) - For running
- `libsodium` - Cryptographic operations
- `libx11`, `libxcursor`, `libxrandr`, `libxi`, `mesa` - GUI dependencies

Install dependencies:
```bash
sudo pacman -S dotnet-sdk dotnet-runtime libsodium libx11 libxcursor libxrandr libxi mesa
```

## Usage

### Desktop Application (GUI)

```bash
susurri-gui
```

Or search for "Susurri" in your application menu.

The GUI provides:
- Login with BIP39 passphrase
- Passphrase generation
- DHT node control (start/stop)
- Status dashboard
- Settings management

### Terminal Interface (CLI)

```bash
susurri
```

Interactive commands:
- `help` - Show available commands
- `login <username>` - Login with passphrase
- `logout` - Logout current user
- `status` - Show current status
- `dht start [port]` - Start DHT node (default: 7070)
- `dht stop` - Stop DHT node
- `dht status` - Show DHT node status
- `ping <host> <port>` - Ping a DHT node
- `exit` - Exit application

## Configuration

Configuration file location:
- User: `~/.config/susurri/appsettings.json`
- System: `/etc/susurri/appsettings.json`

Example configuration:
```json
{
  "Messaging": {
    "UseBackgroundDispatcher": true
  },
  "DHT": {
    "DefaultPort": 7070,
    "BootstrapNodes": []
  },
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Susurri": "Information"
    }
  }
}
```

## Uninstall

### User Installation
```bash
~/.local/share/susurri/uninstall.sh
```

Or:
```bash
./install.sh --uninstall
```

### PKGBUILD Installation
```bash
sudo pacman -R susurri-local
```

## File Locations

**User Installation:**
```
~/.local/share/susurri/         - CLI files
~/.local/share/susurri/gui/     - GUI files
~/.local/bin/susurri            - CLI launcher
~/.local/bin/susurri-gui        - GUI launcher
~/.config/susurri/              - User configuration
~/.local/share/applications/    - Desktop entries
~/.local/share/icons/           - Application icon
```

**System Installation:**
```
/opt/susurri/                   - CLI files
/opt/susurri/gui/               - GUI files
/usr/bin/susurri                - CLI launcher
/usr/bin/susurri-gui            - GUI launcher
/etc/susurri/                   - System configuration
/usr/share/applications/        - Desktop entries
/usr/share/icons/               - Application icon
```

## Troubleshooting

### Command not found after installation

Add `~/.local/bin` to your PATH:
```bash
echo 'export PATH="$PATH:$HOME/.local/bin"' >> ~/.bashrc
source ~/.bashrc
```

### GUI won't start

Ensure X11 dependencies are installed:
```bash
sudo pacman -S libx11 libxcursor libxrandr libxi mesa
```

### Build fails

Ensure you have .NET SDK 10.0+:
```bash
dotnet --version
sudo pacman -S dotnet-sdk
```

## License

MIT License - See LICENSE file in project root.
