#!/bin/bash
set -e

dotnet publish -c Release -r linux-x64 --self-contained -o "$HOME/.local/share/screen-grab"

mkdir -p "$HOME/.local/share/applications"

# App icon for the Mint menu, the tray and other launchers.
ICON_DIR="$HOME/.local/share/icons/hicolor/256x256/apps"
mkdir -p "$ICON_DIR"
cp resources/icon.png "$ICON_DIR/screen-grab.png"

cat > "$HOME/.local/share/applications/screen-grab.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Screen Grab
GenericName=Screenshot Tool
Comment=Take screenshots with Print Screen, even while a menu is open
Exec=$HOME/.local/share/screen-grab/screengrab
Path=$HOME/.local/share/screen-grab
Icon=screen-grab
Terminal=false
Categories=Graphics;Utility;
Keywords=screenshot;screen;capture;grab;print;menu;clipboard;
StartupNotify=true
DESKTOP

chmod +x "$HOME/.local/share/screen-grab/screengrab"
chmod +x "$HOME/.local/share/applications/screen-grab.desktop"
update-desktop-database "$HOME/.local/share/applications"
gtk-update-icon-cache -q -t "$HOME/.local/share/icons/hicolor" 2>/dev/null || true

if [ "$XDG_SESSION_TYPE" != "x11" ]; then
    echo "Warning: this is a $XDG_SESSION_TYPE session. Screen Grab works in X11 sessions only."
fi
