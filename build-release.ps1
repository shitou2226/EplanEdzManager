[CmdletBinding()]
param(
    [switch]$IncludeEplanIntegration,
    [switch]$IncludeLocalFixtures,
    [switch]$SkipInstaller,
    [switch]$SkipInstallerSmoke,
    [switch]$SkipPortable,
    [string]$OutputRoot = (Join-Path $PSScriptRoot 'artifacts')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$versionDocument = [xml](Get-Content -LiteralPath (Join-Path $repositoryRoot 'Version.props') -Raw)
$versionPrefix = [string]$versionDocument.Project.PropertyGroup.VersionPrefix
$versionSuffix = [string]$versionDocument.Project.PropertyGroup.VersionSuffix
$productFileVersion = [string]$versionDocument.Project.PropertyGroup.FileVersion
if ([string]::IsNullOrWhiteSpace($versionPrefix)) { throw 'Version.props does not define VersionPrefix.' }
if ([string]::IsNullOrWhiteSpace($productFileVersion)) { throw 'Version.props does not define FileVersion.' }
$productVersion = if ([string]::IsNullOrWhiteSpace($versionSuffix)) { $versionPrefix } else { "$versionPrefix-$versionSuffix" }
$artifactRoot = [IO.Path]::GetFullPath($OutputRoot)
$releaseName = "EplanEdzManager-$productVersion-x64"
$stageRoot = Join-Path $artifactRoot $releaseName
$productRoot = Join-Path $stageRoot 'EplanEdzManager'
$gitCommit = (& git -C $repositoryRoot rev-parse --verify HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitCommit)) { throw 'Unable to resolve Git commit.' }
$trackedChanges = @(& git -C $repositoryRoot status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0) { throw 'Unable to verify Git working-tree status.' }
if ($trackedChanges.Count -gt 0) { throw 'Release builds require a clean tracked working tree. Commit or intentionally revert the listed changes first.' }

$privateBuildPaths = @(
    $repositoryRoot,
    [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile),
    [Environment]::GetFolderPath([Environment+SpecialFolder]::Desktop),
    [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
    [IO.Path]::GetFullPath($_).TrimEnd('\')
} | Select-Object -Unique

function Invoke-Checked {
    param([Parameter(Mandatory)][string]$FilePath, [Parameter(Mandatory)][string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FilePath failed with exit code $LASTEXITCODE." }
}

function Remove-ArtifactPath {
    param([Parameter(Mandatory)][string]$Path)
    $root = [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\')
    $target = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $target.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Cleanup target is not a strict descendant of the artifact directory.'
    }
    if (-not (Test-Path -LiteralPath $target)) { return }
    $cursor = Get-Item -LiteralPath $target -Force
    while ($null -ne $cursor -and $cursor.FullName.Length -ge $root.Length) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Artifact cleanup refuses reparse points.' }
        $cursor = if ($cursor -is [IO.DirectoryInfo]) { $cursor.Parent } else { $cursor.Directory }
    }
    Remove-Item -LiteralPath $target -Recurse -Force
}

function Invoke-IsolatedProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)][hashtable]$EnvironmentOverrides,
        [int[]]$AcceptedExitCodes = @(0)
    )
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$startInfo.ArgumentList.Add($argument) }
    foreach ($entry in $EnvironmentOverrides.GetEnumerator()) { $startInfo.Environment[$entry.Key] = [string]$entry.Value }
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) { throw "Unable to start isolated process: $FilePath" }
    $standardOutput = $process.StandardOutput.ReadToEndAsync()
    $standardError = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $output = $standardOutput.GetAwaiter().GetResult()
    $errorOutput = $standardError.GetAwaiter().GetResult()
    if ($process.ExitCode -notin $AcceptedExitCodes) {
        throw "$FilePath failed with exit code $($process.ExitCode).`n$output`n$errorOutput"
    }
    return [pscustomobject]@{ ExitCode = $process.ExitCode; StandardOutput = $output; StandardError = $errorOutput }
}

function Assert-NoPrivatePathsInFile {
    param([Parameter(Mandatory)][string]$FilePath)
    $bytes = [IO.File]::ReadAllBytes($FilePath)
    $utf8 = [Text.Encoding]::UTF8.GetString($bytes)
    $utf16 = [Text.Encoding]::Unicode.GetString($bytes)
    foreach ($privateRoot in $privateBuildPaths) {
        foreach ($variant in @($privateRoot, $privateRoot.Replace('\', '/'))) {
            if ($utf8.IndexOf($variant, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $utf16.IndexOf($variant, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw "Release file contains a private build path '$variant': $FilePath"
            }
        }
    }
}

function Assert-ReleaseLayout {
    param(
        [Parameter(Mandatory)][string]$Root,
        [switch]$AllowInstallerGeneratedPaths
    )
    $resolvedRoot = [IO.Path]::GetFullPath($Root)
    $files = @(Get-ChildItem -LiteralPath $resolvedRoot -File -Recurse)
    $proprietary = $files | Where-Object {
        $_.Name -like 'Eplan.EplApi.*.dll' -or
        $_.Name -like 'Eplan.EplApi.*.xml' -or
        $_.Extension -eq '.erx'
    }
    if ($proprietary) { throw 'Release safety gate found redistributed EPLAN proprietary material: ' + ($proprietary.FullName -join ', ') }
    $forbiddenDirectories = Get-ChildItem -LiteralPath $resolvedRoot -Directory -Recurse | Where-Object Name -In @('Tests', 'Samples', 'bin', 'obj')
    if ($forbiddenDirectories) { throw 'Release layout contains forbidden development directories: ' + ($forbiddenDirectories.FullName -join ', ') }
    $forbiddenFiles = $files | Where-Object {
        $_.Extension -in @('.pdb', '.edz', '.mdb', '.db', '.db-shm', '.db-wal', '.props', '.targets', '.ps1', '.cmd', '.bat')
    }
    if ($forbiddenFiles) { throw 'Release layout contains forbidden development/data files: ' + ($forbiddenFiles.FullName -join ', ') }

    foreach ($file in $files) {
        if ($AllowInstallerGeneratedPaths -and
            ($file.Name -eq 'EplanEdzManager.AddIn.json' -or $file.Name -like 'unins000.*')) { continue }
        Assert-NoPrivatePathsInFile $file.FullName
    }
}

function Copy-FilteredTree {
    param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination)
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) { throw "Missing source directory: $Source" }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -File -Recurse | Where-Object {
        $_.Name -notlike 'Eplan.EplApi.*.dll' -and
        $_.Name -notlike 'Eplan.EplApi.*.xml' -and
        $_.Name -ne 'EplanEdzManager.AddIn.json' -and
        $_.Extension -notin @('.pdb', '.edz', '.mdb', '.erx', '.db', '.db-shm', '.db-wal')
    } | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Source, $_.FullName)
        $target = Join-Path $Destination $relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    }
}

if (Test-Path -LiteralPath $stageRoot) {
    $resolvedArtifacts = [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\')
    $resolvedStage = [IO.Path]::GetFullPath($stageRoot).TrimEnd('\')
    if (-not $resolvedStage.StartsWith($resolvedArtifacts + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Stage cleanup target escaped the artifact root.' }
    Remove-ArtifactPath $resolvedStage
}
New-Item -ItemType Directory -Path $productRoot -Force | Out-Null

Invoke-Checked dotnet @('restore', (Join-Path $repositoryRoot 'EplanEdzManager.sln'))
Invoke-Checked dotnet @('restore', (Join-Path $repositoryRoot 'src\EplanEdzManager.Desktop\EplanEdzManager.Desktop.csproj'), '-r', 'win-x64')
Invoke-Checked dotnet @('build', (Join-Path $repositoryRoot 'EplanEdzManager.sln'), '-c', 'Release', '--no-restore', ('-p:SourceRevisionId=' + $gitCommit))

$regularTests = @(
    'EplanEdzManager.Core.Tests',
    'EplanEdzManager.Edz.Tests',
    'EplanEdzManager.Infrastructure.Sqlite.Tests',
    'EplanEdzManager.Application.Tests',
    'EplanEdzManager.EplanBridge.Client.Tests',
    'EplanEdzManager.EplanApi.Filter.Tests',
    'EplanEdzManager.EplanAddIn.Tests',
    'EplanEdzManager.AddIn.IntegrationTests'
)
foreach ($test in $regularTests) {
    $project = Join-Path $repositoryRoot "tests\$test\$test.csproj"
    $testArguments = @('test', $project, '-c', 'Release', '--no-build', '--no-restore', '--verbosity', 'minimal')
    if (-not $IncludeLocalFixtures) { $testArguments += @('--filter', 'Category!=Integration&Category!=LocalFixture') }
    Invoke-Checked dotnet $testArguments
}
if ($IncludeEplanIntegration) {
    $integrationProject = Join-Path $repositoryRoot 'tests\EplanEdzManager.EplanBridge.IntegrationTests\EplanEdzManager.EplanBridge.IntegrationTests.csproj'
    Invoke-Checked dotnet @('test', $integrationProject, '-c', 'Release', '--no-build', '--no-restore', '--verbosity', 'minimal')
}

$desktopPublish = Join-Path $artifactRoot '_publish\Desktop'
$toolsPublish = Join-Path $artifactRoot '_publish\Tools'
foreach ($path in @($desktopPublish, $toolsPublish)) {
    Remove-ArtifactPath $path
}
Invoke-Checked dotnet @('publish', (Join-Path $repositoryRoot 'src\EplanEdzManager.Desktop\EplanEdzManager.Desktop.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore', ('-p:SourceRevisionId=' + $gitCommit), '-o', $desktopPublish)
Copy-FilteredTree $desktopPublish (Join-Path $productRoot 'Desktop')
Copy-FilteredTree (Join-Path $repositoryRoot 'src\EplanEdzManager.EplanBridge\bin\Release\net472') (Join-Path $productRoot 'Bridge')
Copy-FilteredTree (Join-Path $repositoryRoot 'src\EplanEdzManager.EplanAddIn\bin\Release\net472') (Join-Path $productRoot 'AddIn')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'src\EplanEdzManager.EplanAddIn\EplanEdzManager.AddIn.example.json') -Destination (Join-Path $productRoot 'AddIn\EplanEdzManager.AddIn.example.json') -Force

foreach ($tool in @('EplanEdzIndex', 'EplanEdzProbe')) {
    $destination = Join-Path $toolsPublish $tool
    $toolProject = Join-Path $repositoryRoot "tools\$tool\$tool.csproj"
    Invoke-Checked dotnet @('restore', $toolProject, '-r', 'win-x64')
    Invoke-Checked dotnet @('publish', $toolProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore', ('-p:SourceRevisionId=' + $gitCommit), '-o', $destination)
    Copy-FilteredTree $destination (Join-Path $productRoot "Tools\$tool")
}
$releaseDocs = @(
    'USER_GUIDE.md',
    'TROUBLESHOOTING.md',
    'PRIVACY_AND_DATA.md',
    'SAFETY.md',
    'EPLAN_ADDIN_INSTALLATION.md',
    'PARTS_DATABASE_IMPORT_SAFETY.md',
    'CLEAN_MACHINE_TEST_CHECKLIST.md'
)
$releaseDocsRoot = Join-Path $productRoot 'Docs'
New-Item -ItemType Directory -Path $releaseDocsRoot -Force | Out-Null
foreach ($document in $releaseDocs) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot "docs\$document") -Destination (Join-Path $releaseDocsRoot $document) -Force
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination (Join-Path $productRoot 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.zh-CN.md') -Destination (Join-Path $productRoot 'README.zh-CN.md') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $productRoot 'THIRD_PARTY_NOTICES.md') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $productRoot 'LICENSE') -Force
Copy-FilteredTree (Join-Path $repositoryRoot 'docs\licenses') (Join-Path $releaseDocsRoot 'licenses')

Assert-ReleaseLayout $productRoot

$portablePath = Join-Path $artifactRoot "$releaseName.zip"
if (-not $SkipPortable) {
    Remove-ArtifactPath $portablePath
    Compress-Archive -LiteralPath $productRoot -DestinationPath $portablePath -CompressionLevel Optimal
    $zipScanRoot = Join-Path $artifactRoot '_zip-scan 中文 (x64)'
    Remove-ArtifactPath $zipScanRoot
    Expand-Archive -LiteralPath $portablePath -DestinationPath $zipScanRoot
    Assert-ReleaseLayout $zipScanRoot
    Remove-ArtifactPath $zipScanRoot
}

$portableSmokeRoot = Join-Path $artifactRoot '_portable-smoke 中文 (no-sdk)'
$portableSmokeResult = Join-Path $portableSmokeRoot '结果 output.json'
Remove-ArtifactPath $portableSmokeRoot
New-Item -ItemType Directory -Path $portableSmokeRoot -Force | Out-Null
$isolatedEnvironment = @{
    PATH = (Join-Path $env:SystemRoot 'System32') + ';' + $env:SystemRoot
    DOTNET_ROOT = (Join-Path $portableSmokeRoot 'missing-dotnet-runtime')
    DOTNET_MULTILEVEL_LOOKUP = '0'
    EPLAN29_PLATFORM_BIN_DIR = (Join-Path $portableSmokeRoot 'missing-eplan-platform')
    EPLAN29_VARIANT_BIN_DIR = (Join-Path $portableSmokeRoot 'missing-eplan-variant')
    EPLAN_EDZ_MANAGER_BRIDGE_PATH = (Join-Path $portableSmokeRoot 'missing-bridge.exe')
    TEMP = (Join-Path $portableSmokeRoot '临时 Temp')
    TMP = (Join-Path $portableSmokeRoot '临时 Temp')
}
New-Item -ItemType Directory -Path $isolatedEnvironment.TEMP -Force | Out-Null
$portableDesktop = Join-Path $productRoot 'Desktop\EplanEdzManager.Desktop.exe'
[void](Invoke-IsolatedProcess $portableDesktop @('--smoke-test', $portableSmokeResult) $isolatedEnvironment)
$portableSmokeJson = Get-Content -LiteralPath $portableSmokeResult -Raw -Encoding utf8 | ConvertFrom-Json
if (-not $portableSmokeJson.passed -or $portableSmokeJson.noEplanMode -ne 'Offline') {
    throw 'Portable no-SDK/no-EPLAN smoke test did not pass in Offline Mode.'
}
foreach ($tool in @('EplanEdzIndex', 'EplanEdzProbe')) {
    $toolExecutable = Join-Path $productRoot "Tools\$tool\$tool.exe"
    [void](Invoke-IsolatedProcess $toolExecutable @() $isolatedEnvironment @(2))
}
Remove-ArtifactPath $portableSmokeRoot

$setupPath = Join-Path $artifactRoot "EplanEdzManager-$productVersion-x64-Setup.exe"
if (-not $SkipInstaller) {
    $isccCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $iscc = if ($null -ne $isccCommand) { $isccCommand.Source } else { $null }
    if (-not $iscc) {
        $iscc = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup *\ISCC.exe'), 'C:\Program Files (x86)\Inno Setup *\ISCC.exe', 'C:\Program Files\Inno Setup *\ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $iscc) { throw 'Inno Setup compiler ISCC.exe was not found. Install Inno Setup or use -SkipInstaller for a non-release local package.' }
    Invoke-Checked $iscc @(('/DReleaseRoot=' + $stageRoot), ('/DOutputDir=' + $artifactRoot), ('/DProductVersion=' + $productVersion), ('/DProductFileVersion=' + $productFileVersion), (Join-Path $repositoryRoot 'installer\EplanEdzManager.iss'))
    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) { throw 'Installer compiler completed but the setup artifact was not found.' }
    Assert-NoPrivatePathsInFile $setupPath
}

if (-not $SkipInstaller -and -not $SkipInstallerSmoke) {
    $smokeInstall = Join-Path $artifactRoot '_installer-smoke 中文 (x64)'
    $smokeResult = Join-Path $artifactRoot '_installer-smoke 中文 (x64)-result.json'
    Remove-ArtifactPath $smokeInstall
    Remove-ArtifactPath $smokeResult
    $installArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/DIR="{0}"' -f $smokeInstall))
    $install = Start-Process -FilePath $setupPath -ArgumentList $installArguments -WindowStyle Hidden -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "Installer smoke install failed with exit code $($install.ExitCode)." }
    Assert-ReleaseLayout $smokeInstall -AllowInstallerGeneratedPaths
    $installedDesktop = Join-Path $smokeInstall 'Desktop\EplanEdzManager.Desktop.exe'
    $installedAddInConfig = Join-Path $smokeInstall 'AddIn\EplanEdzManager.AddIn.json'
    $addInConfig = Get-Content -LiteralPath $installedAddInConfig -Raw -Encoding utf8 | ConvertFrom-Json
    if (-not [string]::Equals($addInConfig.desktopExecutablePath, $installedDesktop, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installed Add-In configuration did not contain the real UTF-8 Desktop executable path.'
    }
    $installedSmokeEnvironment = $isolatedEnvironment.Clone()
    $installedSmokeEnvironment.TEMP = Join-Path $artifactRoot '_installer-temp 中文 (x64)'
    $installedSmokeEnvironment.TMP = $installedSmokeEnvironment.TEMP
    New-Item -ItemType Directory -Path $installedSmokeEnvironment.TEMP -Force | Out-Null
    [void](Invoke-IsolatedProcess $installedDesktop @('--smoke-test', $smokeResult) $installedSmokeEnvironment)
    if (-not (Test-Path -LiteralPath $smokeResult)) { throw 'Installed Desktop offline smoke test failed.' }
    $smokeJson = Get-Content -LiteralPath $smokeResult -Raw | ConvertFrom-Json
    if (-not $smokeJson.passed -or $smokeJson.noEplanMode -ne 'Offline') { throw 'Installed Desktop smoke result did not pass in Offline Mode.' }
    $uninstaller = Join-Path $smokeInstall 'unins000.exe'
    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -WindowStyle Hidden -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) { throw "Installer smoke uninstall failed with exit code $($uninstall.ExitCode)." }
    $uninstallDeadline = [DateTime]::UtcNow.AddSeconds(5)
    while ((Test-Path -LiteralPath $smokeInstall) -and ([DateTime]::UtcNow -lt $uninstallDeadline)) {
        Start-Sleep -Milliseconds 100
    }
    if (Test-Path -LiteralPath $smokeInstall) { throw 'Installer smoke uninstall left application files in the installation directory.' }
    foreach ($temporary in @($smokeResult, (Join-Path $artifactRoot 'smoke-index.db'), (Join-Path $artifactRoot 'smoke-index.db-shm'), (Join-Path $artifactRoot 'smoke-index.db-wal'))) {
        Remove-ArtifactPath $temporary
    }
    Remove-ArtifactPath $installedSmokeEnvironment.TEMP
}

$publishRoot = Join-Path $artifactRoot '_publish'
Remove-ArtifactPath $publishRoot
$hashTargets = Get-ChildItem -LiteralPath $artifactRoot -File | Where-Object { $_.Name -in @("$releaseName.zip", "EplanEdzManager-$productVersion-x64-Setup.exe") }
$hashLines = foreach ($file in $hashTargets | Sort-Object Name) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    "$hash *$($file.Name)"
}
Set-Content -LiteralPath (Join-Path $artifactRoot 'SHA256SUMS.txt') -Value $hashLines -Encoding ascii
Write-Host "Release artifacts completed: $artifactRoot"
Write-Host "Version: $productVersion; Git: $gitCommit; EPLAN integration included: $IncludeEplanIntegration"
