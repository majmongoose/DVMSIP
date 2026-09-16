#!/bin/bash
# dvmsip-watchdog.sh — restart dvmsip if it exits unexpectedly
# Usage: ./dvmsip-watchdog.sh [config-path]
# Not needed when running under systemd (use Restart=on-failure in the unit file).

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="${SCRIPT_DIR}/../"
CONFIG="${1:-/etc/dvmsip/dvmsip-config.yml}"
BINARY="${INSTALL_DIR}/dvmsip"
RESTART_DELAY=5

echo "[watchdog] Starting dvmsip watchdog"

while true; do
    echo "[watchdog] Starting dvmsip"
    "${BINARY}" -c "${CONFIG}"
    EXIT_CODE=$?

    if [ ${EXIT_CODE} -eq 0 ]; then
        echo "[watchdog] dvmsip exited cleanly — stopping watchdog"
        break
    fi

    echo "[watchdog] dvmsip exited with code ${EXIT_CODE} — restarting in ${RESTART_DELAY}s"
    sleep ${RESTART_DELAY}
done
