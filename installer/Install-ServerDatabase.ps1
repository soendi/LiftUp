# Richtet die Server-Datenbank (PostgreSQL) für LiftUp ein.
#
# Das Skript prüft zuerst, ob PostgreSQL bereits vorhanden ist, und überspringt
# die Installation dann vollständig. Fehlt PostgreSQL, wird es still über winget
# installiert und anschließend die Datenbank samt LiftUp-Benutzer angelegt.
#
# Aufruf (als Administrator, einmal pro Datenbank-Server):
#   .\Install-ServerDatabase.ps1 -SuperuserPassword "Geheim123" -UserPassword "LiftUp123"
#
# Nur prüfen, nichts ändern:
#   .\Install-ServerDatabase.ps1 -WhatIf
#
# Hinweis: Aus gutem Grund getrennt vom Client-Setup. Eine PostgreSQL-Installation
# braucht Administratorrechte und läuft als Dienst; auf jedem Arbeitsplatz wäre sie
# falsch und würde die stillen Benutzer-Updates aushebeln.

[CmdletBinding()]
param(
    [string]$SuperuserPassword,
    [string]$DatabaseName = "liftup",
    [string]$UserName = "liftup",
    [string]$UserPassword,
    [int]$Port = 5432,
    [string]$PostgresPackage = "PostgreSQL.PostgreSQL.17",
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"

function Test-PostgresService {
    $service = Get-Service -Name "postgresql*" -ErrorAction SilentlyContinue
    return [bool]$service
}

function Test-PostgresRegistry {
    return (Test-Path "HKLM:\SOFTWARE\PostgreSQL\Installations")
}

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

function Get-PsqlPath {
    $roots = Get-ChildItem "C:\Program Files\PostgreSQL" -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending
    foreach ($root in $roots) {
        $candidate = Join-Path $root.FullName "bin\psql.exe"
        if (Test-Path $candidate) { return $candidate }
    }

    $command = Get-Command psql.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    return $null
}

Write-Host "LiftUp – Server-Datenbank prüfen" -ForegroundColor Cyan

$installed = (Test-PostgresService) -or (Test-PostgresRegistry)
$listening = Test-PostgresPort -TargetPort $Port

if ($installed) {
    Write-Host "PostgreSQL ist bereits installiert – Installation wird übersprungen." -ForegroundColor Green
}
else {
    Write-Host "PostgreSQL wurde nicht gefunden." -ForegroundColor Yellow
    if ($WhatIf) {
        Write-Host "[WhatIf] winget install --id $PostgresPackage --silent" -ForegroundColor Yellow
        return
    }

    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "winget ist nicht verfügbar. Bitte PostgreSQL manuell installieren: https://www.postgresql.org/download/windows/"
    }

    if ([string]::IsNullOrWhiteSpace($SuperuserPassword)) {
        throw "Für eine Neuinstallation wird -SuperuserPassword benötigt (Passwort des Benutzers 'postgres')."
    }

    Write-Host "Installiere $PostgresPackage (still) …"
    winget install --id $PostgresPackage --silent --accept-package-agreements --accept-source-agreements `
        --override "--mode unattended --unattendedmodeui none --serverport $Port --superpassword `"$SuperuserPassword`""
    if ($LASTEXITCODE -ne 0) {
        throw "Die Installation von PostgreSQL ist fehlgeschlagen (Exitcode $LASTEXITCODE)."
    }

    Start-Sleep -Seconds 5
    $listening = Test-PostgresPort -TargetPort $Port
}

if (-not $listening) {
    Write-Warning "Auf Port $Port antwortet kein PostgreSQL. Datenbank wird nicht angelegt."
    exit 2
}

if ([string]::IsNullOrWhiteSpace($UserPassword)) {
    Write-Warning "Ohne -UserPassword wird kein Datenbankbenutzer angelegt."
    exit 0
}

$psql = Get-PsqlPath
if (-not $psql) {
    throw "psql.exe wurde nicht gefunden. Datenbank und Benutzer bitte manuell anlegen."
}

if ($WhatIf) {
    Write-Host "[WhatIf] Datenbank '$DatabaseName' und Benutzer '$UserName' würden angelegt." -ForegroundColor Yellow
    return
}

$env:PGPASSWORD = $SuperuserPassword

function Invoke-Sql {
    param([string]$Sql)
    & $psql -h 127.0.0.1 -p $Port -U postgres -d postgres -tAc $Sql
    if ($LASTEXITCODE -ne 0) { throw "SQL fehlgeschlagen: $Sql" }
}

$roleExists = (Invoke-Sql "SELECT 1 FROM pg_roles WHERE rolname = '$UserName'").Trim()
if ($roleExists -eq "1") {
    Write-Host "Benutzer '$UserName' existiert bereits – übersprungen." -ForegroundColor Green
}
else {
    Invoke-Sql "CREATE ROLE $UserName LOGIN PASSWORD '$UserPassword'" | Out-Null
    Write-Host "Benutzer '$UserName' angelegt." -ForegroundColor Green
}

$dbExists = (Invoke-Sql "SELECT 1 FROM pg_database WHERE datname = '$DatabaseName'").Trim()
if ($dbExists -eq "1") {
    Write-Host "Datenbank '$DatabaseName' existiert bereits – übersprungen." -ForegroundColor Green
}
else {
    Invoke-Sql "CREATE DATABASE $DatabaseName OWNER $UserName" | Out-Null
    Write-Host "Datenbank '$DatabaseName' angelegt." -ForegroundColor Green
}

Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Fertig. Clients verbinden sich mit:" -ForegroundColor Cyan
Write-Host "  LIFTUP_PG_HOST=<ServerName>"
Write-Host "  LIFTUP_PG_PORT=$Port"
Write-Host "  Datenbank: $DatabaseName, Benutzer: $UserName"
