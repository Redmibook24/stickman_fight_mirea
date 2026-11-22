# Прогон замеров практики 5: для каждого режима запускается хост (Listen Server) и N клиентов
# в headless-режиме, сервер пишет netprofile в CSV/JSON и сам завершается по окончании записи.
#
#   powershell -ExecutionPolicy Bypass -File tools/run_profile.ps1
#   powershell -ExecutionPolicy Bypass -File tools/run_profile.ps1 -Modes baseline,all -Duration 30
param(
    [string]$Exe = "Build/Prac5/prac5_replication.exe",
    [string]$Out = "docs/profiles",
    [string[]]$Modes = @("baseline", "grid", "dormancy", "push", "quantize", "frequency", "all", "everything"),
    [int]$Clients = 3,
    [int]$Duration = 60,
    [int]$Warmup = 8,
    [int]$Port = 7777
)

$ErrorActionPreference = "Stop"
$Modes = $Modes | ForEach-Object { $_ -split "," } | Where-Object { $_ }
$root = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $root $Exe
$outPath = Join-Path $root $Out
New-Item -ItemType Directory -Force $outPath | Out-Null
if (-not (Test-Path $exePath)) { throw "Нет билда $exePath. Соберите: Prac5 → Собрать Windows-билд" }

foreach ($mode in $Modes) {
    Write-Host "=== режим $mode ==="
    $common = @("-batchmode", "-nographics", "-p5opt", $mode, "-p5label", $mode, "-p5out", $outPath,
                "-p5warmup", $Warmup, "-p5duration", $Duration, "-p5clients", $Clients, "-p5port", $Port)

    $hostArgs = $common + @("-p5role", "host", "-logFile", (Join-Path $outPath "$mode`_server.log"))
    $server = Start-Process -FilePath $exePath -ArgumentList $hostArgs -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 2

    $clientProcs = @()
    for ($i = 1; $i -le $Clients; $i++) {
        $clientArgs = $common + @("-p5role", "client", "-logFile", (Join-Path $outPath "$mode`_client$i.log"))
        $clientProcs += Start-Process -FilePath $exePath -ArgumentList $clientArgs -PassThru -WindowStyle Hidden
        Start-Sleep -Milliseconds 700
    }

    $timeoutMs = ($Duration + $Warmup + 150) * 1000
    if (-not $server.WaitForExit($timeoutMs)) {
        Write-Warning "сервер не завершился вовремя — останавливаем"
        $server.Kill()
    }
    foreach ($p in $clientProcs) {
        if (-not $p.WaitForExit(15000)) { $p.Kill() }
    }
    Start-Sleep -Seconds 2
}

Write-Host "Готово. Результаты: $outPath"
