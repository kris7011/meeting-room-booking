<#
.SYNOPSIS
Restores a verified SQLite backup into a NEW database file. Never overwrites an existing database.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BackupFile,
    [Parameter(Mandatory = $true)][string]$DestinationFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tool = Join-Path $root 'tools\RoomBooking.DatabaseTool\RoomBooking.DatabaseTool.csproj'
$BackupFile = [System.IO.Path]::GetFullPath($BackupFile)
$DestinationFile = [System.IO.Path]::GetFullPath($DestinationFile)

Write-Host 'Restoring to a NEW file. No existing database will be overwritten.'
# Release, so building the tool never touches the Debug output that a
# running Start-Local.ps1 API holds open.
dotnet run --project $tool --configuration Release -- restore --from $BackupFile --to $DestinationFile
if ($LASTEXITCODE -ne 0) { throw "Restore failed (exit code $LASTEXITCODE)" }
Write-Host "Start the API with: .\scripts\Start-Local.ps1 -DatabaseFile `"$DestinationFile`""
