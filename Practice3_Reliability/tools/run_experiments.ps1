<#
    Прогон шести серий надёжной доставки и сбор журнала docs/reliability_samples.csv.

    Потери задаются в обе стороны: клиент теряет часть своих команд, сервер теряет
    часть ACK. Задержка и джиттер добавляются только на стороне сервера, как в ПР №2,
    поэтому заданная задержка входит в RTT один раз.

    Журнал перезаписывается целиком. Логи сервера (в них строки [DUP] про отсечённые
    повторы) складываются в logs/ и в репозиторий не попадают.

    Запуск из каталога Practice3_Reliability:
        powershell -ExecutionPolicy Bypass -File tools/run_experiments.ps1
#>
param(
    [int]$Count = 50,
    [int]$Interval = 200,
    [int]$Tick = 10,
    [int]$MaxAttempts = 5,
    [int]$Port = 9070,
    [int]$ServerSeed = 20251002,
    [int]$ClientSeed = 20251003,
    [string]$Csv = "docs/reliability_samples.csv"
)

$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $PSScriptRoot)

$series = @(
    @{ Id = "baseline";         Client = "none";    Server = "none" },
    @{ Id = "loss_5";           Client = "loss=5";  Server = "loss=5" },
    @{ Id = "loss_10";          Client = "loss=10"; Server = "loss=10" },
    @{ Id = "loss_20";          Client = "loss=20"; Server = "loss=20" },
    @{ Id = "delay_100_loss_5"; Client = "loss=5";  Server = "delay=100,loss=5" },
    @{ Id = "jitter_loss_10";   Client = "loss=10"; Server = "delay=50,jitter=100,loss=10" }
)

Write-Host "Сборка в конфигурации Release..."
dotnet build -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Сборка завершилась с ошибкой." }

$serverExe = "server/bin/Release/net9.0/StickmanFight.Server.exe"
$clientExe = "client/bin/Release/net9.0/StickmanFight.Client.exe"

New-Item -ItemType Directory -Force "logs" | Out-Null
if (Test-Path $Csv) { Remove-Item $Csv }

$summary = @()

foreach ($s in $series) {
    Write-Host ""
    Write-Host "=== Серия $($s.Id): клиент «$($s.Client)», сервер «$($s.Server)» ===" -ForegroundColor Cyan

    # Сервер от прошлой серии мог остаться жив: тогда порт занят и команды уйдут к нему.
    Get-Process -Name "StickmanFight.Server" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 300

    $serverLog = "logs/server_$($s.Id).log"
    $server = Start-Process -FilePath $serverExe `
        -ArgumentList @("--port", $Port, "--emulate", $s.Server, "--seed", $ServerSeed, "--quiet") `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput $serverLog
    Start-Sleep -Milliseconds 800

    if ($server.HasExited) {
        throw "Сервер для серии $($s.Id) не запустился (порт $Port занят?)."
    }

    try {
        & $clientExe --port $Port --experiment $s.Id --count $Count --interval $Interval `
            --tick $Tick --max-attempts $MaxAttempts --emulate $s.Client --seed $ClientSeed --csv $Csv
    }
    finally {
        if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
        Start-Sleep -Milliseconds 500
    }

    $duplicates = (Select-String -Path $serverLog -Pattern "\[DUP\]" -Encoding UTF8 | Measure-Object).Count
    Write-Host "Сервер отсёк повторов: $duplicates" -ForegroundColor Magenta
    $summary += [pscustomobject]@{ Series = $s.Id; DuplicatesSuppressed = $duplicates }
}

Write-Host ""
Write-Host "Повторы, отсечённые окном дедупликации на сервере:" -ForegroundColor Green
$summary | Format-Table -AutoSize
Write-Host "Готово. Журнал доставки: $Csv" -ForegroundColor Green
