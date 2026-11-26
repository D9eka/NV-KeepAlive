## English

### What's New in v1.1.2
- **Critical stability fix (Windows & Linux):** recreate `VkInstance` and re-enumerate `VkPhysicalDevice` on every worker resume/recovery cycle instead of reusing stale handles, preventing `nvoglv64.dll` `0xc0000005` (`AccessViolationException`) crashes after `40HXInstaller -gen2` PCIe resets or WDDM TDRs.
- **Self-recovery & watchdog:** fixed the dead `g_needRestart` self-restart condition (`!g_shutdown`), added Corrupted State Exception (`AccessViolationException`) handling on Windows workers, added a 1-minute Task Scheduler watchdog trigger (`MultipleInstances=IgnoreNew`), and hardened `CheckGen2Status()` to verify `gen2-sync-wrapper.cmd` exists on disk.

### Windows 10 / 11
1. Download and extract `NV-KeepAlive-Windows-v1.1.2.zip`.
2. Run `NvKeepAliveUI.exe`.
3. Select GPU(s) and backend, then click **Install**.

**Verification:** click **Test 25s**.

### Linux (Ubuntu / Debian / Arch / Fedora)
1. Download `NV-KeepAlive-Linux-v1.1.2.tar.gz`, extract, and launch the TUI manager (or `install-linux.sh` for CLI install):
   ```bash
   mkdir -p NV-KeepAlive && tar -xzf NV-KeepAlive-Linux-v1.1.2.tar.gz -C NV-KeepAlive
   cd NV-KeepAlive
   sudo bash nv-keepalive-tui
   ```
2. Select GPU(s) and backend (`B`), then press `I` to install.

**Verification:** press `T` in `nv-keepalive-tui` (25s `NVRM: Xid` test) or run `nv-keepalive --status`.

Full documentation: [README.md](https://github.com/D9eka/NV-KeepAlive/blob/main/README.md)

---

## Русский

### Что нового в v1.1.2
- **Критический фикс стабильности (Windows и Linux):** пересоздание `VkInstance` и повторное перечисление `VkPhysicalDevice` после каждой паузы и восстановления вместо повторного использования старого дескриптора — устраняет падение `nvoglv64.dll` с `0xc0000005 (AccessViolationException)` после сброса PCIe (`40HXInstaller -gen2`) или WDDM TDR.
- **Самовосстановление и watchdog:** исправлено нерабочее условие автоперезапуска `g_needRestart` (`!g_shutdown`), добавлен перехват `AccessViolationException` в Windows-воркерах, добавлен 1-минутный watchdog-триггер в Планировщике задач (`IgnoreNew`) и проверка наличия файла `gen2-sync-wrapper.cmd` в статусе Gen2 Sync.

### Windows 10 / 11
1. Скачайте и распакуйте `NV-KeepAlive-Windows-v1.1.2.zip`.
2. Запустите `NvKeepAliveUI.exe`.
3. Выберите видеокарту и движок, нажмите **Установить**.

**Проверка:** нажмите **Тест 25с**.

### Linux (Ubuntu / Debian / Arch / Fedora)
1. Скачайте `NV-KeepAlive-Linux-v1.1.2.tar.gz`, распакуйте и запустите TUI-менеджер (либо `install-linux.sh` для установки из CLI):
   ```bash
   mkdir -p NV-KeepAlive && tar -xzf NV-KeepAlive-Linux-v1.1.2.tar.gz -C NV-KeepAlive
   cd NV-KeepAlive
   sudo bash nv-keepalive-tui
   ```
2. Выберите видеокарту и движок (`B`), затем нажмите `I` для установки.

**Проверка:** нажмите `T` в `nv-keepalive-tui` (тест 25с по `NVRM: Xid`) или выполните `nv-keepalive --status`.

Подробная документация: [README.ru.md](https://github.com/D9eka/NV-KeepAlive/blob/main/README.ru.md)
