#!/usr/bin/env bash
set -euo pipefail

DEPLOY_USER="${DEPLOY_USER:-starbridge-deploy}"
SERVICE_DIR="${STARBRIDGE_SERVICE_DIR:-/opt/starbridge/server}"
CONFIG_DIR="${STARBRIDGE_CONFIG_DIR:-/etc/starbridge}"
ENV_FILE="$CONFIG_DIR/relay.env"
HELPER_FILE="/usr/local/sbin/starbridge-activate-release"
SERVICE_DROPIN_DIR="/etc/systemd/system/starbridge-relay.service.d"
SERVICE_DROPIN_FILE="$SERVICE_DROPIN_DIR/env-file.conf"
SUDOERS_FILE="/etc/sudoers.d/$DEPLOY_USER"

if [[ "$(id -u)" -ne 0 ]]; then
  echo "Run this script as root."
  exit 1
fi

PUBLIC_KEY="${STARBRIDGE_DEPLOY_PUBLIC_KEY:-}"
if [[ -z "$PUBLIC_KEY" && -n "${STARBRIDGE_DEPLOY_PUBLIC_KEY_B64:-}" ]]; then
  PUBLIC_KEY="$(printf '%s' "$STARBRIDGE_DEPLOY_PUBLIC_KEY_B64" | base64 -d)"
fi

if [[ -z "$PUBLIC_KEY" ]]; then
  echo "Set STARBRIDGE_DEPLOY_PUBLIC_KEY or STARBRIDGE_DEPLOY_PUBLIC_KEY_B64."
  exit 1
fi

if ! id "$DEPLOY_USER" >/dev/null 2>&1; then
  useradd --create-home --shell /bin/bash "$DEPLOY_USER"
fi

DEPLOY_HOME="$(getent passwd "$DEPLOY_USER" | cut -d: -f6)"
install -d -m 700 -o "$DEPLOY_USER" -g "$DEPLOY_USER" "$DEPLOY_HOME/.ssh"
touch "$DEPLOY_HOME/.ssh/authorized_keys"
chown "$DEPLOY_USER:$DEPLOY_USER" "$DEPLOY_HOME/.ssh/authorized_keys"
chmod 600 "$DEPLOY_HOME/.ssh/authorized_keys"

if ! grep -Fxq "$PUBLIC_KEY" "$DEPLOY_HOME/.ssh/authorized_keys"; then
  printf '%s\n' "$PUBLIC_KEY" >> "$DEPLOY_HOME/.ssh/authorized_keys"
fi

mkdir -p "$SERVICE_DIR" "$CONFIG_DIR"
chown -R "$DEPLOY_USER:$DEPLOY_USER" "$SERVICE_DIR"
chmod 755 "$SERVICE_DIR"
touch "$ENV_FILE"
chown root:root "$ENV_FILE"
chmod 600 "$ENV_FILE"

ensure_env_default() {
  local key="$1"
  local value="$2"
  if ! grep -q "^$key=" "$ENV_FILE"; then
    printf '%s=%s\n' "$key" "$value" >> "$ENV_FILE"
  fi
}

ensure_env_default ASPNETCORE_URLS http://127.0.0.1:5058
ensure_env_default STARBRIDGE_RELAY_DATA /var/lib/starbridge/relay-state.json

mkdir -p "$SERVICE_DROPIN_DIR"
cat > "$SERVICE_DROPIN_FILE" <<EOF
[Service]
EnvironmentFile=$ENV_FILE
EOF
chown root:root "$SERVICE_DROPIN_FILE"
chmod 644 "$SERVICE_DROPIN_FILE"

cat > "$HELPER_FILE" <<'STARBRIDGE_HELPER'
#!/usr/bin/env bash
set -euo pipefail

ENV_FILE="/etc/starbridge/relay.env"
SERVICE_NAME="starbridge-relay"

if [[ "${1:-}" == "--check" ]]; then
  echo "starbridge deploy helper ready"
  exit 0
fi

release_env="${1:-}"
if [[ -z "$release_env" || ! -f "$release_env" ]]; then
  echo "Usage: starbridge-activate-release /tmp/starbridge-release-<version>.env" >&2
  exit 2
fi

case "$release_env" in
  /tmp/starbridge-release-*.env) ;;
  *)
    echo "Release env must be in /tmp/starbridge-release-*.env" >&2
    exit 2
    ;;
esac

mkdir -p "$(dirname "$ENV_FILE")"
touch "$ENV_FILE"
chown root:root "$ENV_FILE"
chmod 600 "$ENV_FILE"

set_env() {
  local key="$1"
  local value="$2"
  local escaped_value
  escaped_value="$(printf '%s' "$value" | sed 's/[|&]/\\&/g')"

  if grep -q "^$key=" "$ENV_FILE"; then
    sed -i "s|^$key=.*|$key=$escaped_value|" "$ENV_FILE"
  else
    printf '%s=%s\n' "$key" "$value" >> "$ENV_FILE"
  fi
}

while IFS='=' read -r key value || [[ -n "$key" ]]; do
  [[ -z "$key" ]] && continue
  case "$key" in
    STARBRIDGE_LATEST_VERSION|STARBRIDGE_DOWNLOAD_URL|STARBRIDGE_PACKAGE_URL|STARBRIDGE_DOWNLOAD_SHA256|STARBRIDGE_PACKAGE_SHA256|STARBRIDGE_RELEASE_NOTES_B64|STARBRIDGE_UPDATE_REQUIRED)
      set_env "$key" "$value"
      ;;
    *)
      echo "Unsupported release key: $key" >&2
      exit 2
      ;;
  esac
done < "$release_env"

systemctl daemon-reload
systemctl restart "$SERVICE_NAME"

local_manifest="$(mktemp -t starbridge-latest.XXXXXX)"
local_curl_err="$(mktemp -t starbridge-local-curl.XXXXXX)"
trap 'rm -f "$local_manifest" "$local_curl_err"' EXIT

for i in {1..40}; do
  if curl -fs http://127.0.0.1:5058/api/updates/latest >"$local_manifest" 2>"$local_curl_err"; then
    cat "$local_manifest"
    echo
    exit 0
  fi

  if [[ "$i" -eq 40 ]]; then
    echo "Relay did not become ready after restart." >&2
    cat "$local_curl_err" 2>/dev/null >&2 || true
    systemctl status "$SERVICE_NAME" --no-pager -l || true
    journalctl -u "$SERVICE_NAME" -n 80 --no-pager || true
    exit 1
  fi

  sleep 1
done
STARBRIDGE_HELPER

chown root:root "$HELPER_FILE"
chmod 750 "$HELPER_FILE"

cat > "$SUDOERS_FILE" <<EOF
$DEPLOY_USER ALL=(root) NOPASSWD: $HELPER_FILE
EOF
chmod 440 "$SUDOERS_FILE"
visudo -cf "$SUDOERS_FILE" >/dev/null
systemctl daemon-reload

echo "StarBridge deploy user is ready: $DEPLOY_USER"
echo "Service directory owner:"
ls -ld "$SERVICE_DIR"
echo "Relay env file:"
ls -l "$ENV_FILE"
