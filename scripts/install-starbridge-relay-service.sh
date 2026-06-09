#!/usr/bin/env bash
set -euo pipefail

SERVICE_DIR="/opt/starbridge/server"
DATA_DIR="/var/lib/starbridge"
CONFIG_DIR="/etc/starbridge"
ENV_FILE="$CONFIG_DIR/relay.env"
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

mkdir -p "$DATA_DIR" "$CONFIG_DIR"

cat > "$ENV_FILE" <<EOF
ASPNETCORE_URLS=http://127.0.0.1:5058
STARBRIDGE_RELAY_DATA=$DATA_DIR/relay-state.json
STARBRIDGE_RELAY_KEY=$RELAY_KEY
STARBRIDGE_SMTP_HOST=${STARBRIDGE_SMTP_HOST:-}
STARBRIDGE_SMTP_PORT=${STARBRIDGE_SMTP_PORT:-587}
STARBRIDGE_SMTP_USER=${STARBRIDGE_SMTP_USER:-}
STARBRIDGE_SMTP_PASS=${STARBRIDGE_SMTP_PASS:-}
STARBRIDGE_SMTP_FROM=${STARBRIDGE_SMTP_FROM:-}
STARBRIDGE_SMTP_SSL=${STARBRIDGE_SMTP_SSL:-true}
STARBRIDGE_LATEST_VERSION=${STARBRIDGE_LATEST_VERSION:-0.3.7}
STARBRIDGE_DOWNLOAD_URL=${STARBRIDGE_DOWNLOAD_URL:-}
STARBRIDGE_PACKAGE_URL=${STARBRIDGE_PACKAGE_URL:-}
STARBRIDGE_RELEASE_NOTES=${STARBRIDGE_RELEASE_NOTES:-}
STARBRIDGE_UPDATE_REQUIRED=${STARBRIDGE_UPDATE_REQUIRED:-false}
EOF

chown root:root "$ENV_FILE"
chmod 600 "$ENV_FILE"

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
EnvironmentFile=$ENV_FILE

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable starbridge-relay
systemctl restart starbridge-relay
systemctl status starbridge-relay --no-pager
