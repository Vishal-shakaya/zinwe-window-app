<#
.SYNOPSIS
  Test, publish and package the Zinwe Windows agent.

.EXAMPLE
  .\build.ps1                    # tests + publish + artifacts\installer\ZinweAgent.msi
  .\build.ps1 -Version 0.2.0
  .\build.ps1 -SkipInstaller     # publish only
#>
param(
    [string]$Version = "0.1.0",
    [switch]$SkipTests,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

function Invoke-Step([string]$Name, [scriptblock]$Body) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Body
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit $LASTEXITCODE)." }
}

$publish = Join-Path $PSScriptRoot "artifacts\publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

if (-not $SkipTests) {
    Invoke-Step "Test" { dotnet test Zinwe.Agent.slnx -c Release }
}

Invoke-Step "Publish service" {
    dotnet publish src\Zinwe.Agent.Service -c Release -p:Version=$Version -o "$publish\service"
}
Invoke-Step "Publish tray" {
    dotnet publish src\Zinwe.Agent.Tray -c Release -p:Version=$Version -o "$publish\tray"
}

if (-not $SkipInstaller) {
    Invoke-Step "Build MSI" {
        dotnet build installer\Zinwe.Agent.Installer.wixproj -c Release -p:ProductVersion=$Version -o artifacts\installer
    }
    Write-Host "MSI: $(Join-Path $PSScriptRoot 'artifacts\installer\ZinweAgent.msi')" -ForegroundColor Green
    Write-Host "Unsigned build -- sign before giving it to anyone (see README, 'Code signing')." -ForegroundColor Yellow
}
