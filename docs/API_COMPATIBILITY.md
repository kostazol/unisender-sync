# API-совместимость

Описание используемых контрактов и ограничений API. Совместимость конкретной установки проверяется командой `check` и сценариями из [MANUAL_API_TESTS.md](MANUAL_API_TESTS.md).

## Megaplan

Источники: [API v3](https://dev.megaplan.ru/apiv3/index.html), [события](https://dev.megaplan.ru/apps/events.html), [demo-схема](https://demo.megaplan.ru/api/v3/docs), [SDK поставщика](https://github.com/megaplan/megaplansdk), [HTTP-код SDK](https://github.com/megaplan/megaplansdk/blob/main/src/MegaplanRequest.php).

| Возможность | Контракт и реализация | Что проверить в своём аккаунте |
| --- | --- | --- |
| Авторизация | HTTPS GET с Authorization: Bearer, настроенный токен из Secrets; redirect отключён | Права и срок действия токена |
| Аккаунт | GET /api/v3/accountInfo → AccountInfo: permanentHost. Метод и поле описаны demo-схемой. Это отдельный метод от /account/account, который возвращает тарифные сведения | Совпадение постоянного хоста с accountInfo.permanentHost в фактическом webhook. Пользовательский BaseUrl может отличаться; uniqueAccountId не предполагается доступным через этот API |
| Карточка | GET /api/v3/contractor/{id}, meta/data; поддерживаются ContractorHuman и ContractorCompany; требуется canSeeFull=true | Права приложения на карточки и полнота возвращаемых данных |
| Метки | tags: Tag[], tagsCount; Tag.id, name, isGlobal документированы demo-схемой | Раскрытие ссылок и полный набор. isGlobal=false исключает метку; при отсутствии флага разрешён только проверенный ID |
| Поиск по имени при старте | GET /api/v3/contractor/tags с JSON-параметрами limit, fields=[name,isGlobal], pageAfter={id,contentType:Tag}; meta.pagination.count | Метод и параметры подтверждены demo-схемой; Обход до полного count, фильтр isGlobal=true, точное уникальное имя. Отсутствующий isGlobal, неполная/изменившаяся пагинация или неоднозначное имя останавливают запуск. Вторая страница проверена моками |
| Контакты | contactInfo — объединение ContactInfo и Address. У ContactInfo.type строка, у Address.type объект AddressType; почтовые Address исключаются до разбора email/phone, полнота проверяется по всей исходной коллекции. Неизвестные типы элементов контактной информации считаются неполными данными | Основной email, телефон и дополнительные поля; неполные/неизвестные контактные элементы продолжают блокироваться. Личные данные в отчёте не сохраняются |
| Webhook | uuid, integrationUuid, model, event, data, accountInfo; on_after_create/update/drop | Фактические верхние model потока contractors и выбранное поле accountInfo |
| Удаление | По событию drop перечитывается текущая карточка; реальный GET отсутствующего ID вернул 404 и GetAsync вернул null | Реальные soft-delete и событие удаления ещё не проверялись. После рестарта неизвестный ID не даёт старый email |
| Доставка | Ответ 200 после помещения в ограниченный Channel; 503 при заполнении | Документация требует 200 в течение 20 секунд. Приём в память не даёт долговременной гарантии доставки |
| Lifecycle | Integration безопасно пропускается после проверки URL-секрета и integrationUuid. Официальный пример без accountInfo допускается только для пропуска Integration; карточки без accountInfo отклоняются | Токен из события установки не используется |

Значения contentType подтверждены demo-схемой; точный model webhook конкретного аккаунта нужно наблюдать. Неизвестные события пропускаются. Срабатывание on_after_update при изменении только меток и переименовании справочника проверяется вручную. Пагинация API изучена, но обход всей CRM и сверка в текущую версию не входят.

## Обычный Unisender

База https://api.unisender.com/ru/api/. Все вызовы POST form-encoded, api_key и format=json в теле. Unisender Go не используется. Проверяются HTTP-ошибки и JSON error/code; текст поставщика не попадает в журнал.

| Метод / источник | Параметры и ограничение | Реализация / непроверенная особенность |
| --- | --- | --- |
| [subscribe](https://www.unisender.com/ru/support/api/contacts/subscribe/) | list_ids, fields[email], fields[...], tags, overwrite=2, double_optin=0/3/4; результат person_id; до 10 тегов | Переданные теги заменяются, отсутствие tags сохраняет старые. Текущее состояние читается через API; при совпадении записи нет, включая первый webhook после рестарта |
| [exclude](https://www.unisender.com/ru/support/api/contacts/exclude/) | contact_type=email, contact, единственный list_ids | Контакт целиком не удаляется, unsubscribe не вызывается. Перед исключением проверяется отказ подписчика |
| [getContact](https://www.unisender.com/ru/support/api/contacts/getcontact/) | email, include_lists=1; include_fields=1 только при явном FieldMappings; email.status, lists[].id/status, fields | Разрешены new/active; отказ/блокировка/ожидание подтверждения блокируют подписку. Формат отсутствующего контакта проверяется отдельно. Неполное членство не разрешает подписку. Метод не возвращает теги |
| [getLists](https://www.unisender.com/ru/support/api/contacts/getlists/) | api_key; result — массив id/title | На старте ID определяется по точному названию и сохраняется в памяти. Повторный поиск списка на каждый webhook не выполняется |
| [createList](https://www.unisender.com/ru/support/api/contacts/createlist/) | title; result.id; названия уникальны | Отсутствующий ListName создаётся при DryRun=false. После неопределённого исхода/ошибки выполняется getLists для поиска уже созданного имени вместо слепого повтора. Обработка успешного ответа и неопределённого результата покрыта HTTP-моками |
| [getTags](https://www.unisender.com/ru/support/api/inputs/gettags/) | Справочник тегов | Не определяет теги отдельного контакта |
| [importContacts](https://www.unisender.com/ru/support/api/contacts/importcontacts/) | одна строка email+tags; overwrite_tags=0 для добавления недостающих, 1 для удаления/замены; overwrite_lists=0; до 10 тегов | Для контакта в целевом списке при неизменных полях. Добавление отправляет только разницу, без повторного subscribe. Удаление передаёт полный желаемый набор, включая пустой. Проверяются updated=1, invalid и log[].code с e_. Изменения и ошибки проверяются HTTP-моками |
| [async/exportContacts](https://www.unisender.com/ru/support/api/contacts/exportcontacts/) | обязательный проверенный email, только field_names=email/tags; task_uuid → async/getTaskResult → file_to_download | Reader включён для каждого существующего допустимого контакта. Фильтр одного email, без list_id/notify_url. Проверяются UUID, тип и готовность задания; в реальном ответе task_type=export_contacts, в документации exportContacts — поддержаны оба. Ожидание до 30 секунд. CSV UTF-8 с запятыми, произвольный порядок двух колонок, одна строка с точным email; ошибочная/пустая выгрузка не означает пустые tags |
| [Лимиты](https://www.unisender.com/ru/support/api/common/api-limits/) | 1200 общих / 300 subscribe / 20 export за 60 секунд | Применяется совместное равномерное ограничение общих вызовов, subscribe и export; ExportPerMinute по умолчанию 20, увеличить выше 20 нельзя |
| [Ошибки](https://www.unisender.com/ru/support/api/common/api-errors/) | retry_later, api_call_limit_exceeded_for_api_key/ip | Повторяются в памяти с backoff; HTTP 429 учитывает Retry-After |
| Тарифный лимит контактов | importContacts: result.log[].code=w_common_overlimit, warnings[].warning="Addresses overlimit for current period"; контакт может быть добавлен | Отдельный Error/AlertCode=unisender_contact_limit_exceeded, без повтора успешной записи. Окончательный отказ любого API — Error/AlertCode=sync_failed с безопасным ErrorCode и ApiMethod. not_enough_money означает проблему средств, не доказательство конкретного лимита контактов. Число 1000 не зашито и счётчик не хранится |

[Сторонний PHP wrapper](https://github.com/OmgDef/unisender-wrapper/blob/master/UniSenderWrapper.php) изучен как дополнительный пример POST/form-encoding; PHP-зависимостей нет. Авторитет для современного контракта — документация поставщика.

Exclusive требует явного подтверждения владения всеми тегами контакта. PreserveExternal не поддерживает точное зеркалирование без отдельного правила владения; наличие reader само по себе это правило не определяет. Double opt-in=3 требует подтверждённого согласия, 0/4 — отдельного включения писем подтверждения. Никаких sendEmail/createCampaign приложение не вызывает, но назначение изменённых тегов и включение в список сами могут запускать настроенные сценарии.

SyncAllGlobalTags=true передаёт все глобальные метки, кроме управляющей, с актуальными именами. Управляющая исключается по ID и только разрешает синхронизацию. Новые явно глобальные ID подхватываются из карточек без ручного сопоставления. FieldMappings по умолчанию пуст: отправляются email и tags, без ФИО/телефона. Лимит 10 применяется к итоговому набору конкретного контакта.

Reader скачивает CSV отдельным GET без api_key, Authorization и cookies, редиректы отключены. Допускаются HTTPS-хранилища *.s3.unisender.ru (в своём аккаунте usdata.s3.unisender.ru) и *.amazonaws.com; размер не более 64 KiB. Подписанная ссылка, CSV и данные контакта не логируются и не сохраняются на диск. Переход поставщика на другой домен требует проверки совместимости.

Timeout, HTTP 408/5xx записи, оборванный или невалидный ответ могут означать выполненную операцию. Worker повторяет полную сверку через API с backoff, до MaxRetries, без слепого повторения исходного запроса и без блокировки email. Если состояние уже совпадает, записи нет. После исчерпания попыток событие отмечается Uncertain. Следующее событие или обычный retry также читает фактические теги и членство. Exactly-once и мгновенную согласованность внешнего API это не гарантирует. Applied snapshots не используются.
