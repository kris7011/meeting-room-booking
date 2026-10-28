<#
.SYNOPSIS
Starts the API in its Development demo mode on a separate demo database, with
synthetic bookings, so the Angular client and the mobile web build can be
tried without a Microsoft Entra app registration.

.DESCRIPTION
- The database is always .demo/room-booking-demo.db in this repository
  (ignored by Git), never the development database. -Reset deletes it first.
- The original /api/rooms and /api/bookings endpoints answer anonymously
  (LegacyApi__AllowAnonymous=true, which the API accepts only in the
  Development and Testing environments). Organisation endpoints still require
  a Microsoft sign-in and are not part of this demo.
- When the demo database has no bookings, three synthetic bookings are created
  through the API itself (POST /api/bookings), so they pass the same
  validation and overlap rules as any other booking.
- The API runs until Ctrl+C. -StopAfterSeed stops it after seeding (used by CI).
- Settings apply to the API process only and are restored afterwards.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 65535)][int]$Port = 5003,
    [switch]$Reset,
    [switch]$StopAfterSeed
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$apiProject = Join-Path (Join-Path (Join-Path $root 'src') 'MeetingRoomBooking.Api') 'MeetingRoomBooking.Api.csproj'
$demoDirectory = Join-Path $root '.demo'
$databaseFile = Join-Path $demoDirectory 'room-booking-demo.db'
$baseUrl = "http://localhost:$Port"

if ($Reset) {
    foreach ($suffix in '', '-wal', '-shm') {
        $file = "$databaseFile$suffix"
        if (Test-Path -LiteralPath $file) {
            Remove-Item -LiteralPath $file
        }
    }
}

New-Item -ItemType Directory -Force -Path $demoDirectory | Out-Null

$settings = [ordered]@{
    ConnectionStrings__BookingDatabase = "Data Source=$databaseFile"
    Database__BackupDirectory          = Join-Path $demoDirectory 'backups'
    LegacyApi__AllowAnonymous          = 'true'
    ASPNETCORE_ENVIRONMENT             = 'Development'
    # Keep the console readable: no log line per SQL command.
    'Logging__LogLevel__Microsoft.EntityFrameworkCore' = 'Warning'
}

$previous = @{}
foreach ($name in $settings.Keys) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process')
}

# Windows PowerShell 5.1 returns a JSON array as one object; enumerate it to count.
function Get-ItemCount([string]$Path) {
    return (Invoke-RestMethod -Uri "$baseUrl$Path" | ForEach-Object { $_ } | Measure-Object).Count
}

function New-DemoBooking([int]$RoomId, [string]$Title, [string]$BookedBy, [int]$DaysAhead, [int]$Hour, [int]$Hours) {
    $start = [DateTime]::Today.AddDays($DaysAhead).AddHours($Hour).ToUniversalTime()
    $body = @{
        roomId   = $RoomId
        title    = $Title
        bookedBy = $BookedBy
        startUtc = $start.ToString('o')
        endUtc   = $start.AddHours($Hours).ToString('o')
    } | ConvertTo-Json

    Invoke-RestMethod -Method Post -Uri "$baseUrl/api/bookings" -ContentType 'application/json' -Body $body | Out-Null
    Write-Host "  + $Title"
}

function Test-Listening {
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/health" -TimeoutSec 2 | Out-Null
        return $true
    } catch [System.Net.WebException] {
        # A refused connection has no response; any HTTP answer means something is listening.
        return $null -ne $_.Exception.Response
    } catch {
        return $false
    }
}

function Stop-ProcessTree([System.Diagnostics.Process]$Process) {
    if ($PSVersionTable.PSEdition -eq 'Desktop' -or ((Test-Path variable:IsWindows) -and $IsWindows)) {
        & taskkill /PID $Process.Id /T /F 2>&1 | Out-Null
        return
    }
    foreach ($child in @(Get-Process | Where-Object { $_.Parent -and $_.Parent.Id -eq $Process.Id })) {
        Stop-ProcessTree $child
    }
    Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
}

if (Test-Listening) {
    throw "Something is already listening on $baseUrl. Stop it or pass -Port; the demo never seeds another API."
}

$api = $null
try {
    Write-Host "Demo database: $databaseFile"
    Write-Host 'Building and starting the API (the first build can take a minute)...'

    $api = Start-Process -FilePath 'dotnet' -PassThru -NoNewWindow -ArgumentList @(
        'run', '--project', "`"$apiProject`"", '--no-launch-profile', '--', '--urls', $baseUrl)

    $deadline = (Get-Date).AddMinutes(5)
    $ready = $false
    while (-not $ready -and (Get-Date) -lt $deadline) {
        if ($api.HasExited) {
            throw 'The API stopped during start-up; see the build or start-up errors above.'
        }
        try {
            $ready = (Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/health" -TimeoutSec 2).StatusCode -eq 200
        } catch {
            Start-Sleep -Seconds 2
        }
    }
    if (-not $ready) {
        throw "The API did not answer $baseUrl/health within 5 minutes."
    }

    if ((Get-ItemCount '/api/bookings') -eq 0) {
        Write-Host 'Adding synthetic demo bookings:'
        # Rooms 1-3 (Focus Room, Collaboration Room, Board Room) come from the initial migration.
        New-DemoBooking -RoomId 2 -Title 'Sprint planning' -BookedBy 'Alex Demo' -DaysAhead 1 -Hour 9 -Hours 1
        New-DemoBooking -RoomId 1 -Title 'Design review' -BookedBy 'Sam Example' -DaysAhead 1 -Hour 11 -Hours 1
        New-DemoBooking -RoomId 3 -Title 'Quarterly planning' -BookedBy 'Robin Sample' -DaysAhead 2 -Hour 13 -Hours 2
    }

    $rooms = Get-ItemCount '/api/rooms'
    $bookings = Get-ItemCount '/api/bookings'
    if ($rooms -eq 0 -or $bookings -eq 0) {
        throw "The demo database has $rooms rooms and $bookings bookings; expected the seeded rooms and bookings."
    }
    Write-Host "Demo API ready: $baseUrl ($rooms rooms, $bookings bookings). Swagger: $baseUrl/swagger"

    if ($StopAfterSeed) {
        return
    }

    Write-Host 'Angular client: npm --prefix src/meeting-room-booking-web start  ->  http://localhost:4200'
    Write-Host 'Press Ctrl+C to stop the API.'
    Wait-Process -Id $api.Id
} finally {
    if ($null -ne $api -and -not $api.HasExited) {
        # dotnet run starts the API as a child process; stop the whole tree.
        Stop-ProcessTree $api
    }
    foreach ($name in $settings.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
}
