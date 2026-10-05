[CmdletBinding()]
param(
    [Parameter()]
    [string]$AddInDll = (Join-Path $PSScriptRoot '..\src\EplanEdzManager.EplanAddIn\bin\Release\net472\EplanEdzManager.EplanAddIn.dll'),

    [Parameter()]
    [string]$EplanExecutable,

    [Parameter()]
    [switch]$RemoveConfiguration
)

$ErrorActionPreference = 'Stop'
$resolvedAddIn = (Resolve-Path -LiteralPath $AddInDll).Path
$configPath = Join-Path ([System.IO.Path]::GetDirectoryName($resolvedAddIn)) 'EplanEdzManager.AddIn.json'

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

Write-Host 'In EPLAN 2.9, open Utilities > API add-ins and remove/unregister:'
Write-Host "  $resolvedAddIn"
Write-Host 'Close EPLAN after confirming that the Add-In is no longer listed.'
if ($RemoveConfiguration -and (Test-Path -LiteralPath $configPath))
{
    Remove-Item -LiteralPath $configPath -Force
    Write-Host "Removed Add-In config: $configPath"
}
Write-Host 'No EPLAN installation files or registry keys were modified by this script.'
