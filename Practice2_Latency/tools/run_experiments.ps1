<#
    Прогон всех сценариев эксперимента и сбор журнала docs/latency_samples.csv.

    Для каждого сценария поднимается свой сервер с нужными параметрами эмуляции сети,
    затем клиент отправляет заданное число PING. Старый журнал перезаписывается,
    поэтому результат прогона воспроизводим: одни и те же сценарии, один и тот же seed.

    Запуск из каталога Practice2_Latency:
        powershell -ExecutionPolicy Bypass -File tools/run_experiments.ps1
#>
param(
    [int]$Count = 50,
    [int]$Interval = 300,
    [int]$Timeout = 1000,
    [int]$Port = 9060,
    [int]$Seed = 20250920,
    [string]$Csv = "docs/latency_samples.csv"
)

$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $PSScriptRoot)

$scenarios = @(
    @{ Id = "baseline";  Emulate = "none" },
    @{ Id = "delay_50";  Emulate = "delay=50" },
    @{ Id = "delay_100"; Emulate = "delay=100" },
    @{ Id = "jitter";    Emulate = "delay=50,jitter=100" },
    @{ Id = "loss_5";    Emulate = "loss=5" },
    @{ Id = "combined";  Emulate = "delay=100,loss=5" }
)

Write-Host "Сборка в конфигурации Release..."
dotnet build -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Сборка завершилась с ошибкой." }

$serverExe = "server/bin/Release/net9.0/StickmanFight.Server.exe"
$clientExe = "client/bin/Release/net9.0/StickmanFight.Client.exe"

if (Test-Path $Csv) { Remove-Item $Csv }

foreach ($scenario in $scenarios) {
    Write-Host ""
    Write-Host "=== Сценарий $($scenario.Id): эмуляция «$($scenario.Emulate)» ===" -ForegroundColor Cyan

    # Сервер от прошлого сценария мог остаться жив (например, после Ctrl+C):
    # тогда порт занят, новый сервер не поднимется, а замеры молча уйдут к чужому процессу.
    Get-Process -Name "StickmanFight.Server" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 300

    $server = Start-Process -FilePath $serverExe `
        -ArgumentList @("--port", $Port, "--emulate", $scenario.Emulate, "--seed", $Seed, "--quiet") `
        -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 800

    if ($server.HasExited) {
        throw "Сервер для сценария $($scenario.Id) не запустился (порт $Port занят?)."
    }

    try {
        & $clientExe --port $Port --experiment $scenario.Id --count $Count `
            --interval $Interval --timeout $Timeout --csv $Csv
    }
    finally {
        if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
        Start-Sleep -Milliseconds 500
    }
}

Write-Host ""
Write-Host "Готово. Журнал измерений: $Csv" -ForegroundColor Green
