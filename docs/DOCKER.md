# Docker и Portainer

Готовый образ: `ghcr.io/kostazol/unisender-sync:1.0.0`, Linux amd64 и arm64. После настройки публичности пакета он скачивается без регистрации и docker login. Новые версии появляются при [выпуске GitHub Release](PUBLISHING.md).

Сначала подготовьте [Megaplan](MEGAPLAN.md) и [Unisender](UNISENDER.md).

## Сгенерировать WebhookSecret

```bash
docker run --rm --network none ghcr.io/kostazol/unisender-sync:1.0.0 --command generate-webhook-secret
```

Команда выводит 64 случайных hex-символа и ничего не сохраняет. Этот секрет также входит в URL webhook Megaplan.

## Portainer Stack

1. Выберите Docker environment → Stacks → Add stack → Web editor.
2. Вставьте [deploy/portainer/compose.yaml](../deploy/portainer/compose.yaml).
3. Замените адрес CRM и UUID приложения; при необходимости измените имена метки и списка.
4. В Environment variables задайте:

| Переменная | Значение |
| --- | --- |
| MEGAPLAN_API_TOKEN | API Token приложения Megaplan |
| UNISENDER_API_KEY | Ключ Unisender |
| WEBHOOK_SECRET | Результат генерации |

5. Нажмите Deploy the stack и откройте Logs контейнера.

Встроенные рабочие значения не дублируются в Compose: синхронизация и запись включены. Для проверки без записи добавьте в environment `Settings__Sync__DryRun: "true"`; список должен уже существовать.

Общая внешняя Docker-сеть, база данных и монтирования не нужны. Порт хоста 5080 направлен на 8080 контейнера. Для reverse proxy на том же хосте можно ограничить публикацию строкой `127.0.0.1:5080:8080`.

Для Swarm есть [compose.swarm.yaml](../deploy/portainer/compose.swarm.yaml). Секреты задаются теми же переменными Stack; используется одна реплика. Интерактивный attach ниже относится к обычному контейнеру Standalone.

## Docker Compose без Portainer

Из корня проекта скопируйте `.env.example` в `.env`, заполните три секрета и задайте обычные параметры в серверном примере:

```bash
docker compose --env-file .env -f deploy/portainer/compose.yaml up -d
docker compose --env-file .env -f deploy/portainer/compose.yaml logs -f --tail 50 sync
```

Если используете локальный `compose.override.yaml`, при явном `-f` добавьте его в список файлов. Примеры в Git используют `https://megaplan.example.com`; реальные настройки остаются в локальном override или окружении Stack.

Корневой Compose предназначен для сборки из исходников:

```bash
docker compose up -d --build
```

Dockerfile содержит только publish/runtime. Тесты выполняются отдельно; секреты для сборки не нужны. Процесс работает под UID 1654.

## Проверка, CLI и логи

Read-only проверка использует то же окружение контейнера:

```bash
docker exec <container-name> dotnet MegaplanUnisender.Api.dll --command check
```

Команды `status`, `events`, `sync <id>`, `retry <uuid>` доступны в stdin основного процесса:

```bash
docker attach --sig-proxy=false <container-name>
```

Для этого в Standalone-примере включены stdin_open и tty. Отсоединение без остановки — Ctrl+P, Ctrl+Q. Команда stop при restart: always может перезапустить контейнер; остановка выполняется через Docker/Portainer.

Serilog пишет JSON в Docker logs, Docker хранит до трёх файлов по 10 МБ. Для необязательного Graylog добавьте в environment:

```yaml
Secrets__GraylogAddress: graylog.example.com
Secrets__GraylogApplicationName: megaplan-unisender-sync-Production
```

Порт по умолчанию 12201; другой задаётся через Secrets__GraylogPort. GELF UDP дополняет консольный вывод.

## Обновление

Замените тег образа на нужную версию, скачайте её и пересоздайте контейнер. В Portainer обновите image в Stack и выполните Update the stack. Автоматическая публикация образа сама по себе рабочий контейнер не обновляет.

Очередь и история находятся в RAM и теряются при остановке. [Ограничения](REVIEW.md), [сценарии проверки](MANUAL_API_TESTS.md).
