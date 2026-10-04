#!/usr/bin/env bash
# Installs Leona for the current user: a systemd user service that starts at login (and restarts after a
# crash), and the GNOME top bar indicator. No sudo needed. Run again after moving the repository.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
backend="$repo/backend"
units="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
extensions="${XDG_DATA_HOME:-$HOME/.local/share}/gnome-shell/extensions"
uuid="leona@leona.local"
dotnet="$(command -v dotnet || true)"

if [[ -z "$dotnet" ]]; then
    echo "dotnet was not found. Install the .NET SDK first." >&2
    exit 1
fi

echo "Building the backend and the web app…"
dotnet build "$backend" -nologo -v q
if [[ ! -f "$repo/frontend/dist/index.html" ]]; then
    echo "frontend/dist is missing: run 'npm run build' in frontend first." >&2
    exit 1
fi

mkdir -p "$units"
cat > "$units/leona.service" <<UNIT
[Unit]
Description=Leona local AI workspace
After=network-online.target

[Service]
WorkingDirectory=$backend
Environment=Remote__Enabled=true
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
ExecStart=$dotnet $backend/bin/Debug/net10.0/Harness.dll
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
UNIT

# The photo service, when its Python environment is set up (see README, Photos).
if [[ -x "$repo/photo/.venv/bin/python" ]]; then
    cat > "$units/leona-photo.service" <<UNIT
[Unit]
Description=Leona photo editing
After=network-online.target

[Service]
WorkingDirectory=$repo/photo
Environment=PHOTO_ROOT=$backend/uploads
ExecStart=$repo/photo/.venv/bin/python $repo/photo/server.py
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
UNIT
fi

systemctl --user daemon-reload
systemctl --user enable leona.service
echo "Service installed: starts at login. Start it now with: systemctl --user start leona"
if [[ -f "$units/leona-photo.service" ]]; then
    systemctl --user enable leona-photo.service
    echo "Photo service installed. Start it now with: systemctl --user start leona-photo"
fi

# Top bar indicators: Leona, and Tailscale (status, on/off and the tailnet's devices).
mkdir -p "$extensions"
for id in "$uuid" tailscale@leona.local; do
    rm -rf "${extensions:?}/${id:?}"
    cp -r "$repo/desktop/gnome-extension/$id" "$extensions/$id"
    if gnome-extensions enable "$id" 2>/dev/null; then
        echo "Top bar indicator $id enabled."
    else
        # GNOME on Wayland only finds new extensions after logging in again.
        gsettings set org.gnome.shell enabled-extensions \
            "$(python3 -c "import ast,sys; e=ast.literal_eval(sys.argv[1] or '[]'); e+=[] if sys.argv[2] in e else [sys.argv[2]]; print(e)" \
            "$(gsettings get org.gnome.shell enabled-extensions | sed 's/^@as //')" "$id")"
        echo "Top bar indicator $id installed. Log out and in once to see it."
    fi
done
if command -v tailscale >/dev/null && ! tailscale debug prefs 2>/dev/null | grep -q "\"OperatorUser\": \"$USER\""; then
    echo "To switch Tailscale on and off from the top bar, run once: sudo tailscale set --operator=$USER"
fi
