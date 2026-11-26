#!/usr/bin/env bash
set -euo pipefail

if [[ $EUID -ne 0 ]]; then
    echo "Please run as root: sudo bash $0"
    exit 1
fi

echo "Stopping and removing nv-keepalive.service..."
systemctl disable --now nv-keepalive.service 2>/dev/null || true
rm -f /etc/systemd/system/nv-keepalive.service
rm -f /usr/local/bin/nv-keepalive /usr/local/bin/nv-keepalive-tui
rm -f /tmp/nv-keepalive.lock /run/nv-keepalive.pause
systemctl daemon-reload
echo "Done. NV-KeepAlive removed."
