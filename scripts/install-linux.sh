#!/bin/sh
# Installs avamux for the current user: a release build in ~/.local/lib/avamux, the `avamux` command in
# ~/.local/bin, the icon, and an application-menu entry. Run it again after changing the code to update.
set -eu
# The physical path: where /home is a link (to /var/home, say), a build that sees the repository under both names
# mixes them up and fails.
repo="$(cd "$(dirname "$0")/.." && pwd -P)"
lib="$HOME/.local/lib/avamux"
data="${XDG_DATA_HOME:-$HOME/.local/share}"

# Publish next to the old copy and swap, so a running instance keeps working until it is restarted.
rm -rf "$lib.new"
dotnet publish "$repo/src/Avamux" -c Release -o "$lib.new" --nologo -v quiet
rm -rf "$lib.old"
[ -d "$lib" ] && mv "$lib" "$lib.old"
mv "$lib.new" "$lib"
rm -rf "$lib.old"

mkdir -p "$HOME/.local/bin" "$data/applications"
ln -sf "$lib/avamux" "$HOME/.local/bin/avamux"

for size in 48 64 128 256 512; do
  dir="$data/icons/hicolor/${size}x${size}/apps"
  mkdir -p "$dir"
  if command -v magick >/dev/null 2>&1; then magick "$repo/src/Avamux/Assets/avamux.png" -resize "${size}x${size}" "$dir/avamux.png"
  elif [ "$size" = 512 ]; then cp "$repo/src/Avamux/Assets/avamux.png" "$dir/avamux.png"; fi
done

cat > "$data/applications/avamux.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=avamux
GenericName=Terminal Multiplexer
Comment=Terminals, watches, events, a dashboard, logs, a wiki and repositories in one window
Exec=$lib/avamux
Path=$HOME
Icon=avamux
Terminal=false
Categories=System;TerminalEmulator;
Keywords=terminal;tmux;shell;wiki;git;
StartupNotify=true
StartupWMClass=avamux
DESKTOP

command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$data/applications" >/dev/null 2>&1 || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t "$data/icons/hicolor" >/dev/null 2>&1 || true
command -v kbuildsycoca6 >/dev/null 2>&1 && kbuildsycoca6 >/dev/null 2>&1 || true
echo "Installed avamux to $lib (command: ~/.local/bin/avamux, menu entry: avamux)."
