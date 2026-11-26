# NV-KeepAlive v1.0.0

English | [Русский](README.ru.md)

[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20%7C%20Linux-blue)]()
[![Backends](https://img.shields.io/badge/backends-Vulkan%20Compute%20%7C%20CUDA%20Driver%20API-brightgreen)]()
[![Dependencies](https://img.shields.io/badge/dependencies-zero-success)]()
[![License: MIT](https://img.shields.io/badge/license-MIT-yellow.svg)](LICENSE)

Lightweight, zero-dependency background daemon and Windows 11 Fluent GUI for Windows 10/11 and Linux that prevents idle voltage-drop TDR resets (`nvlddmkm Event ID 14 / 153`) and `BSOD 0x0000010E` (`VIDEO_MEMORY_MANAGEMENT_INTERNAL`) on VRAM-modded and mining NVIDIA GPUs (including `CMP 30HX / 40HX / 50HX / 70HX / 90HX / 170HX`, `P102 / P104 / P106`, and 16 GB / 20 GB / 22 GB VRAM-modified RTX/GTX cards).

---

## Table of Contents

- [Problem Background](#problem-background)
- [Alternative Solutions vs. NV-KeepAlive](#alternative-solutions-vs-nv-keepalive)
- [Architecture: Single Binary with Vulkan and CUDA Backends](#architecture-single-binary-with-vulkan-and-cuda-backends)
- [Measured Impact](#measured-impact)
- [Windows Installation and GUI](#windows-installation-and-gui)
- [Linux Installation](#linux-installation)
- [Multi-GPU and Per-GPU Backends](#multi-gpu-and-per-gpu-backends)
- [PCIe Gen2 Unlock Synchronization and Chipset Bridge Fix (CMP 40HX)](#pcie-gen2-unlock-synchronization-and-chipset-bridge-fix-cmp-40hx)
- [Building from Source](#building-from-source)
- [CLI Reference](#cli-reference)
- [Troubleshooting](#troubleshooting)
- [License](#license)

---

## Problem Background

VRAM-modded NVIDIA cards (for example, 16 GB CMP 40HX, 20 GB RTX 3080, or 22 GB RTX 2080 Ti) and certain mining GPUs run stable under load, but drop into deep low-voltage P-states (such as ~760 mV) when idle. Because aftermarket GDDR6 memory straps or VBIOS power tables are not calibrated for those low-voltage transitions:

1. On patched drivers (`dartraiden/NVIDIA-patcher`), Windows triggers cyclic WDDM Timeout Detection and Recovery (`nvlddmkm Event ID 14` or `153`) roughly every 14-20 seconds during idle, causing UI stutters and invalidating active CUDA/Vulkan contexts.
2. When compute/ReBAR unlock is active (such as `Cyridd/cmpunlocker` for CMP 40HX), an idle TDR frequently escalates directly into a kernel stop error: `BSOD 0x0000010E` (`VIDEO_MEMORY_MANAGEMENT_INTERNAL`).
3. A minimal continuous compute heartbeat keeps the GPU out of the unstable idle voltage state without holding significant VRAM or interfering with real workloads.

---

## Alternative Solutions vs. NV-KeepAlive

Another well-known workaround on Windows is **locking GPU voltage/frequency** via GUI tuning tools such as **EVGA Precision X1** (Voltage/Boost Lock) or **MSI Afterburner** (VF-curve lock via `Ctrl+L`).

| Approach | How It Works | Limitations |
|---|---|---|
| **EVGA Precision X1 / MSI Afterburner (Voltage Lock)** | Locks GPU voltage/frequency to a stable point on the VF curve so the card never drops into unstable idle voltage. | Runs in user GUI session only (does not protect Windows login screen, before user logon, or headless sessions); requires keeping a third-party monitoring utility running; Windows-only (no Linux support); some CMP/patched drivers do not expose full VF-curve controls. |
| **NV-KeepAlive (System Service)** | Dispatches a tiny 512-byte compute heartbeat from Windows `Session 0 (SYSTEM)` or Linux `systemd`, and yields completely via NVML when real GPU work starts. | Consumes ~5-20 MiB VRAM per GPU in Vulkan mode (or ~200-300 MiB in CUDA mode); keeps card slightly warmer at idle than deep P8 sleep. Works before Windows login, survives logoff, runs on Linux, requires zero external runtime or tray apps. |

If voltage locking in EVGA Precision X1 or MSI Afterburner already works reliably for your desktop workflow and you do not need pre-login or Linux protection, it is a valid alternative. NV-KeepAlive is designed for setups where pre-login stability, headless/server operation, multi-GPU control, or minimal memory footprint is required.

---

## Architecture: Single Binary with Vulkan and CUDA Backends

Both compute backends are compiled into the **same executable** (`NvKeepAlive.exe` on Windows, `nv-keepalive` on Linux). There is no separate "CUDA build" to download, and no CUDA Toolkit or Vulkan SDK is required at build time or runtime:

1. **Vulkan Compute (recommended for most systems):**
   - Loads `vulkan-1.dll` (Windows) or `libvulkan.so.1` (Linux) dynamically.
   - Compiles an embedded ~350-byte SPIR-V 1.0 compute shader in memory (standard FP32 FMA loop, no `VK_NV_cooperative_matrix2` dependency).
   - Allocates a 512-byte storage buffer; total driver overhead is **~5-20 MiB VRAM per GPU**.
2. **CUDA Driver API (for systems without Vulkan or with unstable Vulkan ICD):**
   - Loads `nvcuda.dll` (Windows) or `libcuda.so.1` (Linux) dynamically.
   - JIT-compiles an embedded PTX 6.0 (`sm_50+`) kernel via `cuModuleLoadData`.
   - Total CUDA context overhead is **~200-300 MiB VRAM per GPU**.

Both backends share the same core protections:
- **NVML External-Load Detector (250 ms polling):** queries `nvmlDeviceGetUtilizationRates` per physical GPU (matched by PCI Bus ID). When external VRAM controller utilization reaches `>= 5%` (LLM inference, PyTorch/ComfyUI, games, encoding), the worker stops dispatching completely (`0.00%` daemon CPU, zero Vulkan/CUDA context switching) and resumes automatically once the workload ends.
- **Canonical PCI Bus ID Sorting:** enumerates NVIDIA GPUs (`vendorID == 0x10DE`, ignoring AMD/Intel) and sorts them by PCI Bus ID (`VK_EXT_pci_bus_info`, `cuDeviceGetPCIBusId`, `nvmlDeviceGetHandleByPciBusId_v2`) so GPU indices match `nvidia-smi` across both APIs.
- **Graceful Windows Shutdown Handling:** registers a Win32 `SetConsoleCtrlHandler` for `CTRL_SHUTDOWN_EVENT` with top shutdown priority (`SetProcessShutdownParameters(0x3FF, 0)`), releasing GPU contexts cleanly before `dxgmms2`/`nvlddmkm` teardown. This prevents shutdown delays and shutdown-time `BSOD 0x10E (0x2D)` minidumps.
- **Clean Boot Timeout (`--wait-timeout 30`):** waits up to 30 seconds at boot for an NVIDIA GPU; if no NVIDIA GPU is present, exits cleanly with code `0`.

---

## Measured Impact

Hardware tested: `NVIDIA CMP 40HX 16GB` (EFI-unlocked, driver `581.94`, Ryzen 5 5600, Windows 11).

| Metric | Without KeepAlive | CUDA Backend | Vulkan Backend |
|---|---:|---:|---:|
| Idle VRAM overhead | 0 MiB | +200-300 MiB | +5-13 MiB |
| Idle `nvlddmkm` TDRs / `BSOD 0x10E` | ~35-45 per 2 min / BSOD | 0 | 0 |
| Daemon CPU usage (12-thread CPU) | - | ~0.28% idle / 0.00% load | ~0.18% idle / 0.00% load |
| `Qwen2.5-Coder-7B` CUDA 12 (`pp512 / tg64`) | crashes | - | 1746 / 70.3 t/s (no regression) |
| `ComfyUI PyTorch` FP16 GEMM / SDXL Proxy | crashes | - | 45.6 TFLOPS / 7.74 it/s |

---

## Windows Installation and GUI

1. Download and extract **`NV-KeepAlive-Windows-v1.0.0.zip`** from the Releases page.
2. Double-click **`NvKeepAliveUI.exe`** (~68 KB native WPF Windows 11 Fluent Dark manager, zero external runtime dependencies).
3. In the window:
   - Toggle language between **RU** and **EN** in the top-right corner (auto-detected from Windows locale on startup).
   - Review detected NVIDIA GPUs and live telemetry (`PCI Bus ID`, `PCIe link`, `VRAM`, temperature, power, live daemon `CPU%`, `TDR (5m)` and `Since start`).
   - Select **Vulkan** or **CUDA** per GPU (explicit selection, no silent fallback). If Vulkan is selected but `vulkan-1.dll` or the NVIDIA Vulkan ICD is missing, an installation guide dialog opens with direct links (LunarG Vulkan Runtime / NVIDIA driver), a live **Recheck** button, and a one-click **CUDA for all** switch.
   - Click **Install** (or **Reinstall** after changing settings) to deploy `NvKeepAlive.exe` to `%ProgramData%\NV-KeepAlive\` and register the `NV-KeepAlive` Scheduled Task (`SYSTEM`, `AtStartup` + `AtLogOn`).
   - Click **Test 25s** to verify 0 `nvlddmkm` TDR errors over 25 seconds.
   - Optional (shown only when a CMP GPU is selected): toggle **`GGML_VK_DISABLE_COOPMAT2`** and click **Apply** (needed only for stock CMP cards without EFI unlock in `llama.cpp` / LM Studio; turn off after EFI unlock).

---

## Linux Installation

```bash
cd linux
sudo bash install-linux.sh
```

The installer:
1. Installs the prebuilt `linux/bin/nv-keepalive-linux-x86_64` binary if compatible with the host libc, or compiles `linux/nv_keepalive.c` locally using `gcc` / `clang` (no Vulkan or CUDA SDK headers required).
2. Prompts for an explicit backend choice (`[1] Vulkan` or `[2] CUDA`). If Vulkan is chosen but `libvulkan.so.1` or the NVIDIA Vulkan ICD is missing, prints distro-specific install commands (`apt`, `pacman`, `dnf`) and lets you recheck or switch to CUDA.
3. On multi-GPU hosts, lets you select specific GPUs or assign different backends per GPU (for example `0:cuda,1:vulkan`).
4. Installs and starts `nv-keepalive.service` (`Restart=on-failure`).

Non-interactive installation examples:

```bash
sudo bash install-linux.sh --targets 0:vulkan
sudo bash install-linux.sh --targets 0:cuda,1:vulkan
```

Service management:

```bash
nv-keepalive --status
systemctl status nv-keepalive
sudo touch /run/nv-keepalive.pause   # pause (releases GPU context)
sudo rm -f /run/nv-keepalive.pause   # resume
sudo bash uninstall-linux.sh         # complete removal
```

---

## Multi-GPU and Per-GPU Backends

A single `NvKeepAlive` process can drive multiple NVIDIA GPUs simultaneously, with an independent backend per card:

```text
NvKeepAlive.exe --targets 0:cuda,1:vulkan --quiet --log "%ProgramData%\NV-KeepAlive\keepalive.log"
```

- When a system has **one** NVIDIA GPU, `NvKeepAliveUI.exe` hides the GPU checkbox and displays only the `Vulkan | CUDA` toggle.
- When a system has **two or more** NVIDIA GPUs (for example, `RTX 4070` + `CMP 40HX`), checkboxes allow enabling protection only on the cards that need it while leaving primary gaming GPUs untouched.
- GPU indices are sorted by PCI Bus ID so `--targets` matches `nvidia-smi` ordering.

---

## PCIe Gen2 Unlock Synchronization and Chipset Bridge Fix (CMP 40HX)

`cmpunlocker` (`https://github.com/Cyridd/cmpunlocker`) is an independent third-party project specifically for `NVIDIA CMP 40HX` (`DEV=0x1F0B`) that unlocks full FP16/Tensor Cores, 8 GiB Resizable BAR, and PCIe Gen2. NV-KeepAlive does **not** bundle any third-party unlocker binaries or drivers.

When a `CMP 40HX` (`DEV=0x1F0B`) is selected in `NvKeepAliveUI.exe`, the **PCIe Gen2 Unlock Sync** panel becomes visible (with a `?` button showing install status and opening the author's release page):

1. **Automatic Process Guard:** if `NvKeepAlive.exe` detects a running `40HXInstaller.exe` process, it automatically pauses and releases the GPU context until the unlocker finishes.
2. **Task Synchronization + Wake from Sleep:** clicking **Sync now** backs up the existing `40HX PCIe Gen2 Bring-up` Scheduled Task (`gen2-orig.xml`) and wraps it in `%ProgramData%\NV-KeepAlive\gen2-sync-wrapper.cmd` (pause KeepAlive -> run original `40HXInstaller.exe` -> run `--retrain-gen2` -> resume KeepAlive), while adding a wake-from-sleep trigger (`Power-Troubleshooter Event ID 1`). Clicking **Restore** cleanly restores the original task.
3. **Built-in Chipset PCIe Bridge Retrain (`--retrain-gen2`):** when a `CMP 40HX` is installed in a secondary chipset PCIe slot behind a PCIe switch (common on B550/X570/Z690 boards, where the parent bridge is not on Bus 0), `40HXInstaller` unlocks GPU capability (`max=Gen2`) but may leave the link trained at `Gen1`. Our `--retrain-gen2` routine scans PCI buses `0..255` via the user's already-installed `WinRing0x64.sys` from `CMP40HX-Unlock`, finds the parent PCIe bridge (`HeaderType==1 && SecondaryBus==gpuBus`), sets `TLS=Gen2`, and triggers `Link Retrain` (`LNKCTL |= 0x20`). If the card is already at Gen2, it exits immediately without writing.

---

## Building from Source

### Windows (no Visual Studio or .NET SDK required)

Uses the built-in Windows `.NET Framework 4` compiler (`csc.exe`):

```cmd
windows\src\build-windows.bat
```

Outputs `windows\NvKeepAlive.exe` (~65 KB) and `windows\NvKeepAliveUI.exe` (~68 KB).

### Linux (no Vulkan or CUDA SDK headers required)

```bash
cd linux
make
```

Outputs `linux/nv-keepalive`.

---

## CLI Reference

| Flag | Default | Description |
|---|---|---|
| `--targets <spec>` | *(none)* | Per-GPU backend list, e.g. `0:vulkan` or `0:cuda,1:vulkan` |
| `--backend <mode>` | `vulkan` | `vulkan` or `cuda` (used when `--targets` is omitted) |
| `--device <spec>` | `auto` | `auto` (mining cards, else GPU 0), `all`, or `0,1` |
| `--list` | - | Print detected NVIDIA GPUs (sorted by PCI Bus ID) and exit |
| `--list-tsv` | - | Machine-readable tab-separated GPU list |
| `--check-vulkan` | - | Print `OK <count>`, `NO_LOADER`, or `NO_NVIDIA_ICD` and exit |
| `--status` | - | Print service state, Vulkan status, GPUs, and recent log |
| `--retrain-gen2` | - | *(Windows)* Retrain parent PCIe bridge to Gen2 for CMP 40HX in chipset slots |
| `--dry-run` | off | *(Windows)* With `--retrain-gen2`: inspect GPU and parent bridge link status only |
| `--wait <sec>` | `25` | *(Windows)* With `--retrain-gen2`: wait up to N seconds for GPU `max=Gen2` |
| `--driver <path>` | `C:\ProgramData\40HXUnlock\drivers\WinRing0x64.sys` | *(Windows)* Custom `WinRing0x64.sys` path for `--retrain-gen2` |
| `--blocks <N>` | `2` | Compute workgroups / CUDA grid blocks |
| `--threads <N>` | `64` | Threads per workgroup / CUDA block |
| `--iters <N>` | `450000` (VK) / `120000` (CU) | FMA loop iterations per dispatch |
| `--sleep <ms>` | `0` | Extra idle sleep between dispatches |
| `--yield-ms <ms>` | `150` | Adaptive yield sleep when GPU latency spikes |
| `--no-adaptive` | off | Disable NVML and timing-based yield |
| `--pause-file <path>` | `<exe_dir>\keepalive.pause` / `/run/nv-keepalive.pause` | Pause flag file path |
| `--log <path>` | *(none)* | Event-only log file (100 KB rotation) |
| `--sec <N>` | `0` | Run for N seconds and exit (`0` = forever) |
| `--wait-timeout <N>` | `30` | Seconds to wait for NVIDIA GPU at boot before exiting cleanly |
| `--no-mutex` / `--no-lock` | off | Allow multiple instances (for testing) |
| `--quiet` | off | Suppress normal stdout logging |

---

## Troubleshooting

**Vulkan reports `NO_LOADER` or `NO_NVIDIA_ICD`:**
- Windows: install the official LunarG Vulkan Runtime (`https://vulkan.lunarg.com/sdk/home#windows`) or reinstall your NVIDIA driver with Vulkan enabled (if using NVCleanstall, keep Vulkan checked).
- Linux: install `libvulkan1` (Debian/Ubuntu), `vulkan-icd-loader` (Arch), or `vulkan-loader` (Fedora/RHEL), and verify `/usr/share/vulkan/icd.d/nvidia_icd.json` exists.
- Alternatively, select the `CUDA` backend for affected GPUs.

**`llama.cpp` / LM Studio crashes with Vulkan on a stock CMP card (without EFI unlock):**
- Enable `GGML_VK_DISABLE_COOPMAT2` in `NvKeepAliveUI.exe` and click **Apply**, then restart LM Studio / `llama.cpp`.
- After applying EFI unlock, turn `GGML_VK_DISABLE_COOPMAT2` back off so Tensor Cores are utilized.

**`NvKeepAliveUI.exe` shows `CPU: -`:**
- Run `NvKeepAliveUI.exe` as Administrator (UAC prompt) so Windows allows reading `TotalProcessorTime` from the `SYSTEM` service process.

---

## License

[MIT License](LICENSE)
