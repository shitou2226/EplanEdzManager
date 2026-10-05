#Requires -Version 7.0
[CmdletBinding()]
param([switch]$RequireLicense)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$paths = @(& git -C $repositoryRoot -c core.quotepath=false ls-files --cached)
if ($LASTEXITCODE -ne 0 -or $paths.Count -eq 0) { throw 'Stage the intended public files before running this gate.' }
$issues = [Collections.Generic.List[string]]::new()
$keywordFiles = [Collections.Generic.List[string]]::new()
$privateRoots = @([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile), $repositoryRoot) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$privateUser = [Environment]::UserName
$tokenPattern = '(?i)(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{20,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----)'
$pathPattern = '(?i)[a-z]:[\\/](?:Users|eplan)(?:[\\/]|$)'
$keywordPattern = '(?i)password|token|secret|apikey|private[ _-]?key|connection[ _-]?string|authorization|bearer'

foreach ($relative in $paths) {
    if ($relative -match '(?i)(^|/)(?:bin|obj|\.git|\.vs|\.idea|artifacts|output|temp|tmp|publish|samples|eplan-api|logs|crashes|crash-reports|backups|LocalAppData|AppData|TestResults)(/|$)' -or
        ($relative -match '(?i)(^|/)diagnostics(/|$)' -and $relative -notmatch '^src/') -or
        $relative -match '(?i)\.(?:dll|exe|edz|mdb|erx|pdb|db|db-shm|db-wal|sqlite|dmp|log|trx|pem|key|pfx|p12)$' -or
        $relative -match '(?i)(?:Eplan\.EplApi\.|Directory\.Build\.props\.local$|\.local\.(?:props|targets|json|config)$|(^|/)\.env(?:\.|$)|\.(?:user|suo)$)') {
        $issues.Add("Forbidden tracked file: $relative")
        continue
    }
    $fullPath = Join-Path $repositoryRoot $relative
    $item = Get-Item -LiteralPath $fullPath
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        $issues.Add("Linked/reparse input is not allowed: $relative")
        continue
    }
    # Inspect index bytes actually destined for upload, including binary encodings.
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-C', $repositoryRoot, 'show', ":$relative")) { [void]$start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $buffer = [IO.MemoryStream]::new()
    $errorRead = $process.StandardError.ReadToEndAsync()
    $process.StandardOutput.BaseStream.CopyTo($buffer)
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Unable to read staged input: $relative" }
    [void]$errorRead.GetAwaiter().GetResult()
    $bytes = $buffer.ToArray()
    $buffer.Dispose()
    $process.Dispose()
    foreach ($encoding in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
        $content = $encoding.GetString($bytes)
        if ($content -match $tokenPattern) { $issues.Add("Credential/private-key signature: $relative") }
        if ($content -match $pathPattern) { $issues.Add("Machine path signature: $relative") }
        foreach ($privateRoot in $privateRoots) {
            foreach ($variant in @($privateRoot, $privateRoot.Replace('\', '/'))) {
                if ($content.IndexOf($variant, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $issues.Add("Private root leak: $relative") }
            }
        }
        if ($privateUser.Length -gt 2 -and [regex]::IsMatch($content, '(?i)(?<![A-Za-z0-9_])' + [regex]::Escape($privateUser) + '(?![A-Za-z0-9_])')) {
            $issues.Add("Development username leak: $relative")
        }
        # Software/security terminology is not itself a credential.
        if ($content -match $keywordPattern -and -not $keywordFiles.Contains($relative)) { $keywordFiles.Add($relative) }
        foreach ($email in [regex]::Matches($content, '(?i)[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}')) {
            if ($email.Value -notmatch '(?i)@users\.noreply\.github\.com$' -and $relative -notlike 'docs/licenses/*') {
                $issues.Add("Unreviewed email address: $relative")
            }
        }
    }
}
if ($RequireLicense) {
    if ('LICENSE' -notin $paths) { $issues.Add('Owner-approved LICENSE is not staged.') }
    $name = (& git -C $repositoryRoot config user.name).Trim()
    $email = (& git -C $repositoryRoot config user.email).Trim()
    if ($name -match '(?i)codex' -or [string]::IsNullOrWhiteSpace($name) -or $email -notmatch '(?i)@users\.noreply\.github\.com$') {
        $issues.Add('Configure the owner-approved public Git identity before release.')
    }
}
if ($issues.Count -gt 0) {
    $issues | Sort-Object -Unique | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    throw 'FAIL: public source gate. Resolve findings before commit/push.'
}
Write-Host "PASS: $($paths.Count) staged files; zero forbidden files, credential signatures, machine paths or unreviewed personal email addresses."
Write-Host "Security-term candidate files: $($keywordFiles.Count). Review semantic candidates separately; a signature scan cannot prove the absence of arbitrary secrets."
