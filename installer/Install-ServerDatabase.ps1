# Richtet die Server-Datenbank (PostgreSQL) fuer LiftUp ein.
#
# Das Skript
#   1. ermittelt die aktuelle PostgreSQL-Version (postgresql.org/versions.json),
#   2. installiert PostgreSQL still, falls es fehlt (winget, sonst EDB-Installer),
#   3. legt Datenbank, Anmelderolle und das LiftUp-Schema idempotent an,
#   4. schreibt eine Verbindungsdatei (liftup-server.json) mit allen Angaben,
#      die LiftUp zum Verbinden und Abgleichen braucht.
#
# Aufruf (als Administrator, einmal pro Datenbank-Server):
#   .\Install-ServerDatabase.ps1 -SuperuserPassword "Geheim123" -HostName "db-server.firma.local"
#
# Nur pruefen, nichts aendern:
#   .\Install-ServerDatabase.ps1 -WhatIf
#
# Weitere Clients im Netz zulassen (pg_hba.conf wird ergaenzt, neu geladen):
#   .\Install-ServerDatabase.ps1 -SuperuserPassword "..." -ClientCidr "10.0.0.0/8"
#
# Hinweis: Aus gutem Grund getrennt vom Client-Setup. Eine PostgreSQL-Installation
# braucht Administratorrechte und laeuft als Dienst; auf jedem Arbeitsplatz waere sie
# falsch und wuerde die stillen Benutzer-Updates aushebeln.
#
# Die Datei ist bewusst ASCII-only gehalten: Windows PowerShell 5.1 liest .ps1 ohne
# BOM als ANSI und wuerde Umlaute in Meldungen und in der Verbindungsdatei zerstoeren.

[CmdletBinding()]
param(
    [string]$SuperuserPassword,
    [string]$DatabaseName = "liftup",
    [string]$UserName = "liftup",
    [string]$UserPassword,
    [string]$SchemaName = "liftup",
    [int]$Port = 5432,
    [string]$PostgresVersion,
    [string]$HostName,
    [ValidateSet("Disable", "Allow", "Prefer", "Require", "VerifyCA", "VerifyFull")]
    [string]$SslMode = "Prefer",
    [string[]]$ClientCidr = @(),
    [string]$OutputPath = (Join-Path $PSScriptRoot "liftup-server.json"),
    [string]$PsqlPath,
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"

$isWindowsHost = $true
if (Test-Path variable:IsWindows) { $isWindowsHost = [bool]$IsWindows }

function Write-Step {
    param([string]$Text)
    Write-Host ""
    Write-Host "== $Text" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Text)
    Write-Host "   $Text" -ForegroundColor Green
}

function Write-Warn {
    param([string]$Text)
    Write-Host "   $Text" -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# Verbindung und Versionen
# ---------------------------------------------------------------------------

function Test-PostgresPort {
    param([int]$TargetPort)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect("127.0.0.1", $TargetPort)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

function Wait-PostgresPort {
    param([int]$Seconds = 180)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-PostgresPort -TargetPort $Port) { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Get-PsqlPath {
    param([string]$Explicit)
    if ($Explicit) {
        if (-not (Test-Path $Explicit)) { throw "psql wurde unter '$Explicit' nicht gefunden." }
        return (Resolve-Path $Explicit).Path
    }

    if ($env:ProgramFiles) {
        $base = Join-Path $env:ProgramFiles "PostgreSQL"
        if (Test-Path $base) {
            $roots = @(Get-ChildItem $base -Directory |
                Sort-Object { try { [double]($_.Name -replace '[^0-9.].*$', '') } catch { 0 } } -Descending)
            foreach ($root in $roots) {
                $candidate = Join-Path $root.FullName "bin\psql.exe"
                if (Test-Path $candidate) { return $candidate }
            }
        }
    }

    $command = Get-Command psql -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    return $null
}

function Test-PostgresInstalled {
    if (Get-PsqlPath -Explicit $PsqlPath) { return $true }
    if (-not $isWindowsHost) { return $false }

    $service = Get-Service -Name "postgresql*" -ErrorAction SilentlyContinue
    if ($service) { return $true }
    return (Test-Path "HKLM:\SOFTWARE\PostgreSQL\Installations")
}

# Aktuelle, noch unterstuetzte Version von postgresql.org. Der Eintrag mit der
# hoechsten Hauptnummer ist die aktuelle Version; latestMinor ist die neueste
# Nebenversion dieses Zweigs.
function Get-PostgresRelease {
    param([string]$Requested)

    if ($PSVersionTable.PSVersion.Major -lt 6) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    }

    try {
        $releases = Invoke-RestMethod -Uri "https://www.postgresql.org/versions.json" -UseBasicParsing
    }
    catch {
        throw "Die Versionsliste von postgresql.org konnte nicht geladen werden: $($_.Exception.Message)"
    }

    $supported = @($releases | Where-Object { $_.supported -eq $true })
    if ($supported.Count -eq 0) {
        throw "Die Versionsliste von postgresql.org enthielt keine unterstuetzte Version."
    }

    if ($Requested) {
        $match = @($supported | Where-Object { "$($_.major)" -eq $Requested })
        if ($match.Count -eq 0) {
            $available = (@($supported | ForEach-Object { "$($_.major)" }) -join ", ")
            throw "PostgreSQL $Requested wird nicht (mehr) unterstuetzt. Verfuegbar: $available."
        }
        return $match[0]
    }

    return ($supported | Sort-Object { [int]"$($_.major)" } -Descending | Select-Object -First 1)
}

function Install-Postgres {
    param($Release)

    $major = "$($Release.major)"
    $minor = "$($Release.latestMinor)"
    $display = "$major.$minor"

    if (-not $isWindowsHost) {
        throw "PostgreSQL ist nicht installiert. Die automatische Installation nutzt winget und ist nur unter Windows verfuegbar. Bitte PostgreSQL $display manuell installieren und das Skript erneut starten."
    }

    $elevated = $false
    try {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $elevated = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    catch {
        $elevated = $false
    }

    if (-not $elevated) {
        throw "Die Installation von PostgreSQL braucht Administratorrechte. Bitte PowerShell als Administrator starten."
    }

    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        $packageId = "PostgreSQL.PostgreSQL.$major"
        Write-Step "Installiere PostgreSQL $display ueber winget ($packageId)"
        $override = "--mode unattended --unattendedmodeui none --serverport $Port --superpassword `"$SuperuserPassword`""
        & $winget.Source install --id $packageId --exact --silent --accept-package-agreements --accept-source-agreements --override $override | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "winget konnte PostgreSQL nicht installieren (Exitcode $LASTEXITCODE). Bitte von Hand installieren: https://www.postgresql.org/download/windows/"
        }
        return
    }

    # Ohne winget: Original-Installer von EnterpriseDB, stille Installation.
    $url = "https://get.enterprisedb.com/postgresql/postgresql-$major.$minor-1-windows-x64.exe"
    $target = Join-Path $env:TEMP "postgresql-$major.$minor-1-windows-x64.exe"
    Write-Step "winget ist nicht vorhanden - lade den PostgreSQL-Installer von EnterpriseDB"
    Write-Host "   $url"
    Invoke-WebRequest -Uri $url -OutFile $target -UseBasicParsing
    & $target --mode unattended --unattendedmodeui none --serverport $Port --superpassword $SuperuserPassword |
        Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Der PostgreSQL-Installer ist fehlgeschlagen (Exitcode $LASTEXITCODE)."
    }
}

# ---------------------------------------------------------------------------
# Hilfsmittel
# ---------------------------------------------------------------------------

function ConvertTo-PlainText {
    param([Security.SecureString]$Secure)
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

function New-RandomPassword {
    param([int]$Length = 24)
    # Ohne leicht zu verwechselnde Zeichen (0/O, 1/l/I); mindestens ein Sonderzeichen
    # ist nicht noetig, weil das Passwort nur maschinell verwendet wird.
    $alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789-_"
    $bytes = New-Object byte[] ($Length)
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $random.GetBytes($bytes)
    }
    finally {
        $random.Dispose()
    }

    $builder = New-Object System.Text.StringBuilder
    for ($index = 0; $index -lt $Length; $index++) {
        [void]$builder.Append($alphabet[$bytes[$index] % $alphabet.Length])
    }
    return $builder.ToString()
}

$script:ResolvedPsql = $null

function Invoke-Psql {
    param(
        [Parameter(Mandatory = $true)][string]$Sql,
        [string[]]$Variables = @(),
        [string]$Database = "postgres"
    )

    if (-not $script:ResolvedPsql) {
        $script:ResolvedPsql = Get-PsqlPath -Explicit $PsqlPath
    }
    if (-not $script:ResolvedPsql) {
        throw "psql wurde nicht gefunden. Bitte PostgreSQL installieren oder -PsqlPath angeben."
    }

    $psqlArguments = @("-h", "127.0.0.1", "-p", "$Port", "-U", "postgres", "-d", $Database,
        "-X", "-q", "-t", "-A", "-v", "ON_ERROR_STOP=1")
    foreach ($variable in $Variables) {
        $psqlArguments += @("-v", $variable)
    }

    # Über die Standardeingabe, nicht mit -c: nur so ersetzt psql die Variablen
    # :'name' (Text) und :"name" (Bezeichner).
    $output = $Sql | & $script:ResolvedPsql @psqlArguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "SQL ist fehlgeschlagen (Exitcode $LASTEXITCODE): $($output -join ' ')"
    }

    return @($output)
}

# Ergänzt pg_hba.conf um Zugriffe aus dem angegebenen Netz und laedt die
# Konfiguration neu. Ohne -ClientCidr bleibt die Datei unveraendert.
function Add-ClientAccess {
    param([string[]]$Cidr)

    if (-not $Cidr -or $Cidr.Count -eq 0) { return @() }

    $hbaFile = ((Invoke-Psql -Sql "SHOW hba_file;") -join "").Trim()
    if (-not $hbaFile -or -not (Test-Path $hbaFile)) {
        Write-Warn "pg_hba.conf wurde nicht gefunden - bitte Zugriffe von Hand freigeben."
        return @()
    }

    $content = Get-Content -Path $hbaFile -Raw
    $added = @()

    foreach ($network in $Cidr) {
        $line = "host    $DatabaseName    $UserName    $network    scram-sha-256"
        if ($content -match [regex]::Escape($line)) {
            Write-Ok "Zugriff aus $network ist bereits freigegeben."
            continue
        }

        Add-Content -Path $hbaFile -Value "$line    # LiftUp" -Encoding ASCII
        $added += $network
        Write-Ok "Zugriff aus $network freigegeben."
    }

    if ($added.Count -gt 0) {
        Invoke-Psql -Sql "SELECT pg_reload_conf();" | Out-Null
        Write-Ok "Konfiguration neu geladen."
    }

    return $added
}

# ---------------------------------------------------------------------------
# Ablauf
# ---------------------------------------------------------------------------

Write-Host "LiftUp - Server-Datenbank einrichten" -ForegroundColor Cyan

foreach ($entry in @(
        @("DatabaseName", $DatabaseName),
        @("UserName", $UserName),
        @("SchemaName", $SchemaName))) {
    if ($entry[1] -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
        throw "Der Wert fuer $($entry[0]) ist kein gueltiger Name: '$($entry[1])'. Erlaubt sind Buchstaben, Ziffern und der Unterstrich, beginnend mit Buchstabe oder Unterstrich."
    }
}

if ($Port -lt 1 -or $Port -gt 65535) {
    throw "Ungueltiger Port: $Port."
}

$installed = Test-PostgresInstalled
$listening = Test-PostgresPort -TargetPort $Port
$release = $null

if ($installed) {
    $installedVersion = ((& (Get-PsqlPath -Explicit $PsqlPath) --version 2>$null) -join " ").Trim()
    Write-Ok "PostgreSQL ist vorhanden ($installedVersion) - Installation wird uebersprungen."
}
else {
    $release = Get-PostgresRelease -Requested $PostgresVersion
    Write-Warn "PostgreSQL wurde nicht gefunden. Vorgesehen ist Version $($release.major).$($release.latestMinor) (aktuelle Version laut postgresql.org)."

    if ($WhatIf) {
        Write-Host "   [WhatIf] PostgreSQL $($release.major).$($release.latestMinor) wuerde installiert (winget PostgreSQL.PostgreSQL.$($release.major))." -ForegroundColor Yellow
        return
    }

    if ([string]::IsNullOrWhiteSpace($SuperuserPassword)) {
        $SecureSuperuser = Read-Host -AsSecureString "Passwort fuer den PostgreSQL-Benutzer 'postgres' (wird fuer die Installation gesetzt)"
        $SuperuserPassword = ConvertTo-PlainText -Secure $SecureSuperuser
    }
    if ([string]::IsNullOrWhiteSpace($SuperuserPassword)) {
        throw "Fuer eine Neuinstallation wird ein Passwort fuer 'postgres' benoetigt (-SuperuserPassword)."
    }

    Install-Postgres -Release $release
    Start-Sleep -Seconds 3
    $listening = Wait-PostgresPort -Seconds 180
    if (-not $listening) {
        Write-Warn "PostgreSQL wurde installiert, antwortet aber nicht auf Port $Port."
        exit 2
    }

    Write-Ok "PostgreSQL installiert und gestartet."
    $installed = $true
}

if (-not $listening) {
    Write-Warn "Auf 127.0.0.1:$Port antwortet kein PostgreSQL. Datenbank wird nicht angelegt."
    Write-Warn "Laeuft der Dienst? Startet er auf einem anderen Port (-Port)?"
    exit 2
}

if (-not $installed) {
    Write-Warn "PostgreSQL-Dienst nicht erkannt, aber Port $Port antwortet - das Skript faehrt fort."
}

$script:ResolvedPsql = Get-PsqlPath -Explicit $PsqlPath
if (-not $script:ResolvedPsql) {
    throw "psql wurde nicht gefunden. Bitte PostgreSQL installieren oder -PsqlPath auf psql.exe setzen."
}

if ([string]::IsNullOrWhiteSpace($SuperuserPassword)) {
    if ($WhatIf) {
        Write-Host "   [WhatIf] Ohne -SuperuserPassword wird interaktiv nach dem Passwort gefragt." -ForegroundColor Yellow
    }
    else {
        $SecureSuperuser = Read-Host -AsSecureString "Passwort fuer den PostgreSQL-Benutzer 'postgres'"
        $SuperuserPassword = ConvertTo-PlainText -Secure $SecureSuperuser
    }
}

$env:PGPASSWORD = $SuperuserPassword

try {
    $serverVersion = ((Invoke-Psql -Sql "SHOW server_version;") -join "").Trim()
    Write-Host ""
    Write-Host "   Server: PostgreSQL $serverVersion auf 127.0.0.1:$Port" -ForegroundColor Gray
}
catch {
    throw "Die Anmeldung als 'postgres' ist fehlgeschlagen. Bitte -SuperuserPassword pruefen. Meldung: $($_.Exception.Message)"
}

$roleExists = ((Invoke-Psql -Sql 'SELECT 1 FROM pg_roles WHERE rolname = :''dbuser'';' `
            -Variables @("dbuser=$UserName")) -join "").Trim() -eq "1"

$dbExists = ((Invoke-Psql -Sql 'SELECT 1 FROM pg_database WHERE datname = :''dbname'';' `
            -Variables @("dbname=$DatabaseName")) -join "").Trim() -eq "1"

$schemaFile = Join-Path $PSScriptRoot "sql/liftup-schema.sql"
if (-not (Test-Path $schemaFile)) {
    throw "Das Schema '$schemaFile' fehlt. Bitte das vollstaendige installer-Verzeichnis verwenden."
}

if ($WhatIf) {
    Write-Host ""
    Write-Host "   [WhatIf] Es wuerde jetzt gelten:" -ForegroundColor Yellow
    if ($roleExists) {
        Write-Host "      Rolle '$UserName' besteht bereits - Passwort wuerde neu gesetzt."
    }
    else {
        Write-Host "      Rolle '$UserName' wuerde angelegt."
    }
    if ($dbExists) {
        Write-Host "      Datenbank '$DatabaseName' besteht bereits - nur das Schema wuerde ergaenzt."
    }
    else {
        Write-Host "      Datenbank '$DatabaseName' wuerde mit Eigentuemer '$UserName' angelegt."
    }
    Write-Host "      Schema '$SchemaName' wuerde aus $schemaFile eingespielt."
    Write-Host "      Verbindungsdatei wuerde nach '$OutputPath' geschrieben."
    if ([string]::IsNullOrWhiteSpace($UserPassword)) {
        Write-Host "      Ohne -UserPassword wuerde ein zufaelliges Passwort gesetzt."
    }
    if ($ClientCidr -and $ClientCidr.Count -gt 0) {
        Write-Host "      pg_hba.conf wuerde fuer folgende Netze ergaenzt: $($ClientCidr -join ', ')."
    }
    return
}

Write-Step "Rolle und Datenbank"

# Ohne Passwort fuer die Anwendung wird eines erzeugt. Es steht danach in der
# Verbindungsdatei; ein vorhandenes Passwort der Rolle wird dabei ersetzt.
if ([string]::IsNullOrWhiteSpace($UserPassword)) {
    $UserPassword = New-RandomPassword
    Write-Ok "Kein -UserPassword angegeben - ein zufaelliges Passwort wird gesetzt."
}

if ($roleExists) {
    # Passwort immer setzen: die Verbindungsdatei muss zum Server passen.
    Invoke-Psql -Sql 'ALTER ROLE :"dbuser" WITH LOGIN PASSWORD :''dbpwd'';' `
        -Variables @("dbuser=$UserName", "dbpwd=$UserPassword") | Out-Null
    Write-Ok "Rolle '$UserName' bestand bereits - Passwort und Anmeldung sind gesetzt."
}
else {
    Invoke-Psql -Sql 'CREATE ROLE :"dbuser" WITH LOGIN PASSWORD :''dbpwd'';' `
        -Variables @("dbuser=$UserName", "dbpwd=$UserPassword") | Out-Null
    Write-Ok "Rolle '$UserName' angelegt."
}

if ($dbExists) {
    Write-Ok "Datenbank '$DatabaseName' bestand bereits."
}
else {
    # CREATE DATABASE laeuft nicht in einer Transaktion; ein eigener Aufruf ist noetig.
    Invoke-Psql -Sql 'CREATE DATABASE :"dbname" WITH OWNER :"dbuser" ENCODING ''UTF8'' TEMPLATE template0;' `
        -Variables @("dbname=$DatabaseName", "dbuser=$UserName") | Out-Null
    Write-Ok "Datenbank '$DatabaseName' angelegt."
}

Write-Step "Schema"

$psqlArguments = @("-h", "127.0.0.1", "-p", "$Port", "-U", "postgres", "-d", $DatabaseName,
    "-X", "-q", "-v", "ON_ERROR_STOP=1", "-v", "schema=$SchemaName", "-v", "owner=$UserName",
    "-f", $schemaFile)
& $script:ResolvedPsql @psqlArguments
if ($LASTEXITCODE -ne 0) {
    throw "Das Schema konnte nicht eingespielt werden (Exitcode $LASTEXITCODE)."
}
Write-Ok "Schema '$SchemaName' ist eingespielt (Tabellen, Indizes, Schemastand)."

# Absicherung: selbst wenn Objekte von einem anderen Konto angelegt wurden,
# darf die Anwendung lesen und schreiben.
Invoke-Psql -Database $DatabaseName -Sql ('GRANT USAGE ON SCHEMA "{0}" TO "{1}";
        GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA "{0}" TO "{1}";
        GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA "{0}" TO "{1}";' -f $SchemaName, $UserName) | Out-Null

$grantedNetworks = Add-ClientAccess -Cidr $ClientCidr

Write-Step "Stand ablesen"

$schemaVersionText = ((Invoke-Psql -Database $DatabaseName `
            -Sql ('SELECT "Version" FROM "{0}"."SchemaInfo" LIMIT 1;' -f $SchemaName)) -join "").Trim()
$schemaVersion = 0
if (-not [int]::TryParse($schemaVersionText, [ref]$schemaVersion)) {
    throw "Der Schemastand konnte nicht gelesen werden (Antwort: '$schemaVersionText')."
}

$tables = @(Invoke-Psql -Database $DatabaseName `
        -Sql ('SELECT tablename FROM pg_tables WHERE schemaname = ''{0}'' ORDER BY tablename;' -f $SchemaName) |
        Where-Object { $_ -and $_.Trim() })
$listenAddresses = ((Invoke-Psql -Sql "SHOW listen_addresses;") -join "").Trim()

Write-Ok "$($tables.Count) Tabellen, Schemastand $schemaVersion."

# ---------------------------------------------------------------------------
# Verbindungsdatei
# ---------------------------------------------------------------------------

Write-Step "Verbindungsdatei schreiben"

$resolvedHost = $HostName
if ([string]::IsNullOrWhiteSpace($resolvedHost)) {
    $resolvedHost = [System.Net.Dns]::GetHostName()
}

$addresses = @()
try {
    $addresses = @([System.Net.Dns]::GetHostAddresses($resolvedHost) |
            Where-Object { $_.AddressFamily -eq "InterNetwork" } |
            ForEach-Object { $_.IPAddressToString })
}
catch {
    $addresses = @()
}

$connectionString = "Host=$resolvedHost;Port=$Port;Database=$DatabaseName;Username=$UserName;" +
    "Password=$UserPassword;SSL Mode=$SslMode;Search Path=$SchemaName;Application Name=LiftUp;Timeout=15"

$notes = "Verbindungsdaten der zentralen LiftUp-Datenbank. Enthaelt das Passwort im Klartext - " +
    "Datei sicher aufbewahren und nicht in ein Repository legen. " +
    "Die Anwendung liest diese Datei im Reiter 'Datenbank' ein."

if ([string]::IsNullOrWhiteSpace($HostName)) {
    $notes += " Der Servername wurde aus dem Computernamen abgeleitet; falls die Clients den Server anders erreichen, -HostName beim Aufruf setzen."
}
if ($listenAddresses -and $listenAddresses -ne "*" -and $listenAddresses -ne "localhost") {
    $notes += " listen_addresses steht auf '$listenAddresses'."
}

$connectionData = [ordered]@{
    format           = "liftup-server"
    formatVersion    = 1
    generatedUtc     = [DateTime]::UtcNow.ToString("o")
    generatedBy      = "Install-ServerDatabase.ps1"
    installerVersion = "1.0.14"
    host             = $resolvedHost
    hostName         = $env:COMPUTERNAME
    addresses        = @($addresses)
    port             = $Port
    database         = $DatabaseName
    schema           = $SchemaName
    username         = $UserName
    password         = $UserPassword
    sslMode          = $SslMode
    serverVersion    = $serverVersion
    schemaVersion    = [int]$schemaVersion
    tableCount       = $tables.Count
    tables           = @($tables)
    connectionString = $connectionString
    clientCidr       = @($grantedNetworks)
    notes            = $notes
}

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory -and -not (Test-Path $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$json = $connectionData | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($OutputPath, $json, (New-Object System.Text.UTF8Encoding($false)))

if ($isWindowsHost) {
    # Nur der ausfuehrende Benutzer darf die Datei lesen - sie enthaelt das Passwort.
    try {
        & icacls $OutputPath /inheritance:r /grant:r "$($env:USERNAME):(R,W)" | Out-Null
    }
    catch {
        Write-Warn "Die Zugriffsrechte der Datei konnten nicht eingeschraenkt werden."
    }
}

Write-Ok "Datei: $((Resolve-Path $OutputPath).Path)"

if ($listenAddresses -and $listenAddresses -ne "*") {
    Write-Warn "listen_addresses steht auf '$listenAddresses'. Clients erreichen den Server nur, wenn er auf dem Netzwerk lauscht:"
    Write-Warn "  psql -U postgres -c `"ALTER SYSTEM SET listen_addresses = '*';`" und Dienst neu starten."
}

Write-Host ""
Write-Host "Fertig. Die Anwendung einlesen lassen:" -ForegroundColor Cyan
Write-Host "  LiftUp -> Reiter 'Datenbank' -> Datei waehlen -> $([System.IO.Path]::GetFileName($OutputPath)) einlesen"
Write-Host "  Server: $resolvedHost`:$Port  Datenbank: $DatabaseName  Schema: $SchemaName  Benutzer: $UserName"
Write-Host ""
