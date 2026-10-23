param(
    [string]$DatabaseFile,
    [string]$EntraApiClientId = $env:Entra__ApiClientId,
    [ValidateRange(1, 65535)][int]$Port = 5003,
    [switch]$InitializeEmpty
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$apiProject = Join-Path $root 'src\MeetingRoomBooking.Api\MeetingRoomBooking.Api.csproj'
if ([string]::IsNullOrWhiteSpace($DatabaseFile)) {
    $DatabaseFile = Join-Path $root 'src\MeetingRoomBooking.Api\meeting-room-booking.db'
}
$DatabaseFile = [System.IO.Path]::GetFullPath($DatabaseFile)

if (-not (Test-Path -LiteralPath $DatabaseFile -PathType Leaf)) {
    if (-not $InitializeEmpty) {
        throw "Database not found: $DatabaseFile. Restore a backup first, or explicitly pass -InitializeEmpty for a new installation."
    }
    New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($DatabaseFile)) | Out-Null
}

if ([string]::IsNullOrWhiteSpace($EntraApiClientId)) {
    throw 'Set Entra__ApiClientId or pass -EntraApiClientId. Signed-in Room Booking requires the API app registration.'
}

$env:ConnectionStrings__BookingDatabase = "Data Source=$DatabaseFile"
$env:Entra__ApiClientId = $EntraApiClientId
$env:Entra__AllowPersonalAccounts = 'true'
$env:LegacyApi__AllowAnonymous = 'false'
$env:ASPNETCORE_ENVIRONMENT = 'Development'

Write-Host "Room Booking local API on http://localhost:$Port"
Write-Host "Database: $DatabaseFile"
Write-Host 'Microsoft login remains online (Entra); the API and data are local.'
dotnet run --project $apiProject --no-launch-profile -- --urls "http://localhost:$Port"
if ($LASTEXITCODE -ne 0) { throw "API exited with code $LASTEXITCODE" }
