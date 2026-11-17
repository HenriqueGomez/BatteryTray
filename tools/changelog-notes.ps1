# Extrai do CHANGELOG.md a seção de uma versão e grava como notas de Release.
# Uso: .\tools\changelog-notes.ps1 1.1.0 notas.md
# Usado pelo GitHub Actions (release.yml); falha se a versão não tiver seção ou se ela estiver vazia.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$OutFile,
    [string]$Repo = 'HenriqueGomez/BatteryTray'
)
$ErrorActionPreference = 'Stop'
$Version = $Version.TrimStart('v', 'V') # aceita "1.1.0" ou a tag "v1.1.0"
$changelog = Join-Path (Split-Path $PSScriptRoot -Parent) 'CHANGELOG.md'
$md = [IO.File]::ReadAllText($changelog) -replace "`r`n", "`n"

# "## [1.1.0] - data" até o próximo "## [" (ou o fim do arquivo)
$pattern = '(?ms)^## \[' + [Regex]::Escape($Version) + '\][^\n]*\n(.*?)(?=^## \[|\z)'
$m = [Regex]::Match($md, $pattern)
if (-not $m.Success) { throw "CHANGELOG.md não tem a seção ## [$Version]" }

$body = [Regex]::Replace($m.Groups[1].Value, '(?s)<!--.*?-->', '').Trim()
if (-not $body) { throw "A seção ## [$Version] do CHANGELOG.md está vazia" }

$notes = $body + "`n`n---`n" +
         "[Ver todas as novidades](https://github.com/$Repo/blob/main/CHANGELOG.md) · " +
         "[Como instalar](https://github.com/$Repo#instalação)`n"
[IO.File]::WriteAllText($OutFile, $notes, (New-Object Text.UTF8Encoding $false))
Write-Host "Notas da versão $Version gravadas em $OutFile"
