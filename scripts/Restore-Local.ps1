param(
    [Parameter(Mandatory = $true)][string]$BackupFile,
    [Parameter(Mandatory = $true)][string]$DestinationFile
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tool = Join-Path $root 'tools\RoomBooking.DatabaseTool\RoomBooking.DatabaseTool.csproj'
$BackupFile = [System.IO.Path]::GetFullPath($BackupFile)
$DestinationFile = [System.IO.Path]::GetFullPath($DestinationFile)
Write-Host 'Restoring to a NEW file. No existing database will be overwritten.'
dotnet run --project $tool -- restore --from $BackupFile --to $DestinationFile
if ($LASTEXITCODE -ne 0) { throw "Restore failed (exit code $LASTEXITCODE)" }
Write-Host "Start the API with: .\scripts\Start-Local.ps1 -DatabaseFile `"$DestinationFile`""
