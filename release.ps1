# Publica uma nova versão do BatteryTray.
# Uso:  .\release.ps1 1.1.0
# Atualiza Updater.Version no código, compila para conferir, faz o commit, cria a tag v1.1.0 e envia.
# O GitHub Actions gera o Release; o app instalado mostra "Atualizar para v1.1.0" no menu.
param([Parameter(Mandatory = $true)][string]$Version)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Use o formato 1.2.3" }
if (git status --porcelain -- BatteryTray.cs | Select-String '^\?\?') { throw "BatteryTray.cs não está no git" }

$file = Join-Path $PSScriptRoot 'BatteryTray.cs'
$src = [IO.File]::ReadAllText($file)
$new = [Regex]::Replace($src, 'public const string Version = "[^"]+";', "public const string Version = `"$Version`";")
if ($new -eq $src -and $src -notmatch "Version = `"$Version`"") { throw "Não encontrei Updater.Version no código" }
[IO.File]::WriteAllText($file, $new, (New-Object Text.UTF8Encoding $true))

# compila só para garantir que está tudo certo antes de publicar
cmd /c "`"$PSScriptRoot\build.cmd`""
if ($LASTEXITCODE -ne 0) { throw "A compilação falhou (feche o BatteryTray se ele estiver aberto a partir desta pasta)" }

git add -A
git commit -m "Versão $Version"
git tag "v$Version"
git push
git push origin "v$Version"
Write-Host "Enviado. Acompanhe em: https://github.com/HenriqueGomez/BatteryTray/actions"
