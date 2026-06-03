#!/usr/bin/env bash
set -euo pipefail

SERVICE_DIR="/opt/starbridge/server"
DATA_DIR="/var/lib/starbridge"
SERVICE_FILE="/etc/systemd/system/starbridge-relay.service"
DLL_NAME="Star Bridge Relay Server.dll"
RELAY_KEY="${STARBRIDGE_RELAY_KEY:-}"

if [[ -z "$RELAY_KEY" ]]; then
  echo "Set STARBRIDGE_RELAY_KEY before running this script."
  echo "Example: STARBRIDGE_RELAY_KEY=your-key bash scripts/install-starbridge-relay-service.sh"
  exit 1
fi

if [[ ! -f "$SERVICE_DIR/$DLL_NAME" ]]; then
  echo "Missing $SERVICE_DIR/$DLL_NAME"
  echo "Upload the published server files to $SERVICE_DIR first."
  exit 1
fi

mkdir -p "$DATA_DIR"

cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=Star Bridge Relay Server
After=network-online.target
Wants=network-online.target

[Service]
WorkingDirectory=$SERVICE_DIR
ExecStart=/usr/bin/dotnet "$SERVICE_DIR/$DLL_NAME"
Restart=always
RestartSec=5
Environment=ASPNETCORE_URLS=http://0.0.0.0:5058
Environment=STARBRIDGE_RELAY_DATA=$DATA_DIR/relay-state.json
Environment=STARBRIDGE_RELAY_KEY=$RELAY_KEY

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable starbridge-relay
systemctl restart starbridge-relay
systemctl status starbridge-relay --no-pager
