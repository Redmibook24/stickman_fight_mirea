<#
    Локальный прогон всех проверок ПР№7: сервер в Docker, клиенты Windows-билда на этом ПК.
    Результат: docs/evidence/<сценарий>/*.log, публичный IP и имя Steam-аккаунта в логах скрыты.

    Перед запуском:
      1. собраны билды: powershell -ExecutionPolicy Bypass -File deploy/build_server.ps1
      2. запущен Docker Desktop;
      3. для Steam-сценариев запущен Steam с вошедшим пользователем.

    Запуск из каталога Practice7_Deploy:
        powershell -ExecutionPolicy Bypass -File deploy/test_local.ps1
        powershell -ExecutionPolicy Bypass -File deploy/test_local.ps1 -SkipSteam
#>
param(
    [switch]$SkipSteam
)

# Continue, а не Stop: docker пишет прогресс в stderr, и PowerShell 5.1 при Stop считал бы это
# ошибкой. Настоящие сбои скрипт выбрасывает сам через throw.
$ErrorActionPreference = "Continue"
Set-Location (Split-Path -Parent $PSScriptRoot)

$client = (Resolve-Path "Builds\WindowsClient\StickmanClient.exe").Path
$evidence = "docs\evidence"
$ipCompose = @("-f", "deploy/compose.yaml")
$steamCompose = @("-f", "deploy/compose.yaml", "-f", "deploy/compose.steam.yaml")
$container = "stickman-server"

function Mask([string]$text) {
    # Публичный IP сервера и имя Steam-аккаунта в отчёт не попадают.
    $text = $text -replace "публичный IP \S+", "публичный IP <скрыт>"
    $text = $text -replace "пользователь .+ \(\d{17}\)", "пользователь <скрыт>"
    return $text
}

function Get-ServerLog([string]$since = "") {
    # Unity в контейнере пишет часть строк в stderr. Потоки склеивает cmd: в PowerShell 5.1
    # «2>&1» превратил бы каждую строку stderr в запись об ошибке с путём к скрипту.
    $opt = if ($since) { "--since $since" } else { "" }
    return (cmd /c "docker logs $opt $container 2>&1" | Out-String)
}

function Start-Server([string[]]$compose) {
    docker compose @compose up -d --build 2>&1 | Out-Null
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        if ((Get-ServerLog) -match "READY") { return }
        Start-Sleep 1
    }
    throw "Сервер не вывел READY за 60 с"
}

function Start-Client([string]$name, [string[]]$extra, [string]$dir) {
    $log = Join-Path (Resolve-Path $dir) "$name.raw.log"
    # Путь в кавычках: Start-Process склеивает аргументы через пробел, а в пути к логу есть пробелы.
    $argList = $extra + @("-screen-width", "640", "-screen-height", "360", "-logFile", "`"$log`"")
    return Start-Process $client -ArgumentList $argList -PassThru
}

function Wait-Log([string]$file, [string]$pattern, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        # Unity пишет лог в UTF-8, а PowerShell 5.1 без -Encoding читает файл как ANSI.
        if ((Test-Path $file) -and ((Get-Content $file -Raw -Encoding UTF8 -ErrorAction SilentlyContinue) -match $pattern)) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Save-Evidence([string]$dir) {
    # Клиенты закрываются заранее: Windows не даёт удалить лог, открытый работающим процессом.
    Stop-Clients
    Start-Sleep 1
    # Лог сервера целиком (без служебного вывода Unity о памяти), логи клиентов - только события.
    # Только записи этого сценария: контейнер общий для нескольких сценариев подряд.
    $server = Get-ServerLog $script:ScenarioStart
    $server = ($server -split "`n" | Where-Object { $_ -notmatch "memorysetup-|^\s*$" }) -join "`n"
    Set-Content -Path (Join-Path $dir "server.log") -Value (Mask $server) -Encoding UTF8
    Get-ChildItem $dir -Filter "*.raw.log" | ForEach-Object {
        $lines = Get-Content $_.FullName -Encoding UTF8 | Where-Object { $_ -match "\[Session|\[Steam|\[SteamSockets|\[Prac4\]" }
        Set-Content -Path ($_.FullName -replace "\.raw\.log$", ".log") -Value (Mask ($lines -join "`n")) -Encoding UTF8
        Remove-Item $_.FullName
    }
    Write-Host "  сохранено: $dir"
}

function New-Scenario([string]$name) {
    $dir = Join-Path $evidence $name
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    New-Item -ItemType Directory -Force $dir | Out-Null
    $script:ScenarioStart = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
    Write-Host "== $name" -ForegroundColor Cyan
    return $dir
}

function Stop-Clients { Get-Process StickmanClient -ErrorAction SilentlyContinue | Stop-Process -Force }

try {
    # ── 1. IP-профиль: два клиента, уход одного клиента, падение и автоперезапуск сервера ──
    $d = New-Scenario "01_ip_two_clients_crash"
    Start-Server $ipCompose
    $c1 = Start-Client "client1" @("-connect", "127.0.0.1:7777", "-autopilot") $d
    Start-Sleep 3
    Start-Client "client2" @("-connect", "127.0.0.1:7777", "-autopilot") $d | Out-Null
    Wait-Log "$d\client2.raw.log" "в игре" 40 | Out-Null
    Start-Sleep 10
    $c1 | Stop-Process -Force          # клиент 1 закрыт аварийно, без прощания с сервером
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and -not ((Get-ServerLog) -match "отключился")) { Start-Sleep 1 }
    # Аварийное завершение процесса сервера: контейнер должен подняться сам, клиент 2 - получить сообщение.
    docker exec $container sh -c 'kill -9 $(pgrep -f StickmanServer.x86_64)'
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and ([int](docker inspect $container --format '{{.RestartCount}}')) -lt 1) { Start-Sleep 1 }
    Wait-Log "$d\client2.raw.log" "ошибка" 45 | Out-Null
    Start-Sleep 3
    "RestartCount = $(docker inspect $container --format '{{.RestartCount}}'), State = $(docker inspect $container --format '{{.State.Status}}')" |
        Set-Content (Join-Path $d "docker_inspect.txt") -Encoding UTF8
    Save-Evidence $d

    # ── 2. Серверная авторитетность: рывок чаще кулдауна у клиента, игнорирующего кулдаун ──
    $d = New-Scenario "02_ip_authority_dash"
    Start-Client "cheater" @("-connect", "127.0.0.1:7777", "-autopilot", "-autodash", "-cheat-cooldown") $d | Out-Null
    Wait-Log "$d\cheater.raw.log" "в игре" 40 | Out-Null
    Start-Sleep 15
    Save-Evidence $d

    # ── 3. Штатная остановка при подключённом клиенте ──
    $d = New-Scenario "03_ip_graceful_stop"
    Start-Client "client" @("-connect", "127.0.0.1:7777") $d | Out-Null
    Wait-Log "$d\client.raw.log" "в игре" 40 | Out-Null
    $sw = [Diagnostics.Stopwatch]::StartNew()
    docker compose @ipCompose stop 2>&1 | Out-Null
    "docker compose stop: $([int]$sw.Elapsed.TotalSeconds) с, ExitCode = $(docker inspect $container --format '{{.State.ExitCode}}')" |
        Set-Content (Join-Path $d "docker_inspect.txt") -Encoding UTF8
    Wait-Log "$d\client.raw.log" "ошибка" 30 | Out-Null
    Save-Evidence $d
    docker compose @ipCompose down 2>&1 | Out-Null

    if ($SkipSteam) { return }

    # ── 4. Steam-профиль: публикация, поиск, подключение двух клиентов, выход и повторный вход ──
    $d = New-Scenario "04_steam_session"
    Start-Server $steamCompose
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and -not ((Get-ServerLog) -match "опубликована")) { Start-Sleep 1 }
    # Поиск через интернет: мастер-сервер Steam возвращает нашу сессию, но опрос сервера
    # по публичному IP из той же домашней сети роутер не пропускает (см. отчёт).
    Start-Client "internet_search" @("-autojoin") $d | Out-Null
    Wait-Log "$d\internet_search.raw.log" "поиск завершён|ошибка" 30 | Out-Null
    Stop-Clients
    Start-Client "client1_rejoin" @("-autojoin-lan", "-rejoin", "-autopilot") $d | Out-Null
    Start-Sleep 4
    Start-Client "client2" @("-autojoin-lan", "-autopilot") $d | Out-Null
    Wait-Log "$d\client1_rejoin.raw.log" "id \d+[\s\S]*в игре[\s\S]*в игре" 60 | Out-Null
    Start-Sleep 3
    Save-Evidence $d

    # ── 5. Steam: падение сервера, повторная регистрация, штатное завершение сессии ──
    $d = New-Scenario "05_steam_restart_and_stop"
    Stop-Clients
    Start-Client "client" @("-autojoin-lan") $d | Out-Null
    Wait-Log "$d\client.raw.log" "в игре" 40 | Out-Null
    docker exec $container sh -c 'kill -9 $(pgrep -f StickmanServer.x86_64)'
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and ([regex]::Matches((Get-ServerLog), "опубликована").Count) -lt 2) { Start-Sleep 1 }
    Wait-Log "$d\client.raw.log" "ошибка" 40 | Out-Null
    Stop-Clients
    Start-Client "client_after_restart" @("-autojoin-lan") $d | Out-Null
    Wait-Log "$d\client_after_restart.raw.log" "в игре" 40 | Out-Null
    docker compose @steamCompose stop 2>&1 | Out-Null
    Wait-Log "$d\client_after_restart.raw.log" "ошибка" 30 | Out-Null
    Save-Evidence $d
}
finally {
    Stop-Clients
    docker compose @steamCompose down 2>&1 | Out-Null
}
Write-Host "Готово, доказательства в $evidence" -ForegroundColor Green
