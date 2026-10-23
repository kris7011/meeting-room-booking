param(
    [string]$DatabaseFile,
    [string]$BackupDirectory = (Join-Path $HOME 'RoomBookingBackups')
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tool = Join-Path $root 'tools\RoomBooking.DatabaseTool\RoomBooking.DatabaseTool.csproj'
if ([string]::IsNullOrWhiteSpace($DatabaseFile)) {
    $DatabaseFile = Join-Path $root 'src\MeetingRoomBooking.Api\meeting-room-booking.db'
}
$DatabaseFile = [System.IO.Path]::GetFullPath($DatabaseFile)
$BackupDirectory = [System.IO.Path]::GetFullPath($BackupDirectory)
Write-Host 'A verified SQLite snapshot will be created. Prefer stopping the API first for a quiet database.'
dotnet run --project $tool -- backup --database $DatabaseFile --directory $BackupDirectory
if ($LASTEXITCODE -ne 0) { throw "Backup failed (exit code $LASTEXITCODE)" }
Write-Host 'Keep an additional copy outside your PC or hosting account. Backups contain private user and booking data.'
