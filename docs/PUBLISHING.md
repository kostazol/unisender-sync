# Выпуск версии и публикация в GHCR

Публичный репозиторий: [kostazol/unisender-sync](https://github.com/kostazol/unisender-sync).

Контейнер: `ghcr.io/kostazol/unisender-sync`. Публикация выполняется workflow [publish.yml](../.github/workflows/publish.yml) при событии **release → published**.

## Выпустить версию

1. Отправьте изменения в main и дождитесь зелёного CI.
2. Откройте **Releases → Draft a new release**.
3. Создайте тег `v1.0.0` на нужном коммите, задайте название и описание выпуска.
4. Для предварительной версии используйте, например, `v1.1.0-rc.1` и включите **Set as a pre-release**.
5. Нажмите **Publish release**, откройте **Actions → Publish container** и дождитесь результата.

Тег принимается в формате `vMAJOR.MINOR.PATCH` или без `v`, с необязательным суффиксом предварительной версии. Один push тега без публикации Release образ не выпускает.

Workflow сначала выполняет сборку и все тесты, затем собирает Linux-образы для amd64 и arm64 и публикует manifest в GHCR. Секреты Megaplan/Unisender для сборки не требуются и в workflow не передаются.

Для `v1.0.0` доступны:

```text
ghcr.io/kostazol/unisender-sync:1.0.0
ghcr.io/kostazol/unisender-sync:latest
```

Предварительные версии получают собственный тег; latest не обновляется. В Action summary также публикуется digest. Для воспроизводимого развёртывания указывайте конкретную версию или digest. Обновление уже запущенного контейнера выполняет оператор; выпуск образа сам по себе его не перезапускает.

## Права и видимость

Публикация использует встроенный GITHUB_TOKEN с `contents: read` и `packages: write`. Отдельный PAT в Repository secrets не требуется. SHA используемых Actions закреплены в файлах workflow.

После первой публикации проверьте **Packages → unisender-sync → Package settings → Change visibility → Public**. Публичный репозиторий сам по себе не гарантирует публичность пакета: первый образ GHCR может быть private. После изменения проверьте скачивание из неавторизованного Docker-клиента.

Публичный образ можно использовать без `docker login`. В Portainer вставьте [готовый Compose](../deploy/portainer/compose.yaml), замените обычные настройки и задайте три переменные с API-ключами и WebhookSecret. [Руководство Docker](DOCKER.md).

## Повтор и проверка

При ошибке откройте упавший job. После устранения причины используйте **Re-run failed jobs**; новую версию не нужно выпускать ради повторения той же сборки. Успешные тесты отдельно от успешной публикации: образ доступен только после зелёного job publish.

На сервере:

```bash
docker pull ghcr.io/kostazol/unisender-sync:1.0.0
```

Реальные настройки остаются в окружении развёртывания. Перед коммитом учитывайте `.gitignore`: локальные overrides, `.env`, User Secrets и `.local/` в репозиторий не отправляются.

[Публикация Docker-образов в GitHub Actions](https://docs.github.com/en/actions/tutorials/publish-packages/publish-docker-images) · [Container Registry](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry).
