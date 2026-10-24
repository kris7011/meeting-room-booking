<#
.SYNOPSIS
Starts the Room Booking API on http://localhost against an existing SQLite database.

.DESCRIPTION
Refuses to start when the database file is missing, unless -InitializeEmpty
is given for a deliberate new installation. The settings below apply only to
the API process: they are put back when it stops, so a later `dotnet run` in
the same PowerShell session does not silently keep using this database.
#>
[CmdletBinding()]
param(
    [string]$DatabaseFile,
    [string]$EntraApiClientId = $env:Entra__ApiClientId,
    [ValidateRange(1, 65535)][int]$Port = 5003,
    [switch]$InitializeEmpty
)

Set-StrictMode -Version Latest
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

# Development loads appsettings.Development.json (Entra and legacy API
# settings) and turns on Swagger; the API is still reachable from this PC only.
# Every other Entra setting, such as allowed tenants, comes from configuration.
$settings = [ordered]@{
    ConnectionStrings__BookingDatabase = "Data Source=""$DatabaseFile"""
    Entra__ApiClientId                 = $EntraApiClientId
    LegacyApi__AllowAnonymous          = 'false'
    ASPNETCORE_ENVIRONMENT             = 'Development'
}
$previous = @{}
foreach ($name in $settings.Keys) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process')
}

try {
    Write-Host "Room Booking local API on http://localhost:$Port"
    Write-Host "Database: $DatabaseFile"
    Write-Host 'Microsoft login remains online (Entra); the API and data are local.'
    dotnet run --project $apiProject --no-launch-profile -- --urls "http://localhost:$Port"
    if ($LASTEXITCODE -ne 0) { throw "API exited with code $LASTEXITCODE" }
}
finally {
    foreach ($name in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
}
