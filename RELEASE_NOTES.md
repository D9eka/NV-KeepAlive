## NV-KeepAlive v1.0.0

First public release of **NV-KeepAlive** — a lightweight, zero-dependency background daemon and Windows 11 Fluent Dark GUI for Windows 10/11 and Linux that eliminates idle voltage-drop TDR resets (`nvlddmkm Event ID 14 / 153`) and `BSOD 0x0000010E` (`VIDEO_MEMORY_MANAGEMENT_INTERNAL`) on VRAM-modded and mining NVIDIA GPUs (`CMP 30/40/50/70/90/170HX`, `P102/P104/P106`, and 16 GB / 20 GB / 22 GB modded RTX/GTX cards).

### Highlights

- **Single binary with dual compute backends (zero SDK dependencies):**
  - **Vulkan Compute** (`~5-20 MiB VRAM` per GPU) via dynamic `vulkan-1.dll` / `libvulkan.so.1` and an embedded ~350-byte SPIR-V 1.0 kernel.
  - **CUDA Driver API** (`~200-300 MiB VRAM` per GPU) via dynamic `nvcuda.dll` / `libcuda.so.1` and an embedded PTX 6.0 (`sm_50+`) kernel.
- **Native Windows 11 Fluent Dark GUI (`windows\NvKeepAliveUI.exe`, ~68 KB):**
  - Built-in **EN / RU** language switch, live `nvidia-smi` telemetry (PCI Bus ID, PCIe link, VRAM, temperature, power), live daemon `CPU%`, and short-window `nvlddmkm` TDR counters (`5m` and `Since start`).
  - Explicit per-GPU backend selection (`Vulkan | CUDA`, no silent fallback) with a built-in **Vulkan Runtime installation guide** dialog and live **Recheck** button if `vulkan-1.dll` or NVIDIA Vulkan ICD is missing.
  - Context-sensitive CMP controls (shown only when a CMP GPU is selected): **PCIe Gen2 Unlock Sync** (for `Cyridd/cmpunlocker` on CMP 40HX, including our built-in `--retrain-gen2` chipset-bridge fix and wake-from-sleep trigger) and **`GGML_VK_DISABLE_COOPMAT2`** toggle switch with a separate **Apply** button.
- **Multi-GPU and per-GPU backend assignment:** run a single daemon across multiple NVIDIA GPUs sorted canonically by PCI Bus ID (e.g. `--targets 0:cuda,1:vulkan`), while ignoring AMD/Intel GPUs.
- **NVML external-load yield (250 ms polling):** detects real GPU workloads (`util.mem >= 5%`) per PCI Bus ID and yields completely (`0.00%` daemon CPU, zero Vulkan/CUDA context thrashing), resuming protection automatically when idle.
- **Graceful Windows shutdown and clean boot exit:** handles `CTRL_SHUTDOWN_EVENT` with top shutdown priority (`SetProcessShutdownParameters(0x3FF, 0)`) to avoid shutdown delays and `BSOD 0x10E (0x2D)` minidumps; exits cleanly with code `0` after `--wait-timeout 30` if no NVIDIA GPU is present.
- **Linux `systemd` support:** prebuilt `linux/bin/nv-keepalive-linux-x86_64` with automatic local `gcc`/`clang` fallback, explicit Vulkan/CUDA choice in `install-linux.sh`, Vulkan install hints when missing, and `Restart=on-failure`.

### Quick Start

**Windows 10 / 11:**
1. Download `NV-KeepAlive-v1.0.0.zip`, verify its SHA-256 against `SHA256SUMS.txt`, and extract the archive.
2. Double-click `windows\NvKeepAliveUI.exe` (approve UAC prompt).
3. Select the desired GPU(s) and backend (`Vulkan` recommended, or `CUDA`), then click **Install**.
4. Click **Test 25s** to verify 0 `nvlddmkm` errors over 25 seconds.

**Linux (Ubuntu / Debian / Arch / Fedora):**
```bash
unzip NV-KeepAlive-v1.0.0.zip -d NV-KeepAlive
cd NV-KeepAlive/linux
sudo bash install-linux.sh
```

### Package Contents

- `windows/NvKeepAliveUI.exe` — Windows 11 Fluent Dark WPF manager (~68 KB, zero external dependencies)
- `windows/NvKeepAlive.exe` — Windows Session 0 / `SYSTEM` keepalive daemon (~65 KB)
- `windows/src/` — C# source code, build script (`build-windows.bat`), and manual `safe-gen2-wrapper.bat`
- `linux/bin/nv-keepalive-linux-x86_64` — prebuilt Linux x86_64 binary
- `linux/` — C source (`nv_keepalive.c`), `Makefile`, `nv-keepalive.service`, `install-linux.sh`, `uninstall-linux.sh`
- `README.md` / `README.ru.md` — full English and Russian technical documentation (including comparison with EVGA Precision X1 / MSI Afterburner voltage lock)
