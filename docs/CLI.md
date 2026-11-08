# CLI

Обычный запуск:

```powershell
.\src\MegaplanUnisender.Api\bin\Debug\net10.0\MegaplanUnisender.Api.exe
```

Или открыть exe двойным щелчком. Он запускает webhook и собственную консоль; аргументы и скрипты не требуются. После сообщения «Webhook запущен» вводить команды в этой консоли. Логи обработки также выводятся сюда. Настройки загружаются из каталога exe, секреты — из .NET User Secrets текущего пользователя.

| Команда | Действие |
| --- | --- |
| help | Справка |
| status | Состояние очереди, режим и последние ошибки |
| events [id] | Последние 100 событий, необязательный ID клиента |
| configuration | Настройки и найденные ID без секретов |
| generate-webhook-secret | Вывести новый WebhookSecret, без сохранения и изменения действующего ключа |
| retry <uuid> | Повторить Failed/Uncertain с новой сверкой фактического состояния API; отдельное подтверждение не требуется |
| sync <id> | Прочитать указанного клиента из Megaplan и поставить обработку в общую очередь. Учитывает текущий DryRun |
| stop | Завершить работу; также Ctrl+C |

Эти команды работают с памятью текущего процесса. Запуск второго процесса с командой status/events/retry/sync/stop завершается с ошибкой и пояснением. Сетевого управления нет. Если stdin закрыт, webhook продолжает работу без командной строки; остановка такого процесса выполняется средствами ОС.

Диагностические команды доступны и в работающей консоли, и отдельным запуском из папки exe. Отдельный запуск не открывает HTTP-порт:

```powershell
.\MegaplanUnisender.Api.exe --help
.\MegaplanUnisender.Api.exe --command check
.\MegaplanUnisender.Api.exe --command generate-webhook-secret
.\MegaplanUnisender.Api.exe --command account
.\MegaplanUnisender.Api.exe --command tags
.\MegaplanUnisender.Api.exe --command lists
.\MegaplanUnisender.Api.exe --command unisender
.\MegaplanUnisender.Api.exe --command 'card 12345'
.\MegaplanUnisender.Api.exe --command 'contact test@example.com'
```

check проверяет подключения и определяет ID с принудительным dry-run, без изменения настроек на диске и без внешних записей. Без ключа Unisender проверяется только Megaplan, результат checked_megaplan_only; с ключом также определяется список Unisender. Запускается только отдельным процессом. account/tags/card требуют только адреса CRM и её токена, lists/unisender/contact — ключа Unisender. contact дополнительно определяет целевой список и возвращает статус и фактические теги через экспорт одного email, без внешней записи. Незаполненные настройки другого сервиса этим независимым командам не мешают.

generate-webhook-secret использует криптографический генератор: 32 случайных байта, 64 hex-символа для URL. Отдельный запуск этой команды выполняется до чтения конфигурации: API, настройки и User Secrets для генерации не нужны. В stdout выводится только новое значение, в Graylog оно не отправляется. Сохраните его в Secrets:WebhookSecret через User Secrets для локального запуска либо в WEBHOOK_SECRET для Compose. Действующий ключ не заменяется автоматически; при его изменении также обновите URL webhook в Megaplan и перезапустите утилиту.

В Docker генерация доступна без настроек и сети:

```powershell
docker run --rm --network none megaplan-unisender-sync:local --command generate-webhook-secret
```

Для разработки доступен также dotnet run:

```powershell
dotnet run --project src/MegaplanUnisender.Api -- --command tags
dotnet run --project src/MegaplanUnisender.Api -- --command 'card 12345'
```

Результаты — JSON или понятное сообщение. Код завершения одноразовой команды: 0 — успешно, 1 — ошибка. Настройки загружаются из тех же appsettings, .NET User Secrets и окружения; в Docker отдельный exec наследует переменные контейнера. Команда configuration отдельного процесса показывает только его настройки; вычислить ID перед просмотром позволяет check.

Изменяющая диагностика — только для Diagnostics.TestEmail, с Diagnostics.EnableLiveTests=true и подтверждёнными политиками записи:

```powershell
.\MegaplanUnisender.Api.exe --command 'live-test test@example.com add --confirm-automation-disabled'
```

Поддержаны add/replace/clear/exclude. clear дополнительно требует проверенного ConfirmEmptyTagsClear. Перед тестом отключить связанные автоматизации; после него вернуть EnableLiveTests=false. Не повторять автоматически после timeout. Для повторов событий и live-test используется общий замок обработчика.
