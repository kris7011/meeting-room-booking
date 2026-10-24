<#
.SYNOPSIS
Writes a verified SQLite snapshot of the Room Booking database to a backup folder.
#>
[CmdletBinding()]
param(
    [string]$DatabaseFile,
    [string]$BackupDirectory = (Join-Path $HOME 'RoomBookingBackups')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tool = Join-Path $root 'tools\RoomBooking.DatabaseTool\RoomBooking.DatabaseTool.csproj'
if ([string]::IsNullOrWhiteSpace($DatabaseFile)) {
    $DatabaseFile = Join-Path $root 'src\MeetingRoomBooking.Api\meeting-room-booking.db'
}
$DatabaseFile = [System.IO.Path]::GetFullPath($DatabaseFile)
$BackupDirectory = [System.IO.Path]::GetFullPath($BackupDirectory)

Write-Host 'Creating a verified SQLite snapshot. It is consistent even while the API runs, but excludes later changes.'
# Release, so building the tool never touches the Debug output that a
# running Start-Local.ps1 API holds open.
dotnet run --project $tool --configuration Release -- backup --database $DatabaseFile --directory $BackupDirectory
if ($LASTEXITCODE -ne 0) { throw "Backup failed (exit code $LASTEXITCODE)" }
Write-Host 'Keep an additional copy outside your PC or hosting account. Backups contain private user and booking data.'
