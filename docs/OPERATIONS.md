# Работа на локальном Windows-ПК

Запуск: открыть собранный MegaplanUnisender.Api.exe либо запустить его из терминала без аргументов. По умолчанию порт 5080, настройки рядом с exe, секреты в User Secrets. После сообщения «Webhook запущен» вводить команды в эту же консоль:

```text
status
configuration
events
events 12345
help
stop
```

Остановка также Ctrl+C. После изменения настроек или User Secrets требуется перезапуск. Приложение не создаёт баз данных или файлов состояния. При закрытом stdin webhook продолжает работать; такой процесс останавливается средствами ОС.

Одноразовая диагностика подключений не запускает HTTP-сервер:

```powershell
pwsh -File scripts/Start-Local.ps1 -Command check
pwsh -File scripts/Start-Local.ps1 -Command tags
pwsh -File scripts/Start-Local.ps1 -Command lists
pwsh -File scripts/Start-Local.ps1 -Command 'card 12345'
```

Команда check выполняет только чтение и принудительно использует dry-run. Отдельный процесс не имеет доступа к очереди и истории работающего процесса. Справка по всем командам: [CLI](CLI.md).

Для включения обработки задать Settings.Sync.Enabled=true. DryRun=true читает CRM без записей в Unisender; DryRun=false применяет следующие webhook сразу. Дополнительной сверки или разрешения первого импорта нет. При Enabled=false события отмечаются Disabled и не обрабатываются.

## Ошибки и повтор

Ошибки видны в логах и командами events/status с UUID события, ID клиента, количеством попыток и безопасным кодом. Периодического переобхода CRM нет. Для повторной обработки в том же терминале:

```text
retry <uuid>
```

Uncertain означает, что удалённая запись могла выполниться, а попытки автоматической сверки исчерпаны. Это статус события, а не блокировка email. После неизвестного результата worker сам заново читает API с backoff, в пределах MaxRetries; это повторная сверка, а не слепое повторение HTTP-запроса. Обычный retry <uuid> или следующий webhook также читает фактические теги и членство. Совпадение не вызывает запись. Флаг --acknowledge-uncertain удалён.

Основные коды: megaplan_tags_unknown/partial_tags — неполные метки; tag_limit_exceeded — больше 10 итоговых тегов; preserve_external_ownership_not_supported — режим владения не поддерживает точное зеркалирование; empty_tag_clear_requires_live_verification — очистка отключена; unisender_contact_shape_unverified/null_result_unverified — неизвестный ответ getContact; unisender_export_* — ошибка получения фактических тегов, запись не выполняется; email_status_not_subscribable/list_status_not_subscribable — реальный отказ или блокировка подписчика на стороне Unisender.

## Входящий HTTPS

Исходящие вызовы API выполняются прямо с ПК. Входящему webhook нужен доступный Мегаплану HTTPS-адрес, ведущий на 127.0.0.1:5080; localhost снаружи недоступен. Через reverse proxy или туннель доступен только POST /webhooks/megaplan/{secret}. У приложения нет HTTP-управления и отдельных ключей для него. Отключить access log для секретного маршрута, ограничить тело запроса и при необходимости IP отправителя.

## Границы первого этапа

Очередь, дедупликация и ошибки ограничены и существуют только до завершения процесса. Подсказки ID/email живут до 5 минут, до 256 записей; поля и теги карточек не сохраняются. После истечения TTL, вытеснения или рестарта удаление карточки по одному ID не позволяет найти старый email. Общий email известен лишь среди недавних кандидатов, чьи карточки перечитываются через API. Applied snapshots не используются: каждый webhook читает фактические теги через экспорт одного email. Совпадающий набор не назначается повторно независимо от рестарта. Экспорт ограничен 20 запросами в минуту; большое число событий увеличит очередь.

## Serilog и Graylog

Используются Serilog.AspNetCore 10.0.0 и Serilog.Sinks.Graylog 3.1.1. Консоль получает JSON, Graylog — GELF UDP. Настройки в Secrets: GraylogAddress, GraylogPort (12201), GraylogApplicationName (megaplan-unisender-sync), GraylogSource (необязательно), GraylogMinimumLevel (Information). Пустой адрес оставляет только консоль. configuration показывает включение Graylog и имя приложения, без endpoint и секретов. После изменения настроек нужен перезапуск.

Пример имени приложения для production: GraylogApplicationName=megaplan-unisender-sync-Production. Фильтр поиска: `application_name:"megaplan-unisender-sync-Production"`. UUID события и ID клиента передаются как EventUuid/ContractorId. HTTP-логи с URL webhook, API-ключами и подписанными ссылками исключаются. Утилита не создаёт файлов логов. GELF UDP не подтверждает получение сервером; локальный тест приёмника проверяет формат и фильтрацию, но не заменяет проверку сообщений на вашем сервере.

### Диагностика доставки вебхука

Для временной диагностики задайте `Secrets__GraylogMinimumLevel=Debug` и пересоздайте контейнер. Две записи от `MegaplanUnisender.Api.Webhook` показывают поступление запроса и его итог с HTTP-кодом и `WebhookOutcome`. Пара связана по `CorrelationId`. Например, `queued` означает постановку в очередь, а `duplicate` — повтор уже принятого события. `Webhook processed` отражает отдельный результат работы очереди.

В этих записях используются безопасные поля, включая размер запроса и проверенные ID события и карточки. После диагностики верните уровень `Information` и пересоздайте контейнер.

### Алерт об отказе Unisender и тарифном лимите

Любая окончательная ошибка синхронизации записывается на уровне Error (GELF level=3), с AlertCode=sync_failed, ErrorCode, ApiMethod, ListId, Status, Attempts, EventUuid и ContractorId. В ErrorCode сохраняется безопасный код Unisender, например unisender_not_enough_money/unspecified/http_402; текст поставщика не логируется, потому что может содержать email и ключи. Постоянная бизнес-ошибка не повторяется автоматически. После исправления тарифа можно выполнить sync <id> или retry <uuid>; следующие события продолжают обрабатываться.

Unisender может принять контакт сверх лимита с предупреждением. Документированные варианты result.log[].code=w_common_overlimit и warnings[].warning="Addresses overlimit for current period" дают отдельный Error с AlertCode=unisender_contact_limit_exceeded, ApiMethod, ListId, WarningSource и контекстом события. Успешный результат при этом сохраняется: повторное создание/назначение не запускается. Такой алерт означает, что тариф требует внимания, даже если событие завершилось Synced.

Фильтр для всех Error утилиты:

```text
application_name:"megaplan-unisender-sync-Production" AND level:3
```

Фильтр именно для распознанного предупреждения о превышении числа контактов:

```text
application_name:"megaplan-unisender-sync-Production" AND AlertCode:"unisender_contact_limit_exceeded"
```

Для отказов с произвольным/общим кодом поставщика используйте первый фильтр: конкретный код отказа создания при превышении тарифа не следует угадывать. Лимит 1000 не зашит в код, своего счётчика контактов нет — решение определяется ответом Unisender. Источники: [ошибки API](https://www.unisender.com/ru/support/api/common/api-errors/), [предупреждение importContacts](https://www.unisender.com/ru/support/api/contacts/importcontacts/).

Проверено на HTTP-заглушках и настоящем локальном GELF UDP-приёмнике: отказ subscribe (not_enough_money и unspecified), предупреждение при создании и при изменении тегов, уровень 3, контекст, отсутствие email/ключей в сообщении, одна попытка постоянного отказа и отсутствие повторной записи после принятого предупреждения. Реальный тариф намеренно не исчерпывался; серверный read-back Graylog этим тестом не заменяется.

Логи не содержат payload, email и ключей. Секреты находятся в отдельном User Secrets, обычные настройки — в appsettings.Local.json. Локальный Docker-запуск описан в DOCKER.md; тесты выполняются на Windows/в CI, вне Dockerfile.
