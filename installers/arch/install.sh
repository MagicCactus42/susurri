#!/bin/bash

# Susurri Installer for Arch Linux
# Secure P2P Chat Application
# Builds from local source and installs to user directories

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color
BOLD='\033[1m'

# Configuration
APP_NAME="susurri"
APP_DISPLAY_NAME="Susurri"
APP_VERSION="1.0.0"
INSTALL_DIR="${HOME}/.local/share/${APP_NAME}"
BIN_DIR="${HOME}/.local/bin"
DESKTOP_DIR="${HOME}/.local/share/applications"
ICON_DIR="${HOME}/.local/share/icons/hicolor/scalable/apps"
CONFIG_DIR="${HOME}/.config/${APP_NAME}"

# Determine script and project directories
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

# Print banner
print_banner() {
    echo -e "${CYAN}"
    echo "  ____                            _ "
    echo " / ___| _   _ ___ _   _ _ __ _ __(_)"
    echo " \\___ \\| | | / __| | | | '__| '__| |"
    echo "  ___) | |_| \\__ \\ |_| | |  | |  | |"
    echo " |____/ \\__,_|___/\\__,_|_|  |_|  |_|"
    echo -e "${NC}"
    echo -e "${BOLD}Secure P2P Chat - Arch Linux Installer v${APP_VERSION}${NC}"
    echo ""
}

# Print colored messages
print_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
print_success() { echo -e "${GREEN}[SUCCESS]${NC} $1"; }
print_warning() { echo -e "${YELLOW}[WARNING]${NC} $1"; }
print_error() { echo -e "${RED}[ERROR]${NC} $1"; }
print_step() { echo -e "${CYAN}[STEP $1]${NC} $2"; }

# Check if running on Arch Linux
check_distro() {
    if [ -f /etc/arch-release ]; then
        return 0
    else
        print_warning "This installer is designed for Arch Linux."
        read -p "Continue anyway? (y/N): " -n 1 -r
        echo
        if [[ ! $REPLY =~ ^[Yy]$ ]]; then
            exit 1
        fi
    fi
}

# Check and install dependencies
check_dependencies() {
    print_step "1/8" "Checking dependencies..."

    local missing_deps=()

    # Check for .NET SDK (needed to build)
    if ! command -v dotnet &> /dev/null; then
        missing_deps+=("dotnet-sdk")
    else
        local dotnet_version=$(dotnet --version 2>/dev/null | cut -d'.' -f1)
        if [ "$dotnet_version" -lt 10 ] 2>/dev/null; then
            print_warning "Found .NET $(dotnet --version), but 10.0+ is required"
            missing_deps+=("dotnet-sdk")
        else
            print_info "Found .NET SDK: $(dotnet --version)"
        fi
    fi

    # Check for libsodium (required for cryptography)
    if ! pacman -Qi libsodium &> /dev/null 2>&1; then
        missing_deps+=("libsodium")
    else
        print_info "Found libsodium"
    fi

    # Check for X11 dependencies (required for Avalonia GUI)
    for pkg in libx11 libxcursor libxrandr libxi mesa; do
        if ! pacman -Qi $pkg &> /dev/null 2>&1; then
            missing_deps+=("$pkg")
        fi
    done

    # If dependencies are missing, offer to install them
    if [ ${#missing_deps[@]} -gt 0 ]; then
        print_warning "Missing required dependencies: ${missing_deps[*]}"
        read -p "Install missing dependencies? (Y/n): " -n 1 -r
        echo
        if [[ ! $REPLY =~ ^[Nn]$ ]]; then
            print_info "Installing dependencies..."
            sudo pacman -S --needed "${missing_deps[@]}"
        else
            print_error "Cannot continue without required dependencies."
            exit 1
        fi
    fi

    print_success "All required dependencies satisfied"
}

# Create installation directories
create_directories() {
    print_step "2/8" "Creating directories..."

    mkdir -p "${INSTALL_DIR}"
    mkdir -p "${INSTALL_DIR}/gui"
    mkdir -p "${BIN_DIR}"
    mkdir -p "${DESKTOP_DIR}"
    mkdir -p "${ICON_DIR}"
    mkdir -p "${CONFIG_DIR}"

    print_success "Directories created"
}

# Build the CLI application
build_cli() {
    print_step "3/8" "Building CLI application..."

    local cli_project="${PROJECT_ROOT}/src/Bootstrapper/Susurri.CLI/Susurri.CLI.csproj"
    local publish_dir="${PROJECT_ROOT}/publish-cli"

    if [ ! -f "${cli_project}" ]; then
        print_error "CLI project not found at: ${cli_project}"
        print_error "Please run this script from the installers/arch directory."
        exit 1
    fi

    # Clean previous build
    rm -rf "${publish_dir}"

    # Restore and build
    cd "${PROJECT_ROOT}"
    dotnet restore "${cli_project}"
    dotnet publish "${cli_project}" \
        -c Release \
        -r linux-x64 \
        --self-contained false \
        -o "${publish_dir}"

    if [ ! -f "${publish_dir}/susurri-cli" ]; then
        print_error "CLI build failed: susurri-cli not found"
        exit 1
    fi

    print_success "CLI built successfully"
}

# Build the GUI application
build_gui() {
    print_step "4/8" "Building GUI application..."

    local gui_project="${PROJECT_ROOT}/src/Bootstrapper/Susurri.GUI/Susurri.GUI.csproj"
    local publish_dir="${PROJECT_ROOT}/publish-gui"

    if [ ! -f "${gui_project}" ]; then
        print_error "GUI project not found at: ${gui_project}"
        exit 1
    fi

    # Clean previous build
    rm -rf "${publish_dir}"

    # Restore and build
    cd "${PROJECT_ROOT}"
    dotnet restore "${gui_project}"
    dotnet publish "${gui_project}" \
        -c Release \
        -r linux-x64 \
        --self-contained false \
        -o "${publish_dir}"

    if [ ! -f "${publish_dir}/Susurri.GUI" ]; then
        print_error "GUI build failed: Susurri.GUI not found"
        exit 1
    fi

    print_success "GUI built successfully"
}

# Install application files
install_files() {
    print_step "5/8" "Installing application files..."

    local cli_publish="${PROJECT_ROOT}/publish-cli"
    local gui_publish="${PROJECT_ROOT}/publish-gui"

    # Copy CLI files
    cp -r "${cli_publish}/"* "${INSTALL_DIR}/"
    chmod +x "${INSTALL_DIR}/susurri-cli"

    # Copy GUI files
    cp -r "${gui_publish}/"* "${INSTALL_DIR}/gui/"
    chmod +x "${INSTALL_DIR}/gui/Susurri.GUI"

    # Create default config if not exists
    if [ ! -f "${CONFIG_DIR}/appsettings.json" ]; then
        cat > "${CONFIG_DIR}/appsettings.json" << 'EOF'
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
EOF
    fi

    print_success "Application files installed to ${INSTALL_DIR}"
}

# Create launcher scripts
create_launchers() {
    print_step "6/8" "Creating launcher scripts..."

    # CLI launcher
    cat > "${BIN_DIR}/susurri" << EOF
#!/bin/bash
# Susurri CLI Launcher
exec "${INSTALL_DIR}/susurri-cli" "\$@"
EOF
    chmod +x "${BIN_DIR}/susurri"

    # GUI launcher
    cat > "${BIN_DIR}/susurri-gui" << EOF
#!/bin/bash
# Susurri GUI Launcher - Avalonia Desktop Application
exec "${INSTALL_DIR}/gui/Susurri.GUI" "\$@"
EOF
    chmod +x "${BIN_DIR}/susurri-gui"

    # Check if ~/.local/bin is in PATH
    if [[ ":$PATH:" != *":${BIN_DIR}:"* ]]; then
        print_warning "${BIN_DIR} is not in your PATH"
        print_info "Add this to your ~/.bashrc or ~/.zshrc:"
        echo -e "    ${YELLOW}export PATH=\"\$PATH:${BIN_DIR}\"${NC}"
        echo ""

        # Offer to add it automatically
        read -p "Add to shell config now? (Y/n): " -n 1 -r
        echo
        if [[ ! $REPLY =~ ^[Nn]$ ]]; then
            if [ -f "${HOME}/.bashrc" ]; then
                echo 'export PATH="$PATH:$HOME/.local/bin"' >> "${HOME}/.bashrc"
                print_success "Added to ~/.bashrc"
            fi
            if [ -f "${HOME}/.zshrc" ]; then
                echo 'export PATH="$PATH:$HOME/.local/bin"' >> "${HOME}/.zshrc"
                print_success "Added to ~/.zshrc"
            fi
            print_info "Please restart your shell or run: source ~/.bashrc"
        fi
    fi

    print_success "Launchers created: susurri, susurri-gui"
}

# Create desktop entries
create_desktop_entries() {
    print_step "7/8" "Creating desktop entries..."

    # GUI desktop entry (primary)
    cat > "${DESKTOP_DIR}/${APP_NAME}.desktop" << EOF
[Desktop Entry]
Name=${APP_DISPLAY_NAME}
Comment=Secure P2P Chat with DHT and Onion Routing
Exec=${BIN_DIR}/susurri-gui
Icon=${APP_NAME}
Terminal=false
Type=Application
Categories=Network;Chat;InstantMessaging;Security;
Keywords=chat;p2p;secure;encrypted;privacy;decentralized;
StartupNotify=true
StartupWMClass=Susurri.GUI
EOF

    # Terminal desktop entry
    cat > "${DESKTOP_DIR}/${APP_NAME}-terminal.desktop" << EOF
[Desktop Entry]
Name=${APP_DISPLAY_NAME} (Terminal)
Comment=Secure P2P Chat - Interactive Terminal
Exec=${BIN_DIR}/susurri
Icon=${APP_NAME}
Terminal=true
Type=Application
Categories=Network;Chat;InstantMessaging;Security;
Keywords=chat;p2p;secure;encrypted;privacy;cli;terminal;
StartupNotify=true
EOF

    # Install icon
    cat > "${ICON_DIR}/${APP_NAME}.svg" << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<svg width="256" height="256" viewBox="0 0 256 256" xmlns="http://www.w3.org/2000/svg">
  <defs>
    <linearGradient id="bg" x1="0%" y1="0%" x2="100%" y2="100%">
      <stop offset="0%" style="stop-color:#6366F1"/>
      <stop offset="100%" style="stop-color:#4F46E5"/>
    </linearGradient>
  </defs>
  <rect width="256" height="256" rx="48" fill="url(#bg)"/>
  <path d="M128 48 C72 48 48 96 48 128 C48 160 72 192 128 192 L128 224 L160 192 C200 192 224 160 224 128 C224 96 200 48 128 48 Z"
        fill="white" opacity="0.9"/>
  <circle cx="96" cy="128" r="16" fill="#6366F1"/>
  <circle cx="144" cy="128" r="16" fill="#6366F1"/>
  <circle cx="192" cy="128" r="16" fill="#6366F1"/>
</svg>
EOF

    # Update icon cache
    if command -v gtk-update-icon-cache &> /dev/null; then
        gtk-update-icon-cache -f -t "${HOME}/.local/share/icons/hicolor" 2>/dev/null || true
    fi

    # Update desktop database
    if command -v update-desktop-database &> /dev/null; then
        update-desktop-database "${DESKTOP_DIR}" 2>/dev/null || true
    fi

    print_success "Desktop entries created"
}

# Finalize installation
finalize_installation() {
    print_step "8/8" "Finalizing installation..."

    # Create uninstall script
    cat > "${INSTALL_DIR}/uninstall.sh" << EOF
#!/bin/bash
# Susurri Uninstaller

echo "Uninstalling Susurri..."

rm -rf "${INSTALL_DIR}"
rm -f "${BIN_DIR}/susurri"
rm -f "${BIN_DIR}/susurri-gui"
rm -f "${DESKTOP_DIR}/${APP_NAME}.desktop"
rm -f "${DESKTOP_DIR}/${APP_NAME}-terminal.desktop"
rm -f "${ICON_DIR}/${APP_NAME}.svg"

# Optionally remove config
read -p "Remove configuration files? (y/N): " -n 1 -r
echo
if [[ \$REPLY =~ ^[Yy]$ ]]; then
    rm -rf "${CONFIG_DIR}"
    echo "Configuration removed."
fi

# Update caches
if command -v update-desktop-database &> /dev/null; then
    update-desktop-database "${DESKTOP_DIR}" 2>/dev/null || true
fi
if command -v gtk-update-icon-cache &> /dev/null; then
    gtk-update-icon-cache -f -t "${HOME}/.local/share/icons/hicolor" 2>/dev/null || true
fi

echo "Susurri has been uninstalled."
EOF
    chmod +x "${INSTALL_DIR}/uninstall.sh"

    print_success "Installation finalized"
}

# Print installation summary
print_summary() {
    echo ""
    echo -e "${GREEN}════════════════════════════════════════════════════════════════${NC}"
    echo -e "${GREEN}                    Installation Complete!                       ${NC}"
    echo -e "${GREEN}════════════════════════════════════════════════════════════════${NC}"
    echo ""
    echo -e "  ${BOLD}Installation Details:${NC}"
    echo -e "    Application:     ${INSTALL_DIR}"
    echo -e "    GUI:             ${INSTALL_DIR}/gui"
    echo -e "    Configuration:   ${CONFIG_DIR}"
    echo ""
    echo -e "  ${BOLD}Commands:${NC}"
    echo -e "    ${CYAN}susurri${NC}         - Start CLI (terminal mode)"
    echo -e "    ${CYAN}susurri-gui${NC}     - Start GUI (desktop application)"
    echo ""
    echo -e "  ${BOLD}Desktop Integration:${NC}"
    echo -e "    Search for '${APP_DISPLAY_NAME}' in your application menu"
    echo ""
    echo -e "  ${BOLD}To uninstall:${NC}"
    echo -e "    ${CYAN}${INSTALL_DIR}/uninstall.sh${NC}"
    echo ""
}

# Uninstall function
uninstall() {
    print_banner
    echo -e "${YELLOW}Uninstalling Susurri...${NC}"
    echo ""

    if [ -f "${INSTALL_DIR}/uninstall.sh" ]; then
        bash "${INSTALL_DIR}/uninstall.sh"
    else
        rm -rf "${INSTALL_DIR}"
        rm -f "${BIN_DIR}/susurri"
        rm -f "${BIN_DIR}/susurri-gui"
        rm -f "${DESKTOP_DIR}/${APP_NAME}.desktop"
        rm -f "${DESKTOP_DIR}/${APP_NAME}-terminal.desktop"
        rm -f "${ICON_DIR}/${APP_NAME}.svg"
    fi

    print_success "Susurri has been uninstalled"
}

# Show help
show_help() {
    print_banner
    echo "Usage: $0 [OPTIONS]"
    echo ""
    echo "Options:"
    echo "  --install       Install Susurri (default)"
    echo "  --uninstall     Uninstall Susurri"
    echo "  --system        Install system-wide (requires sudo)"
    echo "  --help          Show this help message"
    echo ""
    echo "Examples:"
    echo "  $0                    # Install for current user"
    echo "  $0 --uninstall        # Remove installation"
    echo "  sudo $0 --system      # System-wide install to /opt"
    echo ""
}

# System-wide installation (requires root)
install_system() {
    if [ "$EUID" -ne 0 ]; then
        print_error "System-wide installation requires root privileges."
        print_info "Run: sudo $0 --system"
        exit 1
    fi

    INSTALL_DIR="/opt/${APP_NAME}"
    BIN_DIR="/usr/bin"
    DESKTOP_DIR="/usr/share/applications"
    ICON_DIR="/usr/share/icons/hicolor/scalable/apps"
    CONFIG_DIR="/etc/${APP_NAME}"

    print_banner
    print_info "Installing system-wide..."

    check_dependencies
    create_directories
    build_cli
    build_gui
    install_files
    create_launchers
    create_desktop_entries
    finalize_installation
    print_summary
}

# Main function
main() {
    case "${1:-}" in
        --uninstall)
            uninstall
            exit 0
            ;;
        --help|-h)
            show_help
            exit 0
            ;;
        --system)
            install_system
            exit 0
            ;;
        --install|"")
            print_banner
            check_distro
            check_dependencies
            create_directories
            build_cli
            build_gui
            install_files
            create_launchers
            create_desktop_entries
            finalize_installation
            print_summary
            ;;
        *)
            print_error "Unknown option: $1"
            show_help
            exit 1
            ;;
    esac
}

# Run main
main "$@"
