# Fase 4A: Kør Room Booking lokalt, og flyt SQLite-data sikkert

Room Booking kan køre uden en betalt hostingtjeneste. Backend er stadig den
samme .NET 10 API, og data opbevares i en SQLite-fil. Microsoft-login kræver
dog fortsat adgang til Microsoft og en gyldig Entra app-registrering.

## Forudsætninger

- Windows PowerShell og .NET 10 SDK.
- En database, du vil bruge lokalt (den nuværende database eller en gendannet backup).
- API'ets Entra Application (client) ID. Det er en offentlig identifikator, ikke et secret.
- Telefonen kan i udvikling kobles til med USB og `adb reverse tcp:5003 tcp:5003`.

BEMÆRK: Release-APK'en understøtter endnu ikke valg af API-adresse inde i appen.
`EXPO_PUBLIC_API_URL` bliver indbygget i JS-bundlet. Den nuværende Samsung
development build kan køre mod den lokale API ved at vælge det lokale miljø i
`.env.local` og genstarte Metro. Runtime-servervalg bliver en separat
mobilændring; den er **ikke** dækket af dette backend-PR.

## Start eksisterende database lokalt

Åbn PowerShell fra repository-roden:

```powershell
cd C:\Users\krisr\source\repos\meeting-room-booking-entra
$env:Entra__ApiClientId = "<API application client ID>"
.\scripts\Start-Local.ps1
```

Standarddatabasen er `src/MeetingRoomBooking.Api/meeting-room-booking.db`.
Hvis den ikke findes, stopper scriptet uden at oprette en tom database ved en fejl.

For at starte med en helt ny tom database, vælg en NY fil og giv eksplicit
`-InitializeEmpty`:

```powershell
.\scripts\Start-Local.ps1 -DatabaseFile "C:\RoomBooking\data\new.db" -InitializeEmpty
```

For en gendannet database:

```powershell
.\scripts\Start-Local.ps1 -DatabaseFile "C:\RoomBooking\data\restored.db"
```

Scriptet binder kun API'et til `http://localhost:5003`, kræver Microsoft-login
for org-data (`LegacyApi__AllowAnonymous=false`) og skriver den VALGTE fulde
database-sti. Det eksponerer ikke API'et på dit Wi-Fi eller internettet.

Kontrollér `http://localhost:5003/health`; den returnerer `Healthy`.
En eksisterende SQLite-fil migreres ved API-start efter verificeret backup.

## Lav en manuelt verificeret backup

Stop helst API'et, så databasen er i ro. Kør derefter:

```powershell
.\scripts\Backup-Local.ps1
```

Som standard læses samme database som i Start-Local, og backup lægges i
`$HOME\RoomBookingBackups`, uden for repository. Brug eksplicit sti, hvis du
startede API'et med en anden database:

```powershell
.\scripts\Backup-Local.ps1 -DatabaseFile "C:\RoomBooking\data\restored.db" -BackupDirectory "D:\Backups\RoomBooking"
```

Værktøjet bruger SQLite `VACUUM INTO`, kontrollerer databaseintegritet og
sammenligner rækkeantal pr. tabel med kilden. Hvis noget fejler, meldes en
fejl i stedet for en succesfuld backup. Backups hedder
`<databasenavn>.pre-manual-<UTC-tid>.db`.

Kopiér desuden den verificerede backup til et andet fysisk drev eller et
sikkert backupsted, før du opsiger hosting. En backup i samme hostingkonto er
ikke en sikker exit-plan. Databasekopier indeholder bl.a. brugeridentiteter,
invitationer og bookingoplysninger og skal behandles fortroligt.

## Gendan i en NY databasefil (ingen overskrivning)

Dette virker kun med et SQLite-backupformat. Hosting på fx PostgreSQL vil
kræve en særskilt dokumenteret eksport/import, før denne metode kan anvendes.

```powershell
.\scripts\Restore-Local.ps1 `
  -BackupFile "D:\Backups\RoomBooking\meeting-room-booking.pre-manual-20261023T120000000Z.db" `
  -DestinationFile "C:\RoomBooking\data\from-cloud.db"
```

Værktøjet:

1. Kontrollerer `PRAGMA integrity_check` og `PRAGMA foreign_key_check`.
2. Opretter en verificeret SQLite-kopi i en midlertidig fil.
3. Sammenligner rækkeantal for alle tabeller.
4. Flytter kun kopien på plads, hvis destinationen **ikke findes**.

Den eksisterende database overskrives aldrig. Vælg altid et nyt filnavn.
Kør derefter `Start-Local.ps1 -DatabaseFile "...from-cloud.db"`.

Sørg for, at alle cloud-skrivninger er afsluttet før den **sidste** backup,
og verificér lokalt at organisationsnavne, roller, mødelokaler og bookinger
er der. Opsig ikke hostingen, før gendannelsestesten er bestået.

## Lokal Android-udvikling

I `room-booking-mobile` skal `.env.local` have
`EXPO_PUBLIC_API_URL=http://localhost:5003`. Med Samsung på USB:

```powershell
$adb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"
& $adb reverse tcp:5003 tcp:5003
& $adb reverse tcp:8081 tcp:8081
npx expo start --dev-client --clear --port 8081
```

Det lokale HTTP-kald med bearer-token er kun tilladt i development build via
localhost/ADB. Til en release-app eller fjernadgang kræves HTTPS og en anden
konfiguration. Du kan ikke blot vælge en IP-adresse over ubeskyttet HTTP.

## Vigtig afgrænsning

- SQLite-lagring på hosting kræver en vedvarende disk/volume og en passende
  opsætning (typisk én skrivende API-instans), ellers kan data forsvinde ved deploy.
- Dette er manuel eksport og gendannelse, **ikke** automatisk synkronisering
  mellem cloud og din PC. Der må ikke skrives til begge kopier som om de deler data.
- Microsoft Entra er en ekstern loginudbyder. Helt internetfri adgang er
  et selvstændigt projekt.
- Lokal Windows/PowerShell-test med dine rigtige data er nødvendig; CI bruger
  kun isolerede testdatabaser.
