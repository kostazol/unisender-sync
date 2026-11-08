# Настройки и секреты

Конфигурация разделена на секции Settings и Secrets. Основной `src/MegaplanUnisender.Api/appsettings.json` содержит рабочие значения по умолчанию и входит в образ: `Sync.Enabled=true`, `Sync.DryRun=false`, `TagOwnershipMode=Exclusive`, `ConfirmOverwriteAllTags=true`, `ConsentPolicy=VerifiedByAdministrator`. В Compose эти флаги не нужны. Локальный appsettings переопределяет обычные параметры; API-ключи хранятся в отдельном .NET User Secrets с ID `megaplan-unisender-sync-local`.

## Что заполнить

Основной `appsettings.example.json` содержит только параметры подключения и выбранные пользователем названия:

| Параметр Settings | Что указать |
| --- | --- |
| Megaplan.BaseUrl | Адрес своего Megaplan, например https://company.megaplan.ru. HTTPS origin без пути |
| Megaplan.ExpectedIntegrationUuid | UUID созданного приложения из интерфейса Megaplan |
| Megaplan.EligibilityGlobalTagName | Точное имя глобальной метки, разрешающей синхронизацию, например Unisender |
| Unisender.ListName | Название управляемого списка, например Мегаплан. При DryRun=false отсутствующий список создаётся автоматически; найденный ID используется в памяти |
| Sync.Enabled | В примере true: при запуске выполняется автоматическое определение настроек. false отключает обращения к API при запуске |
| Sync.DryRun | В примере true: читать CRM и наблюдать события без записи контактов |

Числовые ID искать и переносить не нужно. Приложение ищет точное ListName и создаёт список с этим именем, если его нет и включена запись. Пустое имя позволяет выбрать единственный список, но не даёт разрешения создать произвольный. Неоднозначный или невалидный ответ останавливает запуск. Если ответ createList потерян, сначала повторно читается getLists: повторной слепой записи нет.

## Что определяется автоматически

До запуска HTTP и обработчика, при Enabled=true:

1. `GET /api/v3/accountInfo` возвращает идентичность аккаунта. По умолчанию используется permanentHost — исходный постоянный хост, который может отличаться от пользовательского домена BaseUrl. Значение не берётся из первого webhook.
2. `GET /api/v3/contractor/tags` читает страницы справочника. Управляющая метка выбирается по точному имени, с учётом регистра, только среди isGlobal=true. Её ID и подтверждённые глобальные ID заполняются в памяти.
3. По умолчанию SyncAllGlobalTags=true: все глобальные метки карточки, кроме управляющей, передаются под текущими CRM-именами. Дополнительные TagMappings не нужны. Управляющая метка исключается по ID даже после переименования; она служит только условием синхронизации. Новые глобальные метки с явным isGlobal=true подхватываются из последующих карточек без перезапуска. Личные метки игнорируются. Неизвестная глобальность новой метки блокирует обработку.
4. Если ключ Unisender заполнен, getLists определяет ListId по ListName. Если имя не найдено, при DryRun=false вызывается createList; при DryRun=true запуск сообщает, что список отсутствует, и не создаёт его. Найденный/созданный ID сохраняется в памяти, обработчик событий больше не запрашивает списки. Явный ListId остаётся дополнительным ручным режимом. Dry-run допускает пустой ключ Unisender: тогда выполняется только настройка Megaplan.
5. Перед записью проверяется getContact для случайного отсутствующего адреса в example.com. Поддержаны result:null и наблюдавшийся в реальном аккаунте код object_not_found. Распознавание фиксируется в памяти; ошибки авторизации и произвольные ошибки не означают отсутствие контакта.

Полученные значения не записываются в appsettings или Secrets. Посмотреть результат: команда configuration в терминале работающей утилиты. Для проверки до запуска: `pwsh -File scripts/Start-Local.ps1 -Command check`; HTTP-сервер при этом не запускается, внешних записей нет.

Управляющая метка после переименования продолжает работать по найденному ID до рестарта. Перед следующим запуском её новое имя нужно обновить в EligibilityGlobalTagName. Остальные глобальные метки при обработке карточек используют текущие имена.

## Где хранить

- `src/MegaplanUnisender.Api/appsettings.Local.json`: секция Settings; файл игнорируется Git.
- .NET User Secrets проекта API: секция Secrets. В Visual Studio — Manage User Secrets, из терминала — `dotnet user-secrets --project src/MegaplanUnisender.Api`. Приложение и скрипты не зависят от расположения хранилища.
- Docker/Portainer: `Settings__...` и `Secrets__...` в окружении контейнера. Три секрета задаются переменными `MEGAPLAN_API_TOKEN`, `UNISENDER_API_KEY`, `WEBHOOK_SECRET` и подставляются Compose. Для CLI Compose можно использовать игнорируемый Git файл `.env`; для Portainer — Environment variables Stack.

В Secrets вручную заполняются только `MegaplanApiToken` и `UnisenderApiKey`. Инициализатор создаёт случайный WebhookSecret; при повторном запуске дополняет отсутствующие поля, сохраняя существующие значения. Start-Local.ps1 вызывает его автоматически. Управление выполняется в терминале, дополнительного ключа управления нет.

Для первого этапа Megaplan → webhook с Sync.DryRun=true достаточно MegaplanApiToken и WebhookSecret. UnisenderApiKey можно заполнить позже: он становится обязательным перед включением записи. Глобальная управляющая метка должна существовать уже при запуске dry-run.

В User Secrets допустимы вложенная секция Secrets и плоские ключи `Secrets:MegaplanApiToken`. В окружении используется разделитель `__`, например `Secrets__MegaplanApiToken`. Значения не помещать в Settings, отслеживаемые Git файлы и журналы. User Secrets — локальное хранилище разработки; Docker получает секреты через окружение.

Приоритет: appsettings → appsettings среды → appsettings.Local → User Secrets → окружение → аргументы. User Secrets подключается явно через .NET-провайдер; переменные окружения имеют приоритет. После изменения настроек перезапустить приложение.

При запуске exe appsettings читаются из его каталога, независимо от рабочей папки. Сборка копирует исходный appsettings.Local.json в bin, но publish его исключает. При отладке после изменения исходного файла пересобрать проект; у готовой утилиты редактировать файл рядом с exe либо использовать окружение. Прямой запуск по умолчанию слушает 127.0.0.1:5080 и не требует PowerShell-обёртки.

## Включение записи

Обычный запуск приложения уже включает запись и политики выбранного сценария: интеграция управляет всеми тегами контакта, согласие подтверждено администратором. Это настроено в основном `appsettings.json`. Отдельный список не изолирует общие теги контакта; лишние относительно Megaplan теги будут удаляться.

В локальном `appsettings.example.json` оставлен `Sync.DryRun=true` для отладки. Для записи достаточно установить `Sync.DryRun=false`; повторять политики в локальном файле или Compose не нужно. Переопределения политик по-прежнему проверяются: несовместимые значения останавливают запуск записи.

Вариант с письмами подтверждения: ConsentPolicy=RequestConfirmation, DoubleOptInMode=0 или 4, AllowConfirmationEmails=true. По умолчанию DoubleOptInMode=3; утилита самостоятельно письма подтверждения не разрешает.

Публичный HTTPS-адрес для доставки webhook на локальный порт и подписку приложения на поток contractors нужно настроить в Megaplan. Утилита не может получить этот адрес из CRM API.

## Дополнительные настройки

Обычный сценарий не требует этих полей; значения по умолчанию заданы в коде.

| Параметры | Назначение / значения по умолчанию |
| --- | --- |
| Megaplan.ExpectedAccountId, AccountIdentityField | Ручное переопределение; пустой ID означает получение через API. Поле по умолчанию permanentHost; поддержаны также uniqueAccountId и id, если версия API их возвращает. Явный ID не заменяется |
| Megaplan.EligibilityGlobalTagId | Выбор по ID вместо имени; EligibilityGlobalTagName в этом случае оставить пустым |
| Megaplan.SyncAllGlobalTags | true. Для выборочного переноса задать false и явные TagMappings |
| Megaplan.ManagedGlobalTagIds, TagMappings | Режим выборочных ID: подтверждённая глобальность и ID → фиксированное имя либо @crm-name. Явные переименования также имеют приоритет в автоматическом режиме |
| Megaplan.ContractorModels | Contractor, ContractorHuman, ContractorCompany |
| Unisender.ApiBaseUrl, ListId | https://api.unisender.com/ru/api/; необязательный ручной ID |
| Unisender.FieldMappings | По умолчанию пусто: только email и теги. ФИО и телефон не передаются. Для отдельного явно заданного расширения поддержаны name, phone, extra:<поле API> |
| Unisender.ConfirmNullResultMeansMissing, VerifiedMissingContactErrorCodes | Проверенные ручные переопределения для версий API, где автоматическое распознавание отсутствующего контакта несовместимо. Общие/auth/rate-limit коды запрещены |
| Unisender.ConfirmEmptyTagsClear | true. При снятии последней обычной метки importContacts передаёт пустой tags с overwrite_tags=1 и overwrite_lists=0. Управляющая метка в набор не входит, контакт остаётся в списке. false — совместимое отключение очистки для особой версии API |
| Webhook.MaxBodySize | 65536 байт, допустимо 1024..1048576 |
| Diagnostics.EnableLiveTests, TestEmail | false и пусто. Изменяющая CLI-диагностика только на специально указанном email |
| Sync.QueueCapacity | 256 |
| Sync.MaxRetries, RetryBackoff | 6, 00:00:10 с exponential backoff и jitter |
| Sync.RateLimits | GeneralPerMinute=120, SubscribePerMinute=60, ExportPerMinute=20 (1..20), MegaplanPerMinute=60 |
| Sync.RecentContractorCacheCapacity | 256, допустимо 1..4096. Только недавние ID/email/eligible, без тегов и полей карточек |
| Sync.RecentContractorCacheTtl | 00:05:00, больше 0 и до 30 минут. Абсолютный срок, не продлевается постоянными обновлениями |

Поля настройки Graylog в Secrets: GraylogAddress, GraylogPort=12201, GraylogApplicationName=megaplan-unisender-sync, GraylogSource, GraylogMinimumLevel=Information. Адрес пуст — только Serilog JSON в stdout. Адрес задан — добавляется GELF UDP; API-ключи Graylog для такого input не нужны. Настройки подключения, как и API-токены, не включаются в образ и не выводятся командой configuration.

Текущие теги каждого существующего контакта читаются через экспорт одного email и сравниваются с CRM без локального applied snapshot. Совпадение не вызывает запись, ошибка чтения запрещает изменение. PreserveExternal не поддерживает точное зеркалирование без отдельного правила владения; для переноса всех CRM-тегов используется Exclusive. Более 10 тегов у контакта — ошибка без обрезания.

В переменных окружения разделитель `__`, например `Settings__Unisender__ListName`. Секреты для локальной отладки заполняются в User Secrets.
