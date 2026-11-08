param(
    [ValidateRange(1024,65535)][int]$Port = 5080,
    [string]$Command = 'run'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src/MegaplanUnisender.Api/MegaplanUnisender.Api.csproj'
& (Join-Path $PSScriptRoot 'Initialize-Local.ps1')
Push-Location $repoRoot
try {
    dotnet run --project $projectPath --launch-profile LocalWindows -- --command $Command --urls "http://127.0.0.1:$Port"
    $commandExitCode = $LASTEXITCODE
}
finally { Pop-Location }
exit $commandExitCode
