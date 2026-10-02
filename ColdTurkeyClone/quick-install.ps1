# quick-install.ps1
#
# Alternativa rapida ao Inno Setup, pensada pra testes: publica os dois
# projetos e copia tudo pra uma pasta de destino ja na estrutura que o app
# espera (App\ e Watchdog\ lado a lado), depois abre o app elevado.
#
# Uso (a partir da pasta raiz do projeto, a que tem o ColdTurkeyClone.sln):
#   .\quick-install.ps1
#   .\quick-install.ps1 -Destination "C:\ColdTurkeyCloneTeste"
#   .\quick-install.ps1 -SkipPublish   # reusa o publish ja existente, so copia
#
# OBS: este arquivo usa apenas caracteres ASCII de proposito (sem acentos,
# sem travessao "--", etc). O PowerShell 5.1 padrao do Windows le arquivos
# .ps1 sem BOM UTF-8 usando a codificacao ANSI do sistema, o que corrompia
# qualquer acento aqui antes de uma correcao anterior.

param(
    [string]$Destination = "$env:LOCALAPPDATA\ColdTurkeyCloneTest",
    [switch]$SkipPublish
)

# ---- Auto-elevacao ----
# Precisa ser admin pra parar o servico do Watchdog antes de sobrescrever os
# arquivos dele. Se o script nao estiver rodando elevado, ele se relanca
# elevado (um prompt do UAC vai aparecer) e encerra a instancia nao-elevada.
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Este script precisa ser admin (pra parar o servico antes de sobrescrever os arquivos)." -ForegroundColor Yellow
    Write-Host "Reabrindo elevado..." -ForegroundColor Yellow
    $argList = @('-ExecutionPolicy', 'Bypass', '-File', "`"$($MyInvocation.MyCommand.Path)`"")
    if ($Destination) { $argList += @('-Destination', "`"$Destination`"") }
    if ($SkipPublish) { $argList += '-SkipPublish' }
    Start-Process PowerShell -Verb RunAs -ArgumentList $argList
    exit
}

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Write-Step($msg) { Write-Host ">> $msg" -ForegroundColor Cyan }

# ---- Encerra qualquer instancia antiga antes de mexer em arquivos ----
# Isso evita o erro classico de "Copy-Item : O processo nao pode acessar o
# arquivo porque ele esta sendo usado por outro processo": o app minimiza
# pra bandeja em vez de fechar de verdade, entao uma instancia de teste
# anterior pode continuar rodando (e com os DLLs dela carregados/travados)
# quando voce roda este script de novo.
Write-Step "Encerrando instancias anteriores (se houver)..."
try {
    Stop-Service -Name "ColdTurkeyCloneWatchdog" -Force -ErrorAction SilentlyContinue
} catch { }
foreach ($procName in @("ColdTurkeyClone", "Watchdog")) {
    Get-Process -Name $procName -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            Write-Host "   Encerrando $procName (PID $($_.Id))..."
            Stop-Process -Id $_.Id -Force -ErrorAction Stop
        } catch {
            Write-Host "   Nao consegui encerrar $procName (PID $($_.Id)): $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}
Start-Sleep -Seconds 2

if (-not $SkipPublish) {
    Write-Step "Publicando App (self-contained win-x64)..."
    dotnet publish (Join-Path $root "App") -c Release -r win-x64 --self-contained true
    if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar App" }

    Write-Step "Publicando Watchdog (self-contained win-x64)..."
    dotnet publish (Join-Path $root "Watchdog") -c Release -r win-x64 --self-contained true
    if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar Watchdog" }
}

$appPublish = Join-Path $root "App\bin\Release\net8.0-windows\win-x64\publish"
$watchdogPublish = Join-Path $root "Watchdog\bin\Release\net8.0-windows\win-x64\publish"

if (-not (Test-Path $appPublish)) { throw "Nao achei $appPublish. Rode sem -SkipPublish primeiro." }
if (-not (Test-Path $watchdogPublish)) { throw "Nao achei $watchdogPublish. Rode sem -SkipPublish primeiro." }

Write-Step "Copiando pra $Destination ..."
New-Item -ItemType Directory -Force -Path "$Destination\App" | Out-Null
New-Item -ItemType Directory -Force -Path "$Destination\Watchdog" | Out-Null

Copy-Item "$appPublish\*" "$Destination\App" -Recurse -Force
Copy-Item "$watchdogPublish\*" "$Destination\Watchdog" -Recurse -Force

if (Test-Path (Join-Path $root "BrowserExtension")) {
    Copy-Item (Join-Path $root "BrowserExtension") "$Destination\BrowserExtension" -Recurse -Force
}
if (Test-Path (Join-Path $root "ExtensionSigningKey")) {
    Copy-Item (Join-Path $root "ExtensionSigningKey") "$Destination\ExtensionSigningKey" -Recurse -Force
}

Write-Step "Pronto. Estrutura final:"
Write-Host "  $Destination\App\ColdTurkeyClone.exe"
Write-Host "  $Destination\Watchdog\Watchdog.exe"
Write-Host ""
Write-Step "Abrindo o app (ja estamos elevados, sem precisar de -Verb RunAs de novo)..."
Start-Process (Join-Path "$Destination\App" "ColdTurkeyClone.exe")
