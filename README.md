# LiftUp

Lokale Windows-Anwendung für Projektverwaltung mit Benutzerverwaltung und Rechteverteilung.
Alle Daten liegen in einer SQLite-Datei auf dem Arbeitsplatz; die Datenstruktur ist auf einen
späteren Abgleich mit einer zentralen PostgreSQL-Datenbank vorbereitet.

## Technik

| Baustein | Verwendung |
| --- | --- |
| WPF (.NET 8, `net8.0-windows`) | Oberfläche |
| LiftUp.Core (`net8.0`) | Domäne, Datenbank, Dienste, Rechte, Sync-Gerüst – ohne UI-Abhängigkeiten |
| Microsoft.Data.Sqlite | lokale Datenbank |
| xUnit | Tests der Core-Schicht |

## Starten

Voraussetzungen: Windows, .NET 8 SDK (oder Visual Studio 2022 mit .NET-8-Workload).

```bash
dotnet build LiftUp.sln
dotnet run --project src/LiftUp.App
```

Tests:

```bash
dotnet test tests/LiftUp.Core.Tests
```

Beim ersten Start wird die Datenbank automatisch angelegt:

```
%LOCALAPPDATA%\LiftUp\liftup.db
```

Der Ort lässt sich über die Umgebungsvariable `LIFTUP_DB` auf einen anderen Pfad legen,
z. B. auf ein Netzlaufwerk.

**Erste Anmeldung:** Benutzer `admin`, Passwort `admin`. Das Startpasswort muss direkt nach
der Anmeldung geändert werden (mindestens 8 Zeichen, Buchstaben und Ziffern).

## Funktionsumfang

### Benutzer und Rechte

- Anmeldung mit Benutzername und Passwort (PBKDF2-HMAC-SHA256, 210.000 Iterationen, Zufallssalz je Konto).
- Benutzer anlegen, bearbeiten, deaktivieren, löschen, Passwort zurücksetzen.
- Rollen mit Rechtebitmaske; Rechte werden im Rollen-Reiter per Häkchen verteilt.
- Systemrollen `Administrator`, `Projektleiter`, `Betrachter` sind vorangelegt und nicht löschbar.
- Der letzte aktive Administrator kann sich nicht selbst die Rechte entziehen oder das Konto löschen.
- Das eigene Konto kann nicht gelöscht oder deaktiviert werden.

Rechte im Einzelnen: Projekte ansehen, Projekte anlegen, Projekte bearbeiten, Projekte löschen,
Benutzer verwalten, Rollen und Rechte verwalten. Die Prüfung passiert in der Core-Schicht
(`Session.Require`), nicht nur in der Oberfläche – ein Aufruf ohne Recht wirft
`PermissionDeniedException`.

### Änderungsprotokoll

- Jede schreibende Aktion landet in `ChangeLog`: Bereich, Vorgang, betroffener Datensatz, Bearbeiter,
  Arbeitsplatz und Zeitpunkt. Das Protokoll dient gleichzeitig als Outbox für den späteren Abgleich.
- Der Reiter „Protokoll“ zeigt die Einträge, neueste zuerst, filterbar nach Bereich, Bearbeiter,
  Zeitraum und Freitext. Die Detailanzeige zeigt die gespeicherte Nutzlast.
- Sichtbar ist der Reiter für Rollen mit dem Recht „Änderungsprotokoll ansehen“; Administrator und
  Projektleiter haben es voreingestellt.
- Passwörter und Hashes stehen nie in der Nutzlast.

### Projekte

- Projekt anlegen, bearbeiten, löschen (Nr., Name, Kunde, Beschreibung, Status, Projektordner).
- Projektordner frei wählbar auf jedem Laufwerk: über „Ordner wählen …“ oder als getippter Pfad.
- Fehlt der Ordner, kann er beim Speichern angelegt werden (Häkchen, standardmäßig aktiv).
- Prüfungen: Pfadformat, verfügbares Laufwerk, kein Dateipfad, kein doppelt zugeordneter Ordner,
  eindeutige Projektnummer.
- Löschen entfernt den Datensatz; auf Wunsch wird der Projektordner zusätzlich in den Papierkorb
  verschoben (Rückfrage im Dialog, standardmäßig „Nein“).
- Die Liste zeigt zusätzlich, ob der Ordner aktuell vorhanden ist.

## Datenmodell

Tabellen: `Users`, `Roles`, `Projects`, `ChangeLog`, `Settings`, `SchemaInfo`.
`ChangeLog` führt zusätzlich den Bearbeiter (`OriginUser`) und die Geräte-Id, damit sich Einträge
ihrem Ursprung zuordnen lassen.

Jede fachliche Tabelle führt:

- `Id` als GUID (clientseitig erzeugt, damit Datensätze ohne Server kollisionsfrei bleiben),
- `RowVersion` (Zähler, wird bei jeder Änderung erhöht),
- `IsDeleted` (weiches Löschen, damit Löschungen synchronisierbar bleiben),
- UTC-Zeitstempel `CreatedUtc` / `UpdatedUtc` sowie `CreatedBy` / `UpdatedBy`.

## Vorbereitung für PostgreSQL

Der Abgleich ist bewusst noch nicht aktiv, aber vollständig vorbereitet:

1. **Änderungsprotokoll als Outbox** – jede schreibende Aktion schreibt in derselben Transaktion
   einen Eintrag nach `ChangeLog` (Entität, Id, Operation, JSON-Nutzlast, Geräte-Id, RowVersion).
   Passwörter und Hashes stehen nie in der Nutzlast.
2. **Geräte-Id** – beim ersten Start wird eine `DeviceId` erzeugt und in `Settings` abgelegt, damit
   Änderungen ihrem Ursprungsarbeitsplatz zugeordnet werden können.
3. **Transport-Schnittstelle** – `ISyncTransport` (`PushAsync` / `PullAsync`) kapselt den Server.
   `SyncService` überträgt offene Einträge und bestätigt sie erst nach erfolgreichem Push.
   Ohne konfiguriertes Ziel meldet die Anwendung „lokal“ und ändert nichts.

Für den echten Betrieb fehlen zwei Schritte: eine `ISyncTransport`-Implementierung (z. B. REST-
Endpunkt vor PostgreSQL oder Npgsql direkt) und die Konfliktregel pro Entität. Empfohlen:
RowVersion vergleichen, bei Konflikt feldweise zusammenführen und Löschungen über `IsDeleted`
gewinnen lassen.

## Projektstruktur

```
src/LiftUp.Core/
  Domain/        Benutzer, Rollen, Rechte, Projekte, Sitzung
  Data/          Datenbank, Schema, Änderungsprotokoll, Pfade
  Security/      Passwort-Hashing und -Richtlinie
  Repositories/  SQL und Mapping
  Services/      Anmeldung, Benutzer, Rollen, Projekte, Ordner
  Sync/          Transport-Schnittstelle und Sync-Dienst
src/LiftUp.App/
  Views/         Projekt-, Benutzer- und Rollenreiter plus Dialoge
tests/LiftUp.Core.Tests/
```

## Projektverwaltung

Projekte haben Nummer, Name, Kunde, Ansprechpartner, Beschreibung, Verantwortlichen und Status.
Die **Projektnummer wird automatisch** im Format `2026-0001` vergeben (eigener Nummernkreis je Jahr,
in derselben Transaktion wie das Anlegen). Eine vorhandene Nummer kann stattdessen eingetragen werden.

Beim Anlegen entsteht der Projektordner samt Unterordnern `01_Dokumente`, `02_Bilder`, `03_PDF`
und `04_Vorlagen`; bei bestehenden Ordnern werden nur fehlende Unterordner ergänzt. Der Pfad darf
auf jedem Laufwerk liegen (lokal, Netzlaufwerk, Wechseldatenträger).

Die Übersicht bietet Suche (Name, Nummer, Kunde, Ansprechpartner, Verantwortlicher, Beschreibung),
Statusfilter und Sortierung nach Projekt, Nummer, Kunde, Verantwortlichem, Status oder Änderungsdatum.

**Archiv:** Archivieren ändert ausschließlich den Status und hält Zeitpunkt und Benutzer fest.
Dateien und Ordner werden weder verschoben noch gelöscht. Archivierte Projekte verschwinden aus der
normalen Übersicht und erscheinen über „Archiv anzeigen“; dort lassen sie sich wiederherstellen.

**Rollen:** Administrator, Projektleiter, Mitarbeiter, Leser. Leser sehen Projekte und Archiv,
Projektleiter dürfen zusätzlich archivieren, löschen und Dokumente erzeugen. Die frühere Rolle
„Betrachter“ heißt bei bestehenden Datenbanken automatisch „Leser“ (Schema-Migration auf Version 2).

## PDF-Erzeugung und Vorlagen

Vorlagen bestehen aus HTML und CSS (Register "Vorlagen"). Der Editor bietet HTML-Eingabe,
Vorschau mit Beispieldaten und eine Platzhalterliste, aus der per Doppelklick eingefügt wird.

| Platzhalter | Inhalt |
| --- | --- |
| `{{project.number}}` | Projektnummer |
| `{{project.name}}` | Projektname |
| `{{project.customer}}` | Kunde |
| `{{project.contact}}` | Ansprechpartner |
| `{{project.responsible}}` | Verantwortlicher |
| `{{project.description}}` | Beschreibung |
| `{{project.status}}` | Status |
| `{{date}}`, `{{datetime}}` | Datum bzw. Datum mit Uhrzeit |
| `{{user}}` | Angemeldeter Benutzer |
| `{{snippet:Name}}` | Textbaustein mit diesem Namen (HTML) |

Werte aus der Datenbank werden HTML-escaped eingesetzt (`&`, `<`, `>`, `"`, `'`), Umlaute bleiben
lesbar. Textbausteine werden bewusst als HTML eingefügt.

**Versionierung:** Jedes Speichern erzeugt eine neue, unveränderliche Vorlagenversion. Erzeugte
PDFs merken sich die verwendete Version; wird die Vorlage später geändert, bleiben alte PDFs
unverändert und weiter abrufbar.

**Erzeugung:** Der Reiter "PDF" rendert die Vorschau und das PDF über die unter Windows
vorinstallierte **WebView2**-Engine – damit stimmen `@page`, Seitenumbrüche, Tabellen und
Schriftgrößen. Formate A4/A3, Hoch-/Querformat und Seitenrand sind einstellbar. Die PDFs landen
im Projektordner unter `03_PDF`; die Historie zeigt Datei, Vorlage, Version und Zeitpunkt und kann
die Datei direkt öffnen.

## Installation und Updates

### Installer bauen

Das MSI entsteht unter Windows (WiX 5 läuft nur dort):

```powershell
.\installer\build-installer.ps1 -Version 1.0.1
```

Ergebnis: `artifacts\LiftUp-Setup.exe` (Installationsprogramm), `artifacts\LiftUp-Setup.msi`
und `artifacts\SHA256SUMS.txt`.

Zum Installieren dient **`LiftUp-Setup.exe`**. Das Programm prüft zuerst die .NET 8
**Desktop** Runtime und lädt sie bei Bedarf von Microsoft nach
(`aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe`, rund 58 MB), bevor es das MSI startet.
Ist die Runtime vorhanden, wird nichts geladen – dann läuft alles wie gehabt.

Die Installation selbst erfolgt je Benutzer nach `%LOCALAPPDATA%\Programs\LiftUp` – ohne
Administratorfreigabe, mit Verknüpfungen im Startmenü und auf dem Desktop. Das Setup bringt
den deutschen WiX-Assistenten mit eigenem Banner, Dialogbild, Symbol und Lizenztext mit.
Verknüpfungen, Programmeintrag und Deinstallation verwaltet Windows selbst.

Stille Installation und Deinstallation:

```
LiftUp-Setup.exe /quiet /norestart
msiexec /i LiftUp-Setup.msi /qn /norestart
msiexec /x {9E4C21B0-3B76-4C55-9F1E-3D6B0C7A8E42} /qn
```

Das Paket ist absichtlich schlank: veröffentlicht wird framework-abhängig (rund 2,3 MB).
Das MSI enthält bewusst keine eigene Runtime-Prüfung mehr, sondern überlässt sie dem
Installationsprogramm; selbstenthaltend wären es etwa 150 MB.

### Stille Updates über GitHub

`UpdateService` (in `LiftUp.Core/Updates`) fragt `api.github.com/repos/soendi/LiftUp/releases/latest` ab:

1. Version aus dem Tag (`v1.2.0`) mit der laufenden Version vergleichen.
2. `LiftUp-Setup.msi` herunterladen und gegen `SHA256SUMS.txt` prüfen; bei Abweichung wird abgebrochen.
3. Installation still per `msiexec /qn /norestart` starten, danach öffnet sich LiftUp wieder.

Das passiert beim Start automatisch. Ein Merker unter `%LOCALAPPDATA%\LiftUp\update-attempt.json`
verhindert Endlosschleifen, falls die Installation scheitert (6 Stunden Sperre je Version).
Abschalten mit `setx LIFTUP_DISABLE_AUTOUPDATE 1`; manuell auslösen über „Updates“ im Kopfbereich.

### Version veröffentlichen

```bash
git tag v1.0.1 && git push origin v1.0.1
```

`.github/workflows/release.yml` baut auf `windows-latest`, führt die Tests aus, erzeugt MSI,
Installationsprogramm und Prüfsumme und legt das Release an. Für die Update-Prüfung ohne Token
muss das Repository öffentlich sein.

### Datenbanken: prüfen und nachholen

LiftUp braucht zwei Datenbanken – die lokale SQLite-Datei und später die zentrale PostgreSQL-Datenbank.
So geht das Setup damit um:

| Prüfung | Verhalten |
| --- | --- |
| Lokale Datenbank | SQLite ist in der Anwendung enthalten (`e_sqlite3.dll`), es gibt nichts zu installieren. Datei und Schema entstehen beim ersten Start – bzw. beim ersten Lauf von `LiftUp.Preflight` – unter dem Konto des Anwenders. |
| Server-Datenbank | `LiftUp.Preflight` prüft die Erreichbarkeit (TCP auf 5432). Das Setup installiert sie nicht still mit – Begründung unten. |
| .NET 8 Desktop Runtime | Das MSI prüft sie über eine Launch-Bedingung und bricht mit klarem Hinweis ab, wenn sie fehlt. |

Prüfen, ohne die Oberfläche zu starten:

```
LiftUp.Preflight.exe                                  # Textausgabe
LiftUp.Preflight.exe --json                           # maschinenlesbar
LiftUp.Preflight.exe --server db-server:5432 --db D:\Daten\liftup.db
```

Rückgabewerte: `0` alles bereit, `1` lokale Datenbank nicht nutzbar, `2` nur die Server-Datenbank fehlt.
Das Werkzeug liegt nach der Installation neben `LiftUp.App.exe`.

Server-Datenbank einrichten (einmalig auf dem Datenbank-Server, als Administrator):

```powershell
.\installer\Install-ServerDatabase.ps1 -SuperuserPassword "…" -UserPassword "…"
.\installer\Install-ServerDatabase.ps1 -WhatIf      # nur prüfen, nichts ändern
```

Das Skript erkennt eine vorhandene Installation (Dienst `postgresql*`, Registry, Port 5432) und
überspringt sie; sonst installiert es PostgreSQL still über winget und legt Datenbank und Benutzer
idempotent an. Clients finden den Server über `LIFTUP_PG_HOST` und `LIFTUP_PG_PORT`.

**Warum nicht automatisch im Client-Setup?** Eine PostgreSQL-Installation braucht Administratorrechte,
läuft als Dienst und gehört auf den Datenbank-Server, nicht auf jeden Arbeitsplatz. Sie würde außerdem
die Benutzer-Installation verlassen und damit die stillen Updates aushebeln. Deshalb prüft das
Client-Setup und meldet, während das Server-Skript einrichtet.

### Marken-Assets

Symbol und Setup-Bilder werden ohne externe Werkzeuge erzeugt:

```bash
python3 installer/make-assets.py
```

Ergebnis: `src/LiftUp.App/Assets/LiftUp.ico`, `installer/assets/banner.bmp`, `installer/assets/dialog.bmp`.

## Datensicherung

Der Reiter **Sicherung** (nur für Administratoren) sichert die Datenbank. Sicherungen entstehen mit
`VACUUM INTO` und sind deshalb auch im laufenden Betrieb konsistent – es wird nie eine offene Datei
kopiert. Einstellbar sind Zielordner, Aufbewahrung (Standard 30 Tage; die jüngste Sicherung bleibt
immer erhalten) und ein zusätzlicher Cloud-Upload. Vor jeder Wiederherstellung legt LiftUp
automatisch eine Sicherung des aktuellen Stands an (`…-vor-wiederherstellung.db`), eine falsche
Auswahl ist also rückholbar.

Ziele: lokaler Ordner, Google Drive, OneDrive. Angemeldet wird sich mit dem eigenen Konto – auch
mit einem Firmenkonto. Die Anmeldung läuft über den Gerätecode: LiftUp zeigt einen Code, öffnet die
Anbieterseite im Browser, und nach der Freigabe ist die Verbindung aktiv; ein Passwort sieht LiftUp
nie. Die Tokens liegen DPAPI-verschlüsselt und an den Windows-Benutzer gebunden unter
`<Datenbankordner>\cloud\`. Gesichert wird ausschließlich im App-Ordner des Anbieters
(`Files.ReadWrite.AppFolder` bzw. Google `drive.appdata`), nicht in den übrigen Dateien des Kontos.

### Anwendung registrieren (einmalig)

Beide Anbieter verlangen eine eigene Anwendungsregistrierung; LiftUp bringt keine Client-IDs mit.
Eingetragen werden sie im Reiter **Sicherung**.

OneDrive / Microsoft:

1. https://portal.azure.com → Entra ID → App-Registrierungen → Neue Registrierung.
2. Kontotypen: „Konten in einem beliebigen Organisationsverzeichnis und persönliche Microsoft-Konten“.
3. Unter Authentifizierung → Erweitert: „Öffentliche Clientflüsse zulassen“ aktivieren.
4. Die Anwendungs-ID (Client-ID) übernehmen; ein Client-Schlüssel ist nicht nötig.

Google Drive:

1. https://console.cloud.google.com → Projekt anlegen, Google Drive API aktivieren.
2. OAuth-Zustimmungsbildschirm einrichten (extern; Testbenutzer oder veröffentlicht).
3. Anmeldedaten → OAuth-Client-ID → Anwendungstyp „TVs und Geräte mit eingeschränktem Zugriff“.
4. Client-ID und Client-Schlüssel übernehmen.

Berechtigungen: Microsoft `offline_access Files.ReadWrite.AppFolder`, Google `drive.appdata`.

## Bekannte Grenzen

- Die Oberfläche ist bisher nur kompiliert, nicht auf einem Windows-Rechner durchgeklickt.
- Die Cloud-Anmeldung (Google Drive, OneDrive) ist nicht gegen die echten Anbieter durchgespielt:
  dafür braucht es die Client-IDs des Kunden und einen Browser. Geprüft ist der Ablauf bis zum
  Gerätecode sowie die Fehlerbehandlung; Uploads sind ungetestet.
- Die Schemamigrationen sind nur über Tests abgedeckt, nicht gegen echte Vorversionen im Feld.
- Projektordner werden beim Löschen nicht automatisch gesichert oder archiviert.
- Ein Ordner, der nach dem Anlegen umbenannt oder verschoben wird, muss im Projekt nachgezogen werden.
- Das MSI konnte in dieser Entwicklungsumgebung nicht erzeugt werden: WiX 5 läuft nur unter Windows.
  Die Definition ist syntaktisch geprüft, das Paket selbst entsteht über `build-installer.ps1` oder die CI.
- Die Update-Logik ist gegen einen HTTP-Stub getestet, nicht gegen ein echtes GitHub-Release.
- `installer/Install-ServerDatabase.ps1` konnte hier nicht ausgeführt werden (kein PowerShell unter Linux);
  geprüft ist nur die Logik, nicht der Lauf gegen einen echten PostgreSQL-Server.

## Prüfliste vor der Auslieferung (Windows)

Einmal auf einem frischen Windows-Rechner durchgehen. Was hier nicht abgehakt ist, gilt als ungetestet.

**Installation**

- [ ] `LiftUp-Setup.exe` auf einem Rechner ohne .NET-Desktop-Runtime ausführen; die Runtime wird nachgeladen.
- [ ] Startmenü-Eintrag vorhanden, Programm startet ohne Fehlermeldung.
- [ ] Installation ohne Administratorrechte möglich.
- [ ] Deinstallation entfernt die Programmdateien, lässt `%LOCALAPPDATA%\LiftUp` (Datenbank, Bilder, Sicherungen) stehen.
- [ ] Installation über eine vorhandene Version (Update) übernimmt Einstellungen und Datenbank.

**Anmeldung und Rechte**

- [ ] Erstanmeldung `admin`/`admin` erzwingt einen Passwortwechsel.
- [ ] Je einen Benutzer pro Rolle anlegen: Administrator, Projektleiter, Mitarbeiter, Leser.
- [ ] Mit jeder Rolle anmelden: Leser sieht keine bearbeitenden Schaltflächen, Mitarbeiter pflegt Projekte,
      Administrator sieht Benutzer, Sicherung und Protokoll, Projektleiter sieht das Protokoll ebenfalls.
- [ ] Falsches Passwort: Meldung prüfen und notieren, was passiert.
- [ ] Reiter „Protokoll“: Einträge erscheinen mit Bearbeiter, Zeit und Objekt; Filter nach Bereich,
      Bearbeiter, Zeitraum und Freitext greifen; als Leser ist der Reiter nicht vorhanden.
- [ ] Eine Datenbank aus einer Vorversion öffnen: die Spalte `OriginUser` wird ergänzt, alte Einträge
      zeigen „unbekannt“.

**Projekte**

- [ ] Neues Projekt erhält die nächste Nummer im Format `JJJJ-0001`.
- [ ] Projektordner wird mit den Standard-Unterordnern angelegt, auch auf einem Netzlaufwerk.
- [ ] Suche, Filter und Sortierung liefern die erwarteten Treffer.
- [ ] Archivieren ändert nur den Status; nichts wird verschoben oder gelöscht. Wiederherstellen funktioniert.
- [ ] Ein Projekt auf einer Freigabe anlegen und mit einem zweiten Benutzer öffnen.

**PDF**

- [ ] Vorlage anlegen und ändern; die Versionierung ist in der Historie sichtbar.
- [ ] Platzhalter `{{project.*}}`, `{{date}}`, `{{user}}`, `{{snippet:…}}`, `{{bild:…}}` werden ersetzt –
      auch mit Umlauten und Sonderzeichen.
- [ ] PDF landet in `03_PDF`, Eintrag in der Historie mit Vorlagenversion.
- [ ] Ohne WebView2-Runtime: verständliche Fehlermeldung statt Absturz.
- [ ] Bild aus der Bibliothek erscheint im PDF, auch wenn das Projekt auf einer Freigabe liegt.

**Sicherung**

- [ ] Lokal sichern; die Datei erscheint in der Liste und im gewählten Ordner.
- [ ] Aufbewahrung prüfen: alte Sicherungen verschwinden, die jüngste bleibt.
- [ ] Wiederherstellen: Sicherheitsbackup entsteht, Daten sind zurück, Hinweis auf Neustart erscheint.
- [ ] Google Drive mit einem Firmenkonto anmelden (Gerätecode im Browser bestätigen), sichern, Datei im
      App-Ordner des Kontos prüfen.
- [ ] Dasselbe mit OneDrive.
- [ ] Nach Ablauf der Token-Gültigkeit erneut sichern: die Anmeldung erneuert sich ohne Zutun.
- [ ] Abmelden entfernt die Verbindung; Sichern mit Cloud-Ziel meldet sauber, dass keine Anmeldung vorliegt.

**Update**

- [ ] Ältere Version installieren, nach Updates suchen lassen, stilles Update bis zum Neustart begleiten.
- [ ] Nach dem Update sind Datenbank und Einstellungen unverändert.
