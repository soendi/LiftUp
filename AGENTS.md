# Hinweise für die Arbeit an LiftUp

## Versionen und Releases

- Die Version steht in `Directory.Build.props` (`<Version>`).
- **Es wird nur die dritte Stelle erhöht:** aus `1.3.0` wird `1.3.1`, `1.3.2`, … – **kein Sprung
  auf `1.4`** und keine Erhöhung der zweiten Stelle, auch nicht bei neuen Funktionen.
- Veröffentlicht wird über den Workflow `release.yml` (workflow_dispatch) mit `version` als
  Eingabe, also `1.3.x`; der Workflow setzt das Tag `v1.3.x` und lädt die sechs Assets hoch.
- Nach dem Release prüfen: Tag zeigt auf den freigegebenen Commit, und alle sechs Assets
  antworten mit HTTP 200.

## Sprache und Stil

- Oberfläche, Kommentare, Dokumentation und Commit-Messages auf Deutsch (Schweizer Rechtschreibung,
  also «ss» statt «ß»).
- Änderungen klein und zielgerichtet halten; bestehende Muster und Dienste weiterverwenden.

## Prüfen

- `dotnet build LiftUp.sln -c Release` und
  `dotnet test tests/LiftUp.Core.Tests/LiftUp.Core.Tests.csproj -c Release`.
- Vier PostgreSQL-Tests werden ohne Datenbankserver übersprungen; das ist erwartet.
