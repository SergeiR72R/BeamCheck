<#
  Puts the built package on the network share so colleagues can install it with one double-click.
    .\deploy\build.ps1                                   (once, builds dist\)
    .\deploy\publish.ps1 -Share "\\server\share\BeamCheck"
  Copies BeamCheck.bundle, install.ps1, install.cmd and writes VERSION (build tag + time). Colleagues start install.cmd
  from the share (the same double-click installs an update).
#>
param(
    [Parameter(Mandatory = $true)][string]$Share
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root "dist"
if (-not (Test-Path (Join-Path $dist "BeamCheck.bundle"))) { throw "dist\BeamCheck.bundle fehlt: zuerst .\deploy\build.ps1 ausführen." }
$tag = "unknown"
$plugin = Get-Content (Join-Path $root "src\BeamCheck.Cad.Shared\Plugin.cs") -Raw
if ($plugin -match 'BuildTag\s*=\s*"([^"]+)"') { $tag = $Matches[1] }
New-Item -ItemType Directory -Force -Path $Share | Out-Null
# new version first, VERSION last: a client that sees the new VERSION finds the complete package
robocopy (Join-Path $dist "BeamCheck.bundle") (Join-Path $Share "BeamCheck.bundle") /MIR /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
Copy-Item (Join-Path $dist "install.ps1") $Share -Force
Copy-Item (Join-Path $dist "install.cmd") $Share -Force
Set-Content -Path (Join-Path $Share "VERSION") -Value ("$tag " + (Get-Date -Format "yyyy-MM-dd HH:mm")) -Encoding ASCII
Write-Host "Veröffentlicht: $Share ($tag). Kollegen: install.cmd in diesem Ordner doppelklicken."
