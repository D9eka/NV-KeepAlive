#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TARGET_BIN="/usr/local/bin/nv-keepalive"
TUI_DST="/usr/local/bin/nv-keepalive-tui"
PREBUILT_BIN="${SCRIPT_DIR}/bin/nv-keepalive-linux-x86_64"
UNIT_DST="/etc/systemd/system/nv-keepalive.service"

CLI_BACKEND=""
CLI_TARGETS=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --backend) CLI_BACKEND="${2:-}"; shift 2 ;;
        --targets) CLI_TARGETS="${2:-}"; shift 2 ;;
        *) echo "Unknown option: $1 (supported: --backend vulkan|cuda, --targets 0:vulkan,1:cuda)"; exit 1 ;;
    esac
done

if [[ $EUID -ne 0 ]]; then
    echo "Please run as root: sudo bash $0"
    exit 1
fi

ARCH="$(uname -m)"
INSTALLED_MODE=""

echo "[1/4] Installing nv-keepalive binary (arch=${ARCH})..."
if [[ "${ARCH}" == "x86_64" && -f "${PREBUILT_BIN}" ]]; then
    chmod 755 "${PREBUILT_BIN}" || true
    if "${PREBUILT_BIN}" --help >/dev/null 2>&1; then
        cp -f "${PREBUILT_BIN}" "${TARGET_BIN}"
        chmod 755 "${TARGET_BIN}"
        INSTALLED_MODE="prebuilt"
        echo "  [OK] Using prebuilt linux-x86_64 binary."
    else
        echo "  [i] Prebuilt binary incompatible with local libc; compiling from source..."
    fi
fi

if [[ -z "${INSTALLED_MODE}" ]]; then
    CC_BIN=""
    if command -v gcc >/dev/null 2>&1; then CC_BIN="gcc"
    elif command -v cc >/dev/null 2>&1; then CC_BIN="cc"
    elif command -v clang >/dev/null 2>&1; then CC_BIN="clang"
    else
        echo "Error: No compatible prebuilt binary and no C compiler (gcc/clang) found."
        echo "Install gcc (e.g. 'sudo apt install gcc' or 'sudo pacman -S gcc') and retry."
        exit 1
    fi
    echo "  Compiling nv_keepalive.c with ${CC_BIN} (zero SDK headers required)..."
    "${CC_BIN}" -O2 -Wall -Wextra -pthread "${SCRIPT_DIR}/nv_keepalive.c" -ldl -o "${TARGET_BIN}"
    chmod 755 "${TARGET_BIN}"
    INSTALLED_MODE="compiled (${CC_BIN})"
fi

echo ""
echo "[2/4] Detecting NVIDIA GPUs and Vulkan availability..."
GPU_LINES="$("${TARGET_BIN}" --list-tsv 2>/dev/null || true)"
GPU_COUNT=0
if [[ -n "${GPU_LINES}" ]]; then
    GPU_COUNT="$(printf '%s\n' "${GPU_LINES}" | wc -l)"
    "${TARGET_BIN}" --list || true
else
    echo "  [WARN] No NVIDIA GPUs currently detected. Service will still be configured."
fi

show_vulkan_guide() {
    local status="$1"
    echo ""
    echo "-------------------------------------------------------------------"
    echo "Vulkan Runtime is not available (${status})."
    echo "To use the lightweight Vulkan Compute backend (~5-20 MiB VRAM):"
    echo "  Debian / Ubuntu: sudo apt update && sudo apt install -y libvulkan1"
    echo "  Arch Linux:      sudo pacman -S vulkan-icd-loader"
    echo "  Fedora / RHEL:   sudo dnf install -y vulkan-loader"
    echo "Also ensure your NVIDIA driver includes the Vulkan ICD (nvidia_icd.json)."
    echo "-------------------------------------------------------------------"
}

TARGETS_ARG="${CLI_TARGETS}"
if [[ -z "${TARGETS_ARG}" ]]; then
    BACKEND="${CLI_BACKEND}"
    while [[ -z "${BACKEND}" ]]; do
        echo ""
        echo "Select compute backend (explicit choice):"
        echo "  [1] Vulkan Compute  - ~5-20 MiB VRAM per GPU (recommended)"
        echo "  [2] CUDA Driver API - ~200-300 MiB VRAM per GPU (if Vulkan is missing/unstable)"
        read -rp "Enter choice [1/2]: " ch
        case "${ch}" in
            1|vulkan|Vulkan)
                VK_OUT="$("${TARGET_BIN}" --check-vulkan 2>/dev/null || true)"
                if [[ "${VK_OUT}" == OK* ]]; then
                    BACKEND="vulkan"
                else
                    show_vulkan_guide "${VK_OUT:-NO_LOADER}"
                    read -rp "Choose: [r] Recheck Vulkan, [c] Switch to CUDA, [q] Quit: " act
                    case "${act}" in
                        c|C) BACKEND="cuda" ;;
                        q|Q) exit 1 ;;
                        *) ;;
                    esac
                fi
                ;;
            2|cuda|CUDA)
                BACKEND="cuda"
                ;;
            *) echo "Invalid selection. Enter 1 or 2." ;;
        esac
    done

    if [[ "${GPU_COUNT}" -le 1 ]]; then
        TARGETS_ARG="0:${BACKEND}"
    else
        echo ""
        echo "Multiple NVIDIA GPUs detected (${GPU_COUNT})."
        echo "Enter comma-separated GPU indices (e.g. '0,1' or '1'), 'all', or explicit per-GPU targets (e.g. '0:cuda,1:vulkan'):"
        read -rp "Selection [all]: " sel
        sel="${sel:-all}"
        if [[ "${sel}" == *":"* ]]; then
            TARGETS_ARG="${sel}"
        elif [[ "${sel}" == "all" ]]; then
            TARGETS_ARG=""
            for ((i=0; i<GPU_COUNT; i++)); do
                TARGETS_ARG+="${TARGETS_ARG:+,}${i}:${BACKEND}"
            done
        else
            TARGETS_ARG=""
            IFS=',' read -ra IDXS <<< "${sel}"
            for idx in "${IDXS[@]}"; do
                idx="${idx// /}"
                [[ -n "${idx}" ]] && TARGETS_ARG+="${TARGETS_ARG:+,}${idx}:${BACKEND}"
            done
        fi
    fi
fi

echo ""
echo "[3/4] Installing systemd service (targets=${TARGETS_ARG})..."
if [[ -f "${SCRIPT_DIR}/nv-keepalive-tui" ]]; then
    cp -f "${SCRIPT_DIR}/nv-keepalive-tui" "${TUI_DST}"
    chmod 755 "${TUI_DST}"
fi
sed "s|--targets 0:vulkan|--targets ${TARGETS_ARG}|g" "${SCRIPT_DIR}/nv-keepalive.service" > "${UNIT_DST}"
systemctl daemon-reload
systemctl enable --now nv-keepalive.service

sleep 1
echo ""
echo "[4/4] Status (${INSTALLED_MODE}):"
"${TARGET_BIN}" --status || true
echo ""
systemctl --no-pager status nv-keepalive.service || true
echo ""
echo "Useful commands:"
echo "  sudo nv-keepalive-tui                # interactive TUI manager"
echo "  nv-keepalive --status"
echo "  sudo touch /run/nv-keepalive.pause   # pause"
echo "  sudo rm -f /run/nv-keepalive.pause   # resume"
echo "  sudo bash ${SCRIPT_DIR}/uninstall-linux.sh"
