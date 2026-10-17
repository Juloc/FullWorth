# FullWorth clean start

Isolierter Ausgangspunkt nach dem Architekturvorbild von `Juloc/Jularr` (`clean-start`).
Die produktive FullWorth-Anwendung unter `../src` wird weder gestartet noch geändert.

## Architektur

- **FullWorth.Frontend:** einziger ASP.NET-Core-Host, Razor Pages, serverseitige Darstellung.
- **FullWorth.Backend:** Klassenbibliothek mit versioniertem Datenvertrag und Service.
- Razor ruft den Backend-Service über Dependency Injection auf, nicht über internes HTTP.
- Derselbe Service stellt `GET /api/v1/public/status` bereit.
- Kein EF, keine Datenbankverbindung, kein Media-Code, keine zweite Web-Anwendung.

`Data/Public/Status/V1` und `Service/Public/Status/V1` zeigen die vorläufige
Contract/Service-Struktur. Domain-Logic, SQL, Integrations und Workers werden erst
mit konkreten Finanzfunktionen eingeführt. Der umfangreiche Jularr-Service-Dispatcher
wird nicht blind kopiert, bevor Authentifizierung und Transaktionsregeln entschieden sind.

## Start

Voraussetzung: .NET SDK 10.

```sh
dotnet build clean-start/FullWorth.CleanStart.slnx -c Release
dotnet run --project clean-start/src/FullWorth.Frontend
```

Im Repository-Stamm ausgeführt; die lokale URL steht in der Konsolenausgabe.

Alternativ aus `clean-start/`:

```sh
docker compose up --build
```

Dann `http://127.0.0.1:8098` öffnen. Der Container bindet nur an localhost.

## Abgrenzung

Dies ist ein öffentliches, funktionsarmes Framework-Gerüst, **keine Finanz-App**
und kein produktives Deployment. Es enthält keine Accounts, Anmeldung,
Berechtigungen, Datenbank, Banking oder Migrationen. Es greift nicht auf
bestehende FullWorth-Daten zu. Für diese Bereiche müssen vor der Integration
eigene Sicherheits-, Daten- und Regressionstests entstehen.
