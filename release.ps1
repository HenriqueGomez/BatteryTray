# Publica uma nova versão do BatteryTray.
# Uso (no PowerShell, dentro desta pasta):  .\release.ps1 1.1.0
# Antes: escreva as mudanças no CHANGELOG.md, na seção [Não publicado].
# O script transforma essa seção em "[1.1.0] - data", atualiza Updater.Version no código,
# compila para conferir, faz o commit, cria a tag v1.1.0 e envia.
# O GitHub Actions gera o Release; o app instalado em outros PCs mostra "Atualizar para v1.1.0" no menu.
param([Parameter(Mandatory = $true)][string]$Version)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Use o formato 1.2.3" }
if (git tag --list "v$Version") { throw "A tag v$Version já existe. Use um número maior." }

# CHANGELOG.md: a seção [Não publicado] vira "[versão] - data" (são as notas que aparecem no Release)
$clFile = Join-Path $PSScriptRoot 'CHANGELOG.md'
$cl = [IO.File]::ReadAllText($clFile) -replace "`r`n", "`n"
if ($cl -notmatch ('(?m)^## \[' + [Regex]::Escape($Version) + '\]'))
{
    $m = [Regex]::Match($cl, '(?ms)^## \[Não publicado\][^\n]*\n(.*?)(?=^## \[|\z)')
    if (-not $m.Success) { throw "O CHANGELOG.md não tem a seção ## [Não publicado]" }
    $pending = [Regex]::Replace($m.Groups[1].Value, '(?s)<!--.*?-->', '').Trim()
    if (-not $pending) { throw "Antes de publicar, escreva o que mudou no CHANGELOG.md, na seção ## [Não publicado]." }
    $date = Get-Date -Format 'yyyy-MM-dd'
    $cl = $cl.Substring(0, $m.Index) + "## [Não publicado]`n`n## [$Version] - $date`n`n$pending`n`n" + $cl.Substring($m.Index + $m.Length)
    [IO.File]::WriteAllText($clFile, $cl, (New-Object Text.UTF8Encoding $false))
}
# confere com a mesma lógica que o GitHub Actions usa para montar as notas
& (Join-Path $PSScriptRoot 'tools\changelog-notes.ps1') $Version (Join-Path $env:TEMP 'BatteryTray-notes.md') | Out-Null

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

    function Invoke-Git { & git.exe @args; if ($LASTEXITCODE -ne 0) { throw "Falhou: git $($args -join ' ')" } }
    Invoke-Git add -A
    Invoke-Git commit -m "Versão $Version"
    Invoke-Git tag "v$Version"
    Invoke-Git push
    Invoke-Git push origin "v$Version"
    Write-Host "Enviado. Acompanhe em: https://github.com/HenriqueGomez/BatteryTray/actions"
}
finally
{
    if ($running -and (Test-Path $exe)) { Start-Process $exe -WorkingDirectory $PSScriptRoot; Write-Host "BatteryTray reaberto." }
}
