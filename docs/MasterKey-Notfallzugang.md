# MasterKey-Notfallzugang

Der MasterKey-Lizenzcode ist ein **Notfallzugang** für Kunden, die ihr Passwort vergessen haben.
Er wird mit der separaten Anwendung **MasterKey.exe** erzeugt und von LiftUp **vollständig
offline** geprüft. Der reguläre Login mit Benutzername und Passwort bleibt unverändert und ist
weiterhin der normale Weg.

## Ablauf

1. Im Anmeldefenster «Passwort vergessen?» aufklappen.
2. Den Lizenzcode aus MasterKey.exe in das Feld «MasterKey-Notfallzugang» einfügen.
3. «Mit MasterKey anmelden» – Benutzername und Passwort sind nicht nötig.
4. LiftUp prüft Signatur und Gültigkeit. Nur bei vollständig erfolgreicher Prüfung entsteht eine
   Notfall-Sitzung mit allen Rechten. Danach ist ausschliesslich die Ansicht
   «Notfall-Wiederherstellung» verfügbar.
5. Passwort des gewählten Kontos neu setzen, Konto entsperren oder – falls nötig – einen neuen
   Administrator anlegen.
6. «Notfallzugang beenden» und sich mit dem neuen Passwort regulär anmelden.

Gelöscht wird dabei nichts: Konten, Projekte und Dokumente bleiben unverändert.

## Aufbau des Codes

| Bestandteil                    | Länge    |
| ------------------------------ | -------- |
| Formatversion (`0x01`)         | 1 Byte   |
| Erzeugungszeitpunkt (IssuedAt) | 8 Bytes  |
| Ablaufzeitpunkt (ExpiresAt)    | 8 Bytes  |
| Ed25519-Signatur               | 64 Bytes |
| **Gesamt**                     | **81 Bytes** |

* Die Zeitstempel sind Unix-Sekunden in UTC als **vorzeichenlose 64-Bit-Ganzzahl in
  Big-Endian** (Netzwerk-Reihenfolge) – so wie sie MasterKey.exe schreibt und wie es
  `public-key.txt` festhält.
* Signiert werden der Kontext `MASTERKEY-LICENSE-V1` (ASCII) **und** die 17 Nutzdatenbytes:
  `signature = Ed25519.Sign(privateKey, "MASTERKEY-LICENSE-V1" || payload)`.
* Kodierung: Base64 **URL-safe ohne Padding** (`-` und `_`, kein `=`) → **108 Zeichen**.

Damit ist LiftUp mit MasterKey.exe kompatibel, solange MasterKey.exe dieselbe Byte-Reihenfolge,
denselben Kontext und dieselbe Kodierung verwendet.

## Gültigkeitsprüfung

Reihenfolge in `MasterKeyCodeValidator.Validate`:

1. Eingabeformat (108 Zeichen, nur Base64-URL-Zeichen)
2. Dekodierung (81 Bytes)
3. Formatversion (nur `0x01`)
4. Nutzdaten
5. Signatur mit dem öffentlichen Schlüssel
6. Plausibilität der Zeitstempel (Ablauf nicht vor Erzeugung, Erzeugung nach dem Jahr 2000)
7. Zeitfenster: gültig ab `IssuedAt − 5 Minuten`, gültig bis `ExpiresAt`, Vergleich in UTC

## Öffentlichen Schlüssel hinterlegen

Der private Schlüssel liegt **ausschliesslich** bei MasterKey.exe. In LiftUp steht nur der
öffentliche Schlüssel, und zwar an genau einer Stelle:

`src/LiftUp.Core/Security/MasterKeyCodeValidator.cs` → `MasterKeyKeys.EmergencyPublicKeyBase64`

Aktuell hinterlegt (Base64, 32 Bytes): `uLqDxYoMmqVkXkxzCAvI2txYVfy7xPkV42wJnyT4jtg=` – identisch mit
`public-key.bin` im Repository. Fingerabdruck (SHA-256 über die Rohbytes):
`7F6E8F07557B8E65A2CA89BC3C59EB57FFAB9C6F23F20C2DF47BBBD883EAD22F`.

Der Schlüssel wechseln:

1. In MasterKey.exe ein neues Schlüsselpaar erzeugen und den öffentlichen Schlüssel als Base64
   ausgeben lassen (`Convert.ToBase64String(publicKey)`, 32 Bytes ohne Header).
2. Den Wert in `EmergencyPublicKeyBase64` ersetzen und den Fingerabdruck im Kommentar
   aktualisieren.
3. LiftUp neu bauen und veröffentlichen. Alte Codes werden danach abgelehnt.

Solange der Wert leer ist, gilt **fail-closed**: Jeder Code wird mit der Meldung
«Der MasterKey-Notfallzugang ist in dieser Installation nicht eingerichtet.» abgelehnt. Eine
Änderung an einer lokalen Konfigurationsdatei oder an der Datenbank aktiviert den Zugang
hingegen nicht – der Schlüssel ist Teil des Programms und nur durch einen neuen Build änderbar.

## Rechte und Protokollierung

* Die Notfall-Sitzung hat `Permission.All`; die Prüfung erfolgt zentral in `Session.Require`
  und damit in jedem Core-Dienst (`UserService`, `RoleService`, …), nicht in der Oberfläche.
* Im Änderungsprotokoll erscheinen `EmergencyAccess/Login`, `EmergencyAccess/Logout` sowie jede
  Passwortänderung mit dem Zusatz «(Notfallzugang)».
* **Nicht** protokolliert werden Lizenzcodes, Passwörter, Hashes oder Schlüsselwerte.

## Sitzungsablauf

Die Notfall-Sitzung endet nach einer einstellbaren Inaktivitätszeit (Standard 15 Minuten,
Bereich 5–120). Die Einstellung liegt in der Tabelle `Settings` unter
`EmergencySessionMinutes` und lässt sich in der Notfall-Ansicht ändern. Jede Bedienung
verlängert die Sitzung; nach Ablauf meldet LiftUp ab und verlangt eine neue Anmeldung.

## Grenzen

Der Code ist offline prüfbar – das schützt seine Echtheit, aber nicht die Integrität einer
vollständig vom Kunden kontrollierten Installation. Wer die Programmdateien oder die Datenbank
selbst manipuliert, kann die Prüfung umgehen. Der Notfallzugang ist deshalb als
Wiederherstellungshilfe gedacht, nicht als Schutz gegen einen Angreifer mit Kontrolle über den
Arbeitsplatz. Codes sollten kurz befristet und nur auf dem vereinbarten Weg übergeben werden.

## Tests

`tests/LiftUp.Core.Tests/MasterKeyEmergencyTests.cs` deckt ab: gültiger, abgelaufener und noch
nicht gültiger Code, Uhrenversatz, manipulierte Nutzdaten und Signatur, fremder öffentlicher
Schlüssel, beschädigter Code, unbekanntes Format, unplausible Zeitstempel, fehlender Schlüssel,
unveränderter regulärer Login, Rechte des Notfallzugangs, Passwort-Reset, Wiederherstellung des
Administratorpassworts, neuer Administrator, Entsperren eines Kontos, Protokollierung ohne Code
und den Sitzungsablauf nach Inaktivität.
