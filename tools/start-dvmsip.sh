#!/bin/bash
# start-dvmsip.sh — start dvmsip in the foreground
# Usage: ./start-dvmsip.sh [config-path]

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="${SCRIPT_DIR}/../"
CONFIG="${1:-/etc/dvmsip/dvmsip-config.yml}"
BINARY="${INSTALL_DIR}/dvmsip"

if [ ! -f "${BINARY}" ]; then
    echo "dvmsip binary not found at ${BINARY}" >&2
    exit 1
fi

if [ ! -f "${CONFIG}" ]; then
    echo "Configuration file not found: ${CONFIG}" >&2
    exit 1
fi

exec "${BINARY}" -c "${CONFIG}"
