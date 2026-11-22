<#
    Проверка сервера, развёрнутого на VM (deploy/deploy_to_vm.sh, профиль steam).
    Клиент запускается на этом ПК, находит сессию поиском Steam в локальной сети и подключается к VM.
    Результат: docs/evidence/06_vm_virtualbox/*.log, публичный IP и имя Steam-аккаунта скрыты.

    Запуск из каталога Practice7_Deploy (параметры по умолчанию для VirtualBox с NAT):
        powershell -ExecutionPolicy Bypass -File deploy/test_vm.ps1
        powershell -ExecutionPolicy Bypass -File deploy/test_vm.ps1 -Vm user@203.0.113.10 -SshPort 22 -SshKey C:\keys\vm
#>
param(
    [string]$Vm = "user@127.0.0.1",
    [int]$SshPort = 2222,
    [string]$SshKey = "$env:USERPROFILE\.ssh\adcm_vm",
    # VirtualBox поверх Hyper-V: привязка к одному ядру (см. deploy/compose.vbox.yaml). Для облачной VM: -ExtraCompose ""
    [string]$ExtraCompose = " -f deploy/compose.vbox.yaml"
)

$ErrorActionPreference = "Continue"
Set-Location (Split-Path -Parent $PSScriptRoot)

$client = (Resolve-Path "Builds\WindowsClient\StickmanClient.exe").Path
$dir = "docs\evidence\06_vm_virtualbox"
$compose = "cd ~/stickman-pr7 && docker compose -f deploy/compose.yaml -f deploy/compose.steam.yaml$ExtraCompose"

function Vm([string]$command) {
    # Потоки склеиваются на стороне VM: в PowerShell 5.1 «2>&1» превратил бы stderr в записи об ошибках.
    return (& ssh -i $SshKey -p $SshPort -o BatchMode=yes $Vm "$command 2>&1" | Out-String)
}

function Mask([string]$text) {
    $text = $text -replace "публичный IP \S+", "публичный IP <скрыт>"
    $text = $text -replace "пользователь .+ \(\d{17}\)", "пользователь <скрыт>"
    return $text
}

function Start-Client([string]$name) {
    $log = Join-Path (Resolve-Path $dir) "$name.raw.log"
    $argList = @("-autojoin-lan", "-autopilot", "-screen-width", "640", "-screen-height", "360", "-logFile", "`"$log`"")
    Start-Process $client -ArgumentList $argList | Out-Null
}

function Wait-Log([string]$name, [string]$pattern, [int]$seconds) {
    $file = Join-Path $dir "$name.raw.log"
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $file) -and ((Get-Content $file -Raw -Encoding UTF8 -ErrorAction SilentlyContinue) -match $pattern)) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Wait-Vm([string]$pattern, [int]$count, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if ([regex]::Matches((Vm "docker logs stickman-server"), $pattern).Count -ge $count) { return }
        Start-Sleep 1
    }
}

function Stop-Clients { Get-Process StickmanClient -ErrorAction SilentlyContinue | Stop-Process -Force }

if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
New-Item -ItemType Directory -Force $dir | Out-Null
Stop-Clients

# Чистый старт: контейнер пересоздаётся, счётчик перезапусков обнуляется.
Vm "$compose up -d --no-build --force-recreate" | Out-Null
Wait-Vm "опубликована" 1 60
$start = (Vm "date -u +%Y-%m-%dT%H:%M:%SZ").Trim()
$info = "VM: " + (Vm ". /etc/os-release && echo `$PRETTY_NAME, Docker `$(docker --version | cut -d' ' -f3 | tr -d ,)").Trim()

# 1. Клиент находит сессию на VM поиском Steam и подключается.
Start-Client "client_before_crash"
Wait-Log "client_before_crash" "в игре" 40 | Out-Null
Start-Sleep 5

# 2. Аварийное завершение процесса сервера: Docker поднимает контейнер, сервер заново публикует сессию.
Vm "docker exec stickman-server sh -c 'kill -9 `$(pgrep -f StickmanServer.x86_64)'" | Out-Null
Wait-Log "client_before_crash" "ошибка" 45 | Out-Null
Wait-Vm "опубликована" 2 60
$restart = "RestartCount = " + (Vm "docker inspect -f '{{.RestartCount}}' stickman-server").Trim()
Stop-Clients

# 3. Повторный поиск и вход после перезапуска сервера.
Start-Client "client_after_restart"
Wait-Log "client_after_restart" "в игре" 40 | Out-Null
Start-Sleep 5

# 4. Штатная остановка: игрок получает сообщение, сессия снимается с публикации.
$sw = [Diagnostics.Stopwatch]::StartNew()
Vm "$compose stop" | Out-Null
$stop = "docker compose stop: $([int]$sw.Elapsed.TotalSeconds) с, ExitCode = " + (Vm "docker inspect -f '{{.State.ExitCode}}' stickman-server").Trim()
Wait-Log "client_after_restart" "ошибка" 30 | Out-Null
Stop-Clients
Start-Sleep 1

@($info, $restart, $stop, "Пользователь процесса: " + ((Vm "docker inspect -f '{{.Config.User}}' stickman-server") -replace "\s", "")) |
    Set-Content (Join-Path $dir "docker_inspect.txt") -Encoding UTF8
$server = Vm "docker logs --since $start stickman-server"
$server = ($server -split "`n" | Where-Object { $_ -notmatch "memorysetup-|^\s*$" }) -join "`n"
Set-Content (Join-Path $dir "server.log") (Mask $server) -Encoding UTF8
Get-ChildItem $dir -Filter "*.raw.log" | ForEach-Object {
    $lines = Get-Content $_.FullName -Encoding UTF8 | Where-Object { $_ -match "\[Session|\[Steam|\[SteamSockets|\[Prac4\]" }
    Set-Content -Path ($_.FullName -replace "\.raw\.log$", ".log") -Value (Mask ($lines -join "`n")) -Encoding UTF8
    Remove-Item $_.FullName
}

# Сервер снова запускается: после проверки он должен оставаться доступным для игроков.
Vm "$compose up -d --no-build" | Out-Null
Write-Host "Готово: $dir, сервер на VM снова запущен" -ForegroundColor Green
