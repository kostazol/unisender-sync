# Запуск как служба Windows

Приложение поддерживает Windows Service через Microsoft.Extensions.Hosting.WindowsServices. В обычном терминале оно остаётся консольной утилитой. Для установки службы нужны права администратора.

## Подготовить сборку

На машине с .NET SDK 10 из корня проекта:

```powershell
dotnet publish src/MegaplanUnisender.Api -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .artifacts/win-x64
```

Перенесите содержимое `.artifacts/win-x64` в `C:\Services\megaplan-unisender-sync`. Такая сборка включает runtime. Для другой архитектуры замените RID.

Создайте рядом с exe `appsettings.Local.json` по [RUNNING.md](RUNNING.md). Он не включается в publish автоматически. Секреты задаются окружением службы; User Secrets вашей учётной записи службе не нужны.

## Зарегистрировать службу

В PowerShell от администратора:

```powershell
$binary = '"C:\Services\megaplan-unisender-sync\MegaplanUnisender.Api.exe" --urls http://0.0.0.0:5080'
New-Service -Name 'megaplan-unisender-sync' -DisplayName 'Megaplan → Unisender' -BinaryPathName $binary -StartupType Automatic
```

Выберите адрес прослушивания под свою сеть. Не запускайте второй экземпляр на том же порту.

Перед первым стартом задайте окружение только этой службы. Замените значения примеров:

```powershell
$serviceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\megaplan-unisender-sync'
$serviceEnvironment = @(
  'Secrets__MegaplanApiToken=CHANGE-ME-MEGAPLAN-TOKEN'
  'Secrets__UnisenderApiKey=CHANGE-ME-UNISENDER-KEY'
  'Secrets__WebhookSecret=CHANGE-ME-GENERATED-WEBHOOK-SECRET'
)
New-ItemProperty -Path $serviceKey -Name Environment -PropertyType MultiString -Value $serviceEnvironment -Force
```

WebhookSecret можно получить до установки командой `MegaplanUnisender.Api.exe --command generate-webhook-secret`. В этот же массив при необходимости добавляются `Secrets__GraylogAddress` и `Secrets__GraylogApplicationName`. Обычные Settings также можно передать окружением вместо JSON.

Если в реестре уже есть Environment, сохраните нужные существующие элементы: приведённая команда заменяет весь массив службы. Ограничьте доступ к каталогу приложения и настройкам службы средствами Windows.

## Проверка и управление

Для отдельной проверки API запустите `--command check` из терминала с теми же Settings и Secrets. Процесс проверки не наследует окружение службы из реестра: задайте переменные в его терминале либо используйте локальный User Secrets.

```powershell
Start-Service megaplan-unisender-sync
Get-Service megaplan-unisender-sync
Restart-Service megaplan-unisender-sync
Stop-Service megaplan-unisender-sync
```

Служба автоматически запускается после загрузки Windows. Для старта после аварий можно настроить вкладку **Recovery** в `services.msc`.

В службе интерактивного терминала и команд status/events/sync/retry нет. Для ручной диагностики остановите службу и запустите приложение в консоли. Serilog сохраняет настройку JSON stdout и необязательного Graylog; SCM не предоставляет окно консоли. Для централизованного чтения прикладных логов службы используйте Graylog. [Настройка логов](OPERATIONS.md).

## Обновление

Остановите службу, замените опубликованные файлы, сохранив локальную конфигурацию, затем запустите её снова. Очередь в RAM не сохраняется при остановке. Переменные окружения службы применяются при новом запуске.

[Рекомендации Microsoft](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service).
