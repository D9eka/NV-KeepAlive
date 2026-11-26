## English

### What's New in v1.1.3
- **Clean Windows reboot & shutdown (`0x10E` fix):** lowered shutdown priority (`SetProcessShutdownParameters(0x100, 0)`) so `NvKeepAlive.exe` protects the GPU until normal apps finish closing instead of exiting first.
- **CMP 40HX EFI Unlock + Gen2 Sync stability:** stopped pausing `NvKeepAlive` during `40HXInstaller -gen2` and `--retrain-gen2` (preventing Stage 2 PnP-disable `0x10E` BSODs on EFI-unlocked `CPU-RM / GSP OFF` cards), improved `WinRing0Session` cleanup so it never stops an external driver instance, and added automatic stale `keepalive.pause` cleanup on boot plus faster pause polling (`300 ms`).

### Windows 10 / 11
1. Download and extract `NV-KeepAlive-Windows-v1.1.3.zip`.
2. Run `NvKeepAliveUI.exe`.
3. Select GPU(s) and backend, then click **Install**.

**Verification:** click **Test 25s**.

### Linux (Ubuntu / Debian / Arch / Fedora)
1. Download `NV-KeepAlive-Linux-v1.1.3.tar.gz`, extract, and launch the TUI manager (or `install-linux.sh` for CLI install):
   ```bash
   mkdir -p NV-KeepAlive && tar -xzf NV-KeepAlive-Linux-v1.1.3.tar.gz -C NV-KeepAlive
   cd NV-KeepAlive
   sudo bash nv-keepalive-tui
   ```
2. Select GPU(s) and backend (`B`), then press `I` to install.

**Verification:** press `T` in `nv-keepalive-tui` (25s `NVRM: Xid` test) or run `nv-keepalive --status`.

Full documentation: [README.md](https://github.com/D9eka/NV-KeepAlive/blob/main/README.md)

---

## Русский

### Что нового в v1.1.3
- **Чистая перезагрузка и выключение Windows (фикс `0x10E`):** приоритет завершения понижен до `SetProcessShutdownParameters(0x100, 0)`, чтобы `NvKeepAlive.exe` защищал GPU до закрытия обычных приложений, а не выходил первым.
- **Стабильность с `CMP 40HX` EFI Unlock + Gen2 Sync:** убрана пауза KeepAlive на время `40HXInstaller -gen2` и `--retrain-gen2` (исключает BSOD `0x10E` от Stage 2 PnP-сброса на EFI-разлоченных картах `CPU-RM / GSP OFF`), `WinRing0Session` больше не останавливает чужой драйвер, добавлены автоочистка зависшего `keepalive.pause` при загрузке ОС и быстрая проверка паузы (`300 мс`).

### Windows 10 / 11
1. Скачайте и распакуйте `NV-KeepAlive-Windows-v1.1.3.zip`.
2. Запустите `NvKeepAliveUI.exe`.
3. Выберите видеокарту и движок, нажмите **Установить**.

**Проверка:** нажмите **Тест 25с**.

### Linux (Ubuntu / Debian / Arch / Fedora)
1. Скачайте `NV-KeepAlive-Linux-v1.1.3.tar.gz`, распакуйте и запустите TUI-менеджер (либо `install-linux.sh` для установки из CLI):
   ```bash
   mkdir -p NV-KeepAlive && tar -xzf NV-KeepAlive-Linux-v1.1.3.tar.gz -C NV-KeepAlive
   cd NV-KeepAlive
   sudo bash nv-keepalive-tui
   ```
2. Выберите видеокарту и движок (`B`), затем нажмите `I` для установки.

**Проверка:** нажмите `T` в `nv-keepalive-tui` (тест 25с по `NVRM: Xid`) или выполните `nv-keepalive --status`.

Подробная документация: [README.ru.md](https://github.com/D9eka/NV-KeepAlive/blob/main/README.ru.md)
