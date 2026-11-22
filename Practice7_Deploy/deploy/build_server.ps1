<#
    Сборка серверного Linux-билда и Windows-клиента ПР№7 без открытия редактора.

    Запуск из каталога Practice7_Deploy:
        powershell -ExecutionPolicy Bypass -File deploy/build_server.ps1

    Результат:
        Builds/LinuxServer/StickmanServer.x86_64   серверный билд, из него собирается Docker-образ
        Builds/WindowsClient/StickmanClient.exe    клиент для игроков
    Лог сборки: Builds/build.log
#>
param(
    [string]$UnityVersion = "6000.6.10f1"
)

$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $PSScriptRoot)

$unity = "C:\Program Files\Unity\Hub\Editor\$UnityVersion\Editor\Unity.exe"
if (-not (Test-Path $unity)) { throw "Не найден Unity $UnityVersion ($unity). Установите его с модулями Linux Build Support (Mono) и Linux Dedicated Server." }

New-Item -ItemType Directory -Force Builds | Out-Null
$log = Join-Path (Get-Location) "Builds\build.log"

# Windows не открывает файлы с путём длиннее 260 символов, а пакеты Unity лежат глубоко
# (Library\PackageCache\...). Если проект в папке с длинным именем, Unity молча не читает часть
# файлов пакетов. Поэтому сборка идёт через короткую ссылку (junction) на эту же папку:
# файлы не копируются, меняется только путь, по которому их видит Unity.
$projectPath = (Get-Location).Path
if ($projectPath.Length -gt 60) {
    $shortPath = Join-Path $env:SystemDrive "stickman_pr7"
    if (-not (Test-Path $shortPath)) {
        cmd /c mklink /J "$shortPath" "$projectPath" | Out-Null
    }
    if ((Get-Item $shortPath).Target -ne $projectPath) {
        throw "$shortPath уже существует и указывает на другую папку, удалите или переименуйте его."
    }
    Write-Host "Длинный путь проекта, сборка через ссылку $shortPath"
    $projectPath = $shortPath
}

Write-Host "Сборка через Unity $UnityVersion, лог: $log"
$p = Start-Process -FilePath $unity -Wait -PassThru -NoNewWindow -ArgumentList @(
    "-batchmode", "-quit", "-nographics",
    "-projectPath", "`"$projectPath`"",
    "-executeMethod", "Prac7.Editor.Prac7Build.BuildAll",
    "-logFile", "`"$log`""
)

Select-String -Path $log -Pattern "\[Prac7\]" | ForEach-Object { $_.Line }
if ($p.ExitCode -ne 0) { throw "Сборка завершилась с кодом $($p.ExitCode), подробности в $log" }
Write-Host "Готово." -ForegroundColor Green
