# Настройка и способы запуска

Сначала подготовьте [Megaplan](MEGAPLAN.md) и [Unisender](UNISENDER.md). Для любой установки нужны адрес CRM, UUID приложения, два API-ключа и секрет webhook. Имена метки и списка указаны в примерах и могут быть изменены. Остальные ID утилита получает автоматически.

## Выбрать способ

| Способ | Руководство | Логи |
| --- | --- | --- |
| Терминал / Visual Studio | [Консольная утилита](CONSOLE.md) | Консоль, необязательный Graylog |
| Фоновая служба Windows | [Windows Service](WINDOWS_SERVICE.md) | Неинтерактивный процесс, необязательный Graylog |
| Фоновая служба Linux | [systemd](LINUX_SERVICE.md) | journal, необязательный Graylog |
| Контейнер | [Docker / Portainer](DOCKER.md) | Docker logs, необязательный Graylog |

На одну пару аккаунтов запускайте один экземпляр. База данных, общий volume и внешняя Docker-сеть не нужны.

## Общие параметры

```json
{
  "Settings": {
    "Megaplan": {
      "BaseUrl": "https://megaplan.example.com",
      "ExpectedIntegrationUuid": "8f15d4ec-32e7-48c6-a420-637b5822d52e",
      "EligibilityGlobalTagName": "Unisender"
    },
    "Unisender": {
      "ListName": "Мегаплан"
    }
  }
}
```

Замените домен и UUID своими. JSON хранится в `appsettings.Local.json` рядом с исполняемым файлом. При сборке из исходников файл лежит в каталоге проекта API и копируется в build output; в publish он не включается.

Для окружения используются эквивалентные имена с `__`: `Settings__Megaplan__BaseUrl`, `Settings__Megaplan__ExpectedIntegrationUuid`, `Secrets__MegaplanApiToken`, `Secrets__UnisenderApiKey`, `Secrets__WebhookSecret`.

В основном appsettings уже заданы рабочие политики: Enabled=true, DryRun=false, полное управление тегами и подтверждённое администратором согласие. Их не нужно повторять в Compose. Для первой проверки без записи задайте `Settings.Sync.DryRun=true`; список Unisender должен существовать.

## WebhookSecret

```powershell
.\MegaplanUnisender.Api.exe --command generate-webhook-secret
```

Или из готового контейнерного образа:

```bash
docker run --rm --network none ghcr.io/kostazol/unisender-sync:1.0.1 --command generate-webhook-secret
```

Команда печатает 64 случайных hex-символа, не требует API и ничего не сохраняет. Используйте результат как WebhookSecret и часть URL события. При смене секрета обновите URL в Megaplan и перезапустите приложение.

## Сеть, команды и локальные файлы

Для webhook нужен адрес, доступный серверу Megaplan. Публичный HTTPS и reverse proxy — вариант публикации, а не обязательная инфраструктура. По умолчанию Windows слушает 127.0.0.1:5080; явный адрес задаётся через `--urls`. Docker-пример публикует 5080 → 8080.

Команды `status`, `events`, `sync`, `retry` относятся к памяти основного процесса и вводятся в его терминале. Отдельные `check`, `tags`, `lists`, `contact` не запускают второй webhook. В службах интерактивный CLI выключен: для диагностики доступен отдельный `--command check`, а состояние обработки видно по логам. [Справка CLI](CLI.md).

Локальные `appsettings.Local.json`, `.env`, `compose.override.yaml` и `.local/` исключены из Git и Docker-контекста. Если явно перечисляете Compose-файлы через `-f`, добавьте локальный override явно. Полученные ID и данные контактов на диск не сохраняются.

Полный справочник — [CONFIGURATION.md](CONFIGURATION.md), приёмочная проверка — [MANUAL_API_TESTS.md](MANUAL_API_TESTS.md).
