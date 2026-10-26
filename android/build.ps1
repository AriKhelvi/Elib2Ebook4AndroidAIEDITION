<#
.SYNOPSIS
    Собирает подписанный APK «E2E для Android» из этого репозитория (форк Elib2Ebook + папка android/).

.DESCRIPTION
    [AI-EDITION] Скрипт сгенерирован ИИ (Claude, Anthropic) под присмотром человека.
    Стабильность не гарантируется — см. android/README.md.

    Что делает:
      1. (с -Pull) подтягивает свежие коммиты этого форка: git pull --ff-only;
      2. при первом запуске создаёт ключ подписи APK — ВНЕ репозитория (по умолчанию
         %LOCALAPPDATA%\Elib2EbookAndroid\signing). Ключ нельзя терять: без него новая версия
         не встанет поверх установленной — придётся удалять приложение вместе с настройками;
      3. чистит промежуточные файлы самого приложения (иначе инкрементальная сборка может не обновить
         versionCode или пропустить подпись) и собирает Release APK;
      4. кладёт готовый APK в android\dist\.

    Инструменты сборки ожидаются в %LOCALAPPDATA%\Elib2EbookAndroidToolchain (см. README: «Подготовка ПК»).

.PARAMETER Pull
    Перед сборкой выполнить git pull --ff-only (например, после кнопки «Sync fork» на GitHub).

.PARAMETER Abi
    Архитектуры через ';'. По умолчанию android-arm64 (Pixel 9a и почти все современные телефоны).
    Для эмулятора: -Abi "android-arm64;android-x64".

.PARAMETER SigningDir
    Папка с ключом подписи (e2e.keystore + password.txt). По умолчанию $env:E2E_SIGNING_DIR,
    а если она не задана — %LOCALAPPDATA%\Elib2EbookAndroid\signing.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\android\build.ps1 -Pull
#>
param(
    [switch]$Pull,
    [string]$Abi = 'android-arm64',
    [string]$SigningDir = $(if ($env:E2E_SIGNING_DIR) { $env:E2E_SIGNING_DIR } else { Join-Path $env:LOCALAPPDATA 'Elib2EbookAndroid\signing' })
)

$ErrorActionPreference = 'Stop'

# Корень репозитория — на уровень выше папки android/
$android = $PSScriptRoot
$repo = Split-Path $android -Parent

# --- 0. Инструменты ---------------------------------------------------------
$toolchain = Join-Path $env:LOCALAPPDATA 'Elib2EbookAndroidToolchain'
$dotnet = Join-Path $toolchain 'dotnet\dotnet.exe'
$jdk = Join-Path $toolchain 'jdk'
$sdk = Join-Path $toolchain 'android-sdk'

foreach ($path in @($dotnet, $jdk, $sdk)) {
    if (-not (Test-Path $path)) { throw "Не найден $path — сначала подготовьте ПК (см. android\README.md, «Подготовка ПК»)" }
}

# Свой .NET из папки тулчейна, без телеметрии и без поиска других установок .NET
$env:DOTNET_ROOT = Join-Path $toolchain 'dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_MULTILEVEL_LOOKUP = '0'

# --- 1. Исходники -----------------------------------------------------------
if ($Pull) {
    Write-Host '==> git pull' -ForegroundColor Cyan
    git -C $repo pull --ff-only
    if ($LASTEXITCODE -ne 0) { throw 'git pull не удался (есть локальные изменения или ветки разошлись)' }
}

$commit = (git -C $repo rev-parse --short HEAD).Trim()
# Версия ядра — из корневого Directory.Build.props (она же станет versionName приложения)
$props = [xml](Get-Content (Join-Path $repo 'Directory.Build.props') -Raw)
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
Write-Host "    Elib2Ebook $version ($commit)"

# --- 2. Ключ подписи (вне репозитория!) ------------------------------------
$keystore = Join-Path $SigningDir 'e2e.keystore'
$passwordFile = Join-Path $SigningDir 'password.txt'
if (-not (Test-Path $keystore)) {
    Write-Warning "Ключ подписи не найден в $SigningDir — создаю НОВЫЙ."
    Write-Warning 'APK с новым ключом не встанет поверх версии, подписанной старым ключом. Если старый ключ есть — укажите -SigningDir.'
    New-Item -ItemType Directory -Force $SigningDir | Out-Null
    # Случайный пароль из 24 символов без похожих букв/цифр
    $chars = [char[]]'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789'
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $password = -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
    [System.IO.File]::WriteAllText($passwordFile, $password)
    & (Join-Path $jdk 'bin\keytool.exe') -genkeypair -keystore $keystore -alias e2e -keyalg RSA -keysize 2048 `
        -validity 10000 -storepass $password -keypass $password -dname 'CN=E2E for Android' -noprompt
    if ($LASTEXITCODE -ne 0) { throw 'keytool не смог создать ключ' }
}

# apksigner не переваривает пробелы в пути «file:...» (например, C:\Users\Cave Cat), поэтому пароль —
# через переменную окружения, которая живёт только в этом процессе
$env:E2E_KS_PASS = (Get-Content $passwordFile -Raw).Trim()

# --- 3. Сборка --------------------------------------------------------------
# versionCode должен расти с каждой сборкой, иначе Android откажется ставить обновление.
# Берём число минут с 01.01.2026 — растёт само и влезает в int на десятилетия.
$versionCode = [int](([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - 1767225600) / 60)

# Инкрементальная упаковка не пересобирает манифест, если поменялся только versionCode, и может
# пропустить подпись после упавшего прогона — чистим промежуточные файлы самого приложения
# (библиотеки ядра остаются в кэше, пересобирается только APK).
foreach ($dir in @('E2E.Droid\obj\Release', 'E2E.Droid\bin\Release')) {
    $full = Join-Path $android $dir
    if (Test-Path $full) { Remove-Item -Recurse -Force $full }
}

Write-Host "==> Собираю APK (versionCode $versionCode, $Abi)" -ForegroundColor Cyan
# ВАЖНО: без -o и без -p:RuntimeIdentifiers — глобальные свойства командной строки уходят во все
# 60+ библиотек ядра и ломают их сборку. Архитектуры передаются через свойство проекта E2EAbi.
& $dotnet publish (Join-Path $android 'E2E.Droid\E2E.Droid.csproj') `
    -c Release -f net10.0-android -nologo -v:minimal `
    "-p:E2EAbi=$Abi" `
    "-p:AndroidSdkDirectory=$sdk" `
    "-p:JavaSdkDirectory=$jdk" `
    "-p:ApplicationDisplayVersion=$version" `
    "-p:ApplicationVersion=$versionCode" `
    "-p:E2ECommit=$commit" `
    '-p:AndroidKeyStore=true' `
    "-p:AndroidSigningKeyStore=$keystore" `
    '-p:AndroidSigningKeyAlias=e2e' `
    '-p:AndroidSigningKeyPass=env:E2E_KS_PASS' `
    '-p:AndroidSigningStorePass=env:E2E_KS_PASS'
if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась' }

$publishDir = Join-Path $android 'E2E.Droid\bin\Release\net10.0-android\publish'
$apk = Get-ChildItem $publishDir -Filter '*-Signed.apk' | Select-Object -First 1
if (-not $apk) { throw "Подписанный APK не найден в $publishDir" }

# --- 4. Результат ----------------------------------------------------------
$dist = Join-Path $android 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$target = Join-Path $dist "E2E-$version-$commit.apk"
Copy-Item $apk.FullName $target -Force
Write-Host ''
Write-Host "Готово: $target ($([math]::Round($apk.Length / 1MB, 1)) МБ)" -ForegroundColor Green
