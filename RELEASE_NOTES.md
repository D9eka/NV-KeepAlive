## English

### What's New in v1.1.1
- **Windows UI:** themed Fluent Dark/Light scrollbars (`ScrollBar` + `ScrollViewer`), responsive 1/2-column layout (vertical resize expands the Event Log; wide/maximized windows >= 1120 px move the Event Log to the right while keeping controls compact and vertically centered on the left), and telemetry alignment fixes (`VRAM`, GPU wattage).

### Windows 10 / 11
1. Download and extract `NV-KeepAlive-Windows-v1.1.1.zip`.
2. Run `NvKeepAliveUI.exe`.
3. Select GPU(s) and backend, then click **Install**.

**Verification:** click **Test 25s**.

### Linux (Ubuntu / Debian / Arch / Fedora)
1. Download `NV-KeepAlive-Linux-v1.1.1.tar.gz`, extract, and launch the TUI manager (or `install-linux.sh` for CLI install):
   ```bash
   mkdir -p NV-KeepAlive && tar -xzf NV-KeepAlive-Linux-v1.1.1.tar.gz -C NV-KeepAlive
   cd NV-KeepAlive
   sudo bash nv-keepalive-tui
   ```
2. Select GPU(s) and backend (`B`), then press `I` to install.

**Verification:** press `T` in `nv-keepalive-tui` (25s `NVRM: Xid` test) or run `nv-keepalive --status`.

Full documentation: [README.md](https://github.com/D9eka/NV-KeepAlive/blob/main/README.md)

---

## Русский

### Что нового в v1.1.1
- **Windows UI:** тёмные и светлые Fluent-скроллы в тон теме, адаптивная 1/2-колоночная раскладка (растягивание по вертикали увеличивает окно лога; при ширине >= 1120 px карточки фиксируются слева по центру, а журнал событий переезжает вправо на всю высоту) и исправление переноса/обрезки телеметрии (`VRAM`, мощность GPU).

### Windows 10 / 11
1. Скачайте и распакуйте `NV-KeepAlive-Windows-v1.1.1.zip`.
2. Запустите `NvKeepAliveUI.exe`.
3. Выберите видеокарту и движок, нажмите **Установить**.

**Проверка:** нажмите **Тест 25с**.

### Linux (Ubuntu / Debian / Arch / Fedora)
1. Скачайте `NV-KeepAlive-Linux-v1.1.1.tar.gz`, распакуйте и запустите TUI-менеджер (либо `install-linux.sh` для установки из CLI):
   ```bash
   mkdir -p NV-KeepAlive && tar -xzf NV-KeepAlive-Linux-v1.1.1.tar.gz -C NV-KeepAlive
   cd NV-KeepAlive
   sudo bash nv-keepalive-tui
   ```
2. Выберите видеокарту и движок (`B`), затем нажмите `I` для установки.

**Проверка:** нажмите `T` в `nv-keepalive-tui` (тест 25с по `NVRM: Xid`) или выполните `nv-keepalive --status`.

Подробная документация: [README.ru.md](https://github.com/D9eka/NV-KeepAlive/blob/main/README.ru.md)
