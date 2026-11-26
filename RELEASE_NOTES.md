## English

### Windows 10 / 11
1. Download and extract `NV-KeepAlive-Windows-v1.0.0.zip`.
2. Run `NvKeepAliveUI.exe`.
3. Select GPU(s) and backend, then click **Install**.

**Verification:** click **Test 25s**.

### Linux (Ubuntu / Debian / Arch / Fedora)
1. Download `NV-KeepAlive-Linux-v1.0.0.tar.gz`, extract, and run the installer:
   ```bash
   mkdir -p NV-KeepAlive && tar -xzf NV-KeepAlive-Linux-v1.0.0.tar.gz -C NV-KeepAlive
   cd NV-KeepAlive
   sudo bash install-linux.sh
   ```
2. Follow the interactive prompts to select the backend and GPU(s).

**Verification:** run `nv-keepalive --status` or `systemctl status nv-keepalive`.

Full documentation: [README.md](https://github.com/D9eka/NV-KeepAlive/blob/main/README.md)

---

## Русский

### Windows 10 / 11
1. Скачайте и распакуйте `NV-KeepAlive-Windows-v1.0.0.zip`.
2. Запустите `NvKeepAliveUI.exe`.
3. Выберите видеокарту и движок, нажмите **Установить**.

**Проверка:** нажмите **Тест 25с**.

### Linux (Ubuntu / Debian / Arch / Fedora)
1. Скачайте `NV-KeepAlive-Linux-v1.0.0.tar.gz`, распакуйте и запустите установщик:
   ```bash
   mkdir -p NV-KeepAlive && tar -xzf NV-KeepAlive-Linux-v1.0.0.tar.gz -C NV-KeepAlive
   cd NV-KeepAlive
   sudo bash install-linux.sh
   ```
2. В интерактивном меню выберите движок и нужные видеокарты.

**Проверка:** выполните `nv-keepalive --status` или `systemctl status nv-keepalive`.

Подробная документация: [README.ru.md](https://github.com/D9eka/NV-KeepAlive/blob/main/README.ru.md)
