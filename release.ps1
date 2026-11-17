# Publica uma nova versão do BatteryTray.
# Uso (no PowerShell, dentro desta pasta):  .\release.ps1 1.1.0
# Atualiza Updater.Version no código, compila para conferir, faz o commit, cria a tag v1.1.0 e envia.
# O GitHub Actions gera o Release; o app instalado em outros PCs mostra "Atualizar para v1.1.0" no menu.
param([Parameter(Mandatory = $true)][string]$Version)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Use o formato 1.2.3" }
if (git tag --list "v$Version") { throw "A tag v$Version já existe. Use um número maior." }

$file = Join-Path $PSScriptRoot 'BatteryTray.cs'
$exe = Join-Path $PSScriptRoot 'BatteryTray.exe'
$src = [IO.File]::ReadAllText($file)
$new = [Regex]::Replace($src, 'public const string Version = "[^"]+";', "public const string Version = `"$Version`";")
if ($new -notmatch [Regex]::Escape("public const string Version = `"$Version`";")) { throw "Não encontrei Updater.Version no código" }
[IO.File]::WriteAllText($file, $new, (New-Object Text.UTF8Encoding $false))

# o BatteryTray roda a partir desta pasta: fecha para poder recompilar o .exe
$running = Get-Process BatteryTray -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) { Write-Host "Fechando o BatteryTray para recompilar..."; $running | Stop-Process -Force; Start-Sleep -Milliseconds 800 }

try
{
    # compila (avisos contam como erro) para garantir que está tudo certo antes de publicar
    cmd /c "`"$PSScriptRoot\build.cmd`""
    if ($LASTEXITCODE -ne 0) { throw "A compilação falhou; nada foi enviado" }

    git add -A
    git commit -m "Versão $Version"
    git tag "v$Version"
    git push
    git push origin "v$Version"
    Write-Host "Enviado. Acompanhe em: https://github.com/HenriqueGomez/BatteryTray/actions"
}
finally
{
    if ($running -and (Test-Path $exe)) { Start-Process $exe -WorkingDirectory $PSScriptRoot; Write-Host "BatteryTray reaberto." }
}
