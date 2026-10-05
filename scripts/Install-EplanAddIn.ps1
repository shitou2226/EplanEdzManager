[CmdletBinding()]
param(
    [Parameter()]
    [string]$AddInDll = (Join-Path $PSScriptRoot '..\src\EplanEdzManager.EplanAddIn\bin\Release\net472\EplanEdzManager.EplanAddIn.dll'),

    [Parameter()]
    [string]$DesktopExecutable = (Join-Path $PSScriptRoot '..\src\EplanEdzManager.Desktop\bin\Release\net8.0-windows\EplanEdzManager.Desktop.exe'),

    [Parameter()]
    [string]$EplanExecutable,

    [Parameter()]
    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
$resolvedAddIn = (Resolve-Path -LiteralPath $AddInDll).Path
$resolvedDesktop = (Resolve-Path -LiteralPath $DesktopExecutable).Path

if ([System.IO.Path]::GetExtension($resolvedAddIn) -ne '.dll') { throw 'AddInDll must point to a DLL.' }
if ([System.IO.Path]::GetExtension($resolvedDesktop) -ne '.exe') { throw 'DesktopExecutable must point to an EXE.' }

$configPath = Join-Path ([System.IO.Path]::GetDirectoryName($resolvedAddIn)) 'EplanEdzManager.AddIn.json'
$config = [ordered]@{
    desktopExecutablePath = $resolvedDesktop
    connectTimeoutMilliseconds = 5000
}
$config | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding utf8

Write-Host "Prepared Add-In: $resolvedAddIn"
Write-Host "Prepared config: $configPath"
if ($PrepareOnly) { return }

if ([string]::IsNullOrWhiteSpace($EplanExecutable) -and -not [string]::IsNullOrWhiteSpace($env:EPLAN29_PLATFORM_BIN_DIR))
{
    $EplanExecutable = Join-Path $env:EPLAN29_PLATFORM_BIN_DIR 'EPLAN.exe'
}
if ([string]::IsNullOrWhiteSpace($EplanExecutable))
{
    throw 'Specify -EplanExecutable or set EPLAN29_PLATFORM_BIN_DIR. No developer-machine EPLAN path is assumed.'
}
$resolvedEplan = (Resolve-Path -LiteralPath $EplanExecutable).Path

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $resolvedEplan
$startInfo.UseShellExecute = $false
$startInfo.WorkingDirectory = [System.IO.Path]::GetDirectoryName($resolvedEplan)
$startInfo.Arguments = '/Variant:"Electric P8"'
[void][System.Diagnostics.Process]::Start($startInfo)

Write-Host ''
Write-Host 'EPLAN was started using the verified Electric P8 variant.'
Write-Host 'In EPLAN 2.9, open Utilities > API add-ins, choose Load/Register, and select:'
Write-Host "  $resolvedAddIn"
Write-Host 'This script intentionally does not modify the registry or copy files into the EPLAN installation.'
