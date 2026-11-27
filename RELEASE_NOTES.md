## English

### What's New in v1.1.4
- **Automatic GPU disable/enable standby (Windows daemon):** detects disabled NVIDIA GPUs (`Code 22`) via `cfgmgr32.dll` (`0%` CPU, zero log spam), cleanly releases GPU contexts into standby when a GPU is disabled in Device Manager, and automatically resumes protection within ~2-3 seconds when re-enabled.
- **Live GPU hot-refresh in Windows GUI:** hooks `WM_DEVICECHANGE` and live topology diffs to automatically update the GPU list, conditional CMP cards, and `Standby (no GPU)` status badge without restarting `NvKeepAliveUI.exe`.

### Windows 10 / 11
1. Download and extract `NV-KeepAlive-Windows-v1.1.4.zip`.
2. Run `NvKeepAliveUI.exe`.
3. Select GPU(s) and backend, then click **Install**.

**Verification:** click **Test 25s**.

### Linux (Ubuntu / Debian / Arch / Fedora)
1. Download `NV-KeepAlive-Linux-v1.1.4.tar.gz`, extract, and launch the TUI manager (or `install-linux.sh` for CLI install):
   ```bash
   mkdir -p NV-KeepAlive && tar -xzf NV-KeepAlive-Linux-v1.1.4.tar.gz -C NV-KeepAlive
   cd NV-KeepAlive
   sudo bash nv-keepalive-tui
   ```
2. Select GPU(s) and backend (`B`), then press `I` to install.

**Verification:** press `T` in `nv-keepalive-tui` (25s `NVRM: Xid` test) or run `nv-keepalive --status`.

Full documentation: [README.md](https://github.com/D9eka/NV-KeepAlive/blob/main/README.md)

---

## Русский

### Что нового в v1.1.4
- **Авто-стоп/старт при отключении/включении видеокарты (Windows):** демон отслеживает состояние NVIDIA GPU через `cfgmgr32.dll` (`0%` CPU, без спама в лог) — при отключении карты в Диспетчере устройств (`Code 22`) сразу освобождает контекст и уходит в тихий режим ожидания, а при повторном включении сам возобновляет защиту за ~2–3 секунды.
- **Живое обновление списка GPU в Windows GUI:** обработка `WM_DEVICECHANGE` и проверка изменений состава видеокарт автоматически обновляют список GPU, карточки `Gen2 Sync`/`CoopMat2` и бейдж `Ожидание GPU` без перезапуска `NvKeepAliveUI.exe`.

### Windows 10 / 11
1. Скачайте и распакуйте `NV-KeepAlive-Windows-v1.1.4.zip`.
2. Запустите `NvKeepAliveUI.exe`.
3. Выберите видеокарту и движок, нажмите **Установить**.

**Проверка:** нажмите **Тест 25с**.

### Linux (Ubuntu / Debian / Arch / Fedora)
1. Скачайте `NV-KeepAlive-Linux-v1.1.4.tar.gz`, распакуйте и запустите TUI-менеджер (либо `install-linux.sh` для установки из CLI):
   ```bash
   mkdir -p NV-KeepAlive && tar -xzf NV-KeepAlive-Linux-v1.1.4.tar.gz -C NV-KeepAlive
   cd NV-KeepAlive
   sudo bash nv-keepalive-tui
   ```
2. Выберите видеокарту и движок (`B`), затем нажмите `I` для установки.

**Проверка:** нажмите `T` в `nv-keepalive-tui` (тест 25с по `NVRM: Xid`) или выполните `nv-keepalive --status`.

Подробная документация: [README.ru.md](https://github.com/D9eka/NV-KeepAlive/blob/main/README.ru.md)
