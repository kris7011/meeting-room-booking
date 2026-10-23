[CmdletBinding()]
param(
    [string]$TargetFile = (Join-Path (Join-Path $HOME '.claude') 'CLAUDE.md'),
    [switch]$Preview
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$startMarker = '<!-- ROOM_BOOKING_SHARED_ENGINEERING_STANDARDS_START -->'
$endMarker = '<!-- ROOM_BOOKING_SHARED_ENGINEERING_STANDARDS_END -->'
$managedSection = @'
<!-- ROOM_BOOKING_SHARED_ENGINEERING_STANDARDS_START -->
## Shared engineering standards (all projects)

Apply these preferences consistently across coding projects, unless a repository
contains more specific and justified instructions.

- Use English for source code, identifiers, comments, automated tests,
  technical documentation, branch names, commit messages, pull requests,
  and code reviews. Preserve intentionally localized user-facing copy.
- Follow current language, framework, platform, and library best practices
  for the versions actually installed in the project. Verify unfamiliar
  or version-sensitive APIs against official documentation.
- Write clean, maintainable, secure, idiomatic code. Use clear naming,
  cohesive units, explicit contracts, strict typing where supported,
  and pragmatic SOLID, DRY, KISS, and separation of concerns.
- Choose an architecture and directory structure appropriate to the
  project's actual scope and constraints. Prefer simple, modular designs;
  avoid needless abstractions, extra dependencies, overengineering,
  speculative features, and unapproved large rewrites.
- Match existing conventions unless improving them is intentional and
  reviewable. Explain important trade-offs and migrations.
- Enforce authentication, authorization, validation, privacy, and tenant
  isolation on the server. Never commit credentials or secrets.
- Preserve real data. Make verified backups before risky migrations,
  restores, or cleanup. Never reset, overwrite, or silently delete a
  database, repository changes, or user work.
- Add or update meaningful tests for behavior changes, including errors,
  boundaries, and security-sensitive flows. Run the project's configured
  checks and report actual outcomes before declaring work complete.
- Prefer focused commits and PRs with useful English rationale, tests,
  and any rollback or migration notes. Do not merge or deploy without
  user authorization.
- Where feasible, avoid mandatory hosting-provider lock-in and document
  how to develop, back up, restore, and operate software locally.

Before implementing a task, read the current repository's AGENTS.md and
CLAUDE.md (if present) for project-specific standards. Do not assume that
an instruction implies a particular framework or navigation system.
<!-- ROOM_BOOKING_SHARED_ENGINEERING_STANDARDS_END -->
'@

$fullPath = [System.IO.Path]::GetFullPath($TargetFile)
if ([System.IO.Directory]::Exists($fullPath)) {
    throw "The target is a directory, not a file: $fullPath"
}

$original = if ([System.IO.File]::Exists($fullPath)) {
    [System.IO.File]::ReadAllText($fullPath)
} else {
    ''
}

$hasStart = $original.Contains($startMarker, [System.StringComparison]::Ordinal)
$hasEnd = $original.Contains($endMarker, [System.StringComparison]::Ordinal)

if ($hasStart -ne $hasEnd) {
    throw 'Existing Claude instructions have only one standards marker. Fix this manually before updating.'
}

if ($hasStart) {
    $start = $original.IndexOf($startMarker, [System.StringComparison]::Ordinal)
    $end = $original.IndexOf($endMarker, $start, [System.StringComparison]::Ordinal)
    if ($end -lt 0 -or
        $original.IndexOf($startMarker, $start + $startMarker.Length, [System.StringComparison]::Ordinal) -ge 0 -or
        $original.IndexOf($endMarker, $end + $endMarker.Length, [System.StringComparison]::Ordinal) -ge 0) {
        throw 'Duplicate or malformed standards markers. Fix this manually before updating.'
    }

    $updated = $original.Substring(0, $start) +
        $managedSection +
        $original.Substring($end + $endMarker.Length)
} else {
    $separator = if ($original.Length -eq 0) { '' }
                 elseif ($original.EndsWith("`n")) { "`n" }
                 else { "`n`n" }
    $updated = $original + $separator + $managedSection + "`n"
}

if ($updated -ceq $original) {
    Write-Host "Claude user instructions already up to date: $fullPath"
    return
}

if ($Preview) {
    Write-Host "Preview: would update the managed engineering standards in $fullPath"
    Write-Host "Existing content outside the managed section would be preserved."
    return
}

$directory = [System.IO.Path]::GetDirectoryName($fullPath)
[System.IO.Directory]::CreateDirectory($directory) | Out-Null
$tempFile = Join-Path $directory ('.claude-standards-' + [guid]::NewGuid().ToString('N') + '.tmp')
$backupFile = $null

try {
    [System.IO.File]::WriteAllText($tempFile, $updated, [System.Text.UTF8Encoding]::new($false))

    if ([System.IO.File]::Exists($fullPath)) {
        $backupFile = $fullPath + '.backup.' +
            [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
        [System.IO.File]::Replace($tempFile, $fullPath, $backupFile)
    } else {
        [System.IO.File]::Move($tempFile, $fullPath)
    }
} finally {
    if ([System.IO.File]::Exists($tempFile)) {
        [System.IO.File]::Delete($tempFile)
    }
}

Write-Host "Updated Claude's user-level instructions: $fullPath"
if ($backupFile) {
    Write-Host "Previous instructions backed up to: $backupFile"
}
Write-Host 'Start a new Claude Code session to load the updated global instructions.'
