#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Invoke-Dotnet {
    param([Parameter(Mandatory)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
}

$buildProjects = @(
    'src/EplanEdzManager.Desktop/EplanEdzManager.Desktop.csproj',
    'tools/EplanEdzIndex/EplanEdzIndex.csproj',
    'tools/EplanEdzProbe/EplanEdzProbe.csproj'
)
$testNames = @(
    'EplanEdzManager.Core.Tests',
    'EplanEdzManager.Edz.Tests',
    'EplanEdzManager.Infrastructure.Sqlite.Tests',
    'EplanEdzManager.Application.Tests',
    'EplanEdzManager.EplanBridge.Client.Tests',
    'EplanEdzManager.EplanAddIn.Tests',
    'EplanEdzManager.AddIn.IntegrationTests'
)

foreach ($relative in $buildProjects) {
    $project = Join-Path $repositoryRoot $relative
    Invoke-Dotnet @('restore', $project)
    Invoke-Dotnet @('build', $project, '-c', 'Release', '--no-restore', '--verbosity', 'minimal')
}
foreach ($name in $testNames) {
    $project = Join-Path $repositoryRoot "tests/$name/$name.csproj"
    Invoke-Dotnet @('restore', $project)
    Invoke-Dotnet @('test', $project, '-c', 'Release', '--no-restore', '--filter', 'Category!=Integration&Category!=LocalFixture', '--verbosity', 'minimal')
}
Write-Host 'PASS: Desktop/tools build and offline synthetic-data tests. No EPLAN-dependent project was built.'
