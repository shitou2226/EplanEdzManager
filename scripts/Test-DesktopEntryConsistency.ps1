[CmdletBinding()]
param(
    [string]$AddInConfigPath = (Join-Path $env:LOCALAPPDATA 'Programs\EplanEdzManager\AddIn\EplanEdzManager.AddIn.json'),
    [string]$ShortcutPath = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'EplanEdzManager.Desktop.lnk')
)

$ErrorActionPreference = 'Stop'
$failures = [System.Collections.Generic.List[string]]::new()

if (-not (Test-Path -LiteralPath $AddInConfigPath -PathType Leaf)) {
    $failures.Add("Add-In 配置不存在：$AddInConfigPath")
}
if (-not (Test-Path -LiteralPath $ShortcutPath -PathType Leaf)) {
    $fallback = Join-Path ([Environment]::GetFolderPath('Desktop')) 'EPLAN EDZ Manager.lnk'
    if (Test-Path -LiteralPath $fallback -PathType Leaf) {
        $ShortcutPath = $fallback
    }
    else {
        $failures.Add("桌面快捷方式不存在：$ShortcutPath")
    }
}

$desktopFromAddIn = $null
$shortcutTarget = $null
$shortcutIcon = $null
if ($failures.Count -eq 0) {
    $config = Get-Content -LiteralPath $AddInConfigPath -Raw -Encoding utf8 | ConvertFrom-Json
    $desktopFromAddIn = [IO.Path]::GetFullPath([string]$config.desktopExecutablePath)
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcutTarget = [IO.Path]::GetFullPath([string]$shortcut.TargetPath)
    $shortcutIcon = [string]$shortcut.IconLocation

    if (-not [string]::Equals($desktopFromAddIn, $shortcutTarget, [StringComparison]::OrdinalIgnoreCase)) {
        $failures.Add('桌面快捷方式与 EPLAN Add-In 启动的不是同一份程序。')
    }
    if (-not (Test-Path -LiteralPath $desktopFromAddIn -PathType Leaf)) {
        $failures.Add("Add-In 指向的桌面程序不存在：$desktopFromAddIn")
    }
    if ([string]::IsNullOrWhiteSpace($shortcutIcon) -or $shortcutIcon.StartsWith(',', [StringComparison]::Ordinal)) {
        $failures.Add('桌面快捷方式没有显式图标路径。')
    }
}

[pscustomobject]@{
    Passed = $failures.Count -eq 0
    AddInDesktop = $desktopFromAddIn
    Shortcut = $ShortcutPath
    ShortcutTarget = $shortcutTarget
    ShortcutIcon = $shortcutIcon
    Failures = $failures.ToArray()
} | ConvertTo-Json -Depth 4

if ($failures.Count -gt 0) { exit 1 }
