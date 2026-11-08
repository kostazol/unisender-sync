$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src/MegaplanUnisender.Api/MegaplanUnisender.Api.csproj'
$localSettingsPath = Join-Path $repoRoot 'src/MegaplanUnisender.Api/appsettings.Local.json'
if (!(Test-Path -LiteralPath $localSettingsPath)) {
    $settings = Get-Content -LiteralPath (Join-Path $repoRoot 'appsettings.example.json') -Raw | ConvertFrom-Json -AsHashtable
    $settings | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $localSettingsPath -Encoding utf8
}
$secretJson = dotnet user-secrets list --json --project $projectPath
if ($LASTEXITCODE -ne 0) { throw 'Could not read .NET User Secrets' }
try {
    $storedSecrets = (($secretJson | Where-Object { $_ -notmatch '^\s*//' }) -join [Environment]::NewLine) | ConvertFrom-Json -AsHashtable
} catch { throw 'Could not parse .NET User Secrets output' }
$missing = @{}
foreach ($key in @('MegaplanApiToken', 'UnisenderApiKey', 'WebhookSecret')) {
    $flatKey = 'Secrets:' + $key
    $nestedHasKey = $storedSecrets.ContainsKey('Secrets') -and $storedSecrets.Secrets.ContainsKey($key)
    $value = if ($storedSecrets.ContainsKey($flatKey)) { $storedSecrets[$flatKey] } elseif ($nestedHasKey) { $storedSecrets.Secrets[$key] } else { '' }
    if ($key -eq 'WebhookSecret') {
        if ([string]::IsNullOrWhiteSpace($value)) { $missing[$flatKey] = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) }
    } elseif (!$storedSecrets.ContainsKey($flatKey) -and !$nestedHasKey) { $missing[$flatKey] = '' }
}
if ($missing.Count -gt 0) {
    $missing | ConvertTo-Json -Depth 3 | dotnet user-secrets set --project $projectPath
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize .NET User Secrets' }
}
Write-Output "Settings: $localSettingsPath"
Write-Output 'User Secrets: Manage User Secrets in Visual Studio or dotnet user-secrets --project src/MegaplanUnisender.Api'
