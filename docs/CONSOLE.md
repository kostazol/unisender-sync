# Консольная утилита и Visual Studio

Для сборки из исходников нужен .NET SDK 10. Для framework-dependent сборки на другом компьютере нужен ASP.NET Core Runtime 10; self-contained publish включает runtime.

## Сборка на Windows

Из корня репозитория:

```powershell
Copy-Item appsettings.example.json src/MegaplanUnisender.Api/appsettings.Local.json
dotnet restore MegaplanUnisender.sln --configfile NuGet.Config
dotnet build MegaplanUnisender.sln -c Release
.\src\MegaplanUnisender.Api\bin\Release\net10.0\MegaplanUnisender.Api.exe --command generate-webhook-secret
```

Заполните обычные параметры в скопированном JSON по [RUNNING.md](RUNNING.md). Если локальный файл уже существует, сохраните его настройки вместо повторного копирования.

Откройте **Manage User Secrets** проекта API и заполните секцию Secrets по [шаблону](../secrets.example.json): MegaplanApiToken, UnisenderApiKey и сгенерированный WebhookSecret. Из терминала можно использовать `dotnet user-secrets --project src/MegaplanUnisender.Api`; путь к внутреннему хранилищу не требуется.

## Проверка и запуск

```powershell
.\src\MegaplanUnisender.Api\bin\Release\net10.0\MegaplanUnisender.Api.exe --command check
.\src\MegaplanUnisender.Api\bin\Release\net10.0\MegaplanUnisender.Api.exe
```

В локальном примере включён dry-run. Для записи установите `Settings.Sync.DryRun=false` и пересоберите, чтобы обновлённый JSON скопировался рядом с exe.

Без аргументов запускаются webhook и CLI в этом окне. По умолчанию на Windows адрес 127.0.0.1:5080. Если Megaplan находится на другом сервере и подключается напрямую, выберите сетевой адрес:

```powershell
.\MegaplanUnisender.Api.exe --urls http://0.0.0.0:5080
```

В основном терминале доступны `help`, `status`, `events`, `sync <id>`, `retry <uuid>`, `configuration`, `stop`. Остановка также Ctrl+C. JSON-логи выводятся в консоль; Graylog включается только при заданном адресе. [CLI](CLI.md), [логи](OPERATIONS.md).

## Visual Studio

Откройте `MegaplanUnisender.sln`, выберите проект `MegaplanUnisender.Api` и профиль `LocalWindows`. F5 — отладка, Ctrl+F5 — обычный запуск.

README, Dockerfile и Compose доступны в Solution Items; документы и скрипты — в папках решения. Новые общие файлы добавляйте через **Add → Existing Item**. Если `.sln` изменился снаружи, примите перезагрузку решения.

## Отдельная папка или Linux

```powershell
dotnet publish src/MegaplanUnisender.Api -c Release -o .artifacts/publish
```

Создайте `appsettings.Local.json` рядом с опубликованным приложением. Секреты передайте через окружение `Secrets__...` либо используйте локальный User Secrets. На Windows запускается exe, на Linux:

```bash
dotnet MegaplanUnisender.Api.dll --urls http://0.0.0.0:5080
```

Для автоматического запуска после загрузки ОС используйте [Windows Service](WINDOWS_SERVICE.md) или [systemd](LINUX_SERVICE.md).
