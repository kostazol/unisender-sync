# Запуск в Linux через systemd

Приложение поддерживает systemd lifetime и уведомление о готовности. Пример рассчитан на Linux с systemd и публикацию linux-x64.

## Подготовить файлы

На машине с .NET SDK 10:

```bash
dotnet publish src/MegaplanUnisender.Api -c Release -r linux-x64 --self-contained true -o .artifacts/linux-x64
```

Перенесите публикацию в `/opt/megaplan-unisender-sync`. Для ARM64 используйте RID `linux-arm64`. Self-contained сборка не требует отдельной установки .NET runtime.

На сервере создайте отдельного системного пользователя и каталог конфигурации:

```bash
sudo useradd --system --home-dir /opt/megaplan-unisender-sync --shell /usr/sbin/nologin megaplan-unisender-sync
sudo install -d -m 750 -o root -g megaplan-unisender-sync /etc/megaplan-unisender-sync
sudo chmod +x /opt/megaplan-unisender-sync/MegaplanUnisender.Api
```

Если пользователь уже существует, повторно создавать его не нужно. Файлы приложения должны быть доступны ему для чтения и исполнения.

## Конфигурация

Создайте `/opt/megaplan-unisender-sync/appsettings.Local.json` по [RUNNING.md](RUNNING.md). Локальный JSON не включается в publish.

В `/etc/megaplan-unisender-sync/environment` сохраните:

```dotenv
Secrets__MegaplanApiToken=CHANGE-ME-MEGAPLAN-TOKEN
Secrets__UnisenderApiKey=CHANGE-ME-UNISENDER-KEY
Secrets__WebhookSecret=CHANGE-ME-GENERATED-WEBHOOK-SECRET
```

Получить WebhookSecret можно командой `./MegaplanUnisender.Api --command generate-webhook-secret`. Graylog необязателен: при необходимости добавьте `Secrets__GraylogAddress` и `Secrets__GraylogApplicationName`.

```bash
sudo chown root:root /etc/megaplan-unisender-sync/environment
sudo chmod 600 /etc/megaplan-unisender-sync/environment
```

systemd читает этот файл и передаёт переменные процессу. Хранилище User Secrets вашей интерактивной учётной записи не используется.

## Unit

Создайте `/etc/systemd/system/megaplan-unisender-sync.service`:

```ini
[Unit]
Description=Megaplan to Unisender synchronization
Wants=network-online.target
After=network-online.target

[Service]
Type=notify
User=megaplan-unisender-sync
Group=megaplan-unisender-sync
WorkingDirectory=/opt/megaplan-unisender-sync
ExecStart=/opt/megaplan-unisender-sync/MegaplanUnisender.Api --urls http://0.0.0.0:5080
EnvironmentFile=/etc/megaplan-unisender-sync/environment
Restart=on-failure
RestartSec=10
TimeoutStartSec=240
TimeoutStopSec=50
SyslogIdentifier=megaplan-unisender-sync

[Install]
WantedBy=multi-user.target
```

Адрес и порт замените под свою сеть. Type=notify сообщает готовность после инициализации API и запуска HTTP. Таймаут старта учитывает проверку конфигурации; таймаут остановки больше таймаута host в приложении.

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now megaplan-unisender-sync
sudo systemctl status megaplan-unisender-sync
sudo journalctl -u megaplan-unisender-sync -f
```

JSON stdout попадает в journal. При настроенном Graylog одновременно отправляется GELF UDP. Интерактивный CLI в службе выключен. Отдельный процесс диагностики не наследует EnvironmentFile автоматически: передавайте ему то же окружение. Для работы с очередью через CLI остановите службу и запустите приложение в терминале.

Обновление: `systemctl stop`, замена публикации с сохранением конфигурации, `systemctl start`. Очередь в RAM при остановке теряется.

[Документация Microsoft UseSystemd](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.hosting.systemdhostbuilderextensions.usesystemd).
