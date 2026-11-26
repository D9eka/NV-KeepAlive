## English

### What's New in v1.1.0
- **Windows:** automatic Light/Dark theme following Windows settings (`Auto / Dark / Light`), embedded application icon, live per-GPU daemon VRAM measurement, and non-intrusive update check.
- **Linux:** new zero-dependency interactive terminal manager (`nv-keepalive-tui`) with per-GPU `Vulkan | CUDA` selection, live daemon VRAM/CPU/`NVRM: Xid` telemetry, Vulkan install guide, and 25s self-test.

### Windows 10 / 11
1. Download and extract `NV-KeepAlive-Windows-v1.1.0.zip`.
2. Run `NvKeepAliveUI.exe`.
3. Select GPU(s) and backend, then click **Install**.

**Verification:** click **Test 25s**.

### Linux (Ubuntu / Debian / Arch / Fedora)
1. Download `NV-KeepAlive-Linux-v1.1.0.tar.gz`, extract, and launch the TUI manager (or `install-linux.sh` for CLI install):
   ```bash
   mkdir -p NV-KeepAlive && tar -xzf NV-KeepAlive-Linux-v1.1.0.tar.gz -C NV-KeepAlive
   cd NV-KeepAlive
   sudo bash nv-keepalive-tui
   ```
2. Select GPU(s) and backend (`B`), then press `I` to install.

**Verification:** press `T` in `nv-keepalive-tui` (25s `NVRM: Xid` test) or run `nv-keepalive --status`.

Full documentation: [README.md](https://github.com/D9eka/NV-KeepAlive/blob/main/README.md)

---

## Русский

### Что нового в v1.1.0
- **Windows:** автоподстройка светлой/тёмной темы под Windows (`Авто / Тёмная / Светлая`), фирменная иконка приложения, живая VRAM самого фикса по каждой карте и тихая проверка обновлений.
- **Linux:** интерактивный терминальный менеджер `nv-keepalive-tui` (0 зависимостей) с выбором `Vulkan | CUDA` по картам, живой телеметрией VRAM/CPU/`NVRM: Xid`, гайдом по Vulkan и тестом 25с.

### Windows 10 / 11
1. Скачайте и распакуйте `NV-KeepAlive-Windows-v1.1.0.zip`.
2. Запустите `NvKeepAliveUI.exe`.
3. Выберите видеокарту и движок, нажмите **Установить**.

**Проверка:** нажмите **Тест 25с**.

### Linux (Ubuntu / Debian / Arch / Fedora)
1. Скачайте `NV-KeepAlive-Linux-v1.1.0.tar.gz`, распакуйте и запустите TUI-менеджер (либо `install-linux.sh` для установки из CLI):
   ```bash
   mkdir -p NV-KeepAlive && tar -xzf NV-KeepAlive-Linux-v1.1.0.tar.gz -C NV-KeepAlive
   cd NV-KeepAlive
   sudo bash nv-keepalive-tui
   ```
2. Выберите видеокарту и движок (`B`), затем нажмите `I` для установки.

**Проверка:** нажмите `T` в `nv-keepalive-tui` (тест 25с по `NVRM: Xid`) или выполните `nv-keepalive --status`.

Подробная документация: [README.ru.md](https://github.com/D9eka/NV-KeepAlive/blob/main/README.ru.md)
