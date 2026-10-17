# FullWorth – Bestand offener Legacy-Issues

> **Snapshot vom 17.10.2026.** Vollständige Kopie der zu diesem Zeitpunkt offenen GitHub-Issues (ohne Pull Requests).
> Dieses Dokument ist ein Archiv, keine Freigabe zur Umsetzung und kein weiterer aktiver Backlog.
> Die originalen GitHub-Issues bleiben unverändert und sind über die untenstehenden Links erreichbar.
> Für den Neuaufbau werden ausschließlich neue, ausdrücklich als Clean Start gekennzeichnete Planungs-Issues angelegt. Alte Entscheidungen gelten nicht automatisch als neu bestätigt.

Erfasst: **5 offene Issues**. Offene Pull Requests sind getrennt aufgeführt und **keine** Issues.

## Übersicht

| Issue | Titel | Letzte Änderung (UTC) |
|---|---|---|
| [#119](https://github.com/Juloc/FullWorth/issues/119) | Bug: „Umbuchen“-Checkbox soll Änderungen direkt speichern | 2026-09-14T17:52:28Z |
| [#130](https://github.com/Juloc/FullWorth/issues/130) | FinTS/Banking: ING protokollkonform reparieren, C24 korrekt anbinden und vollständige sichere Diagnostik ergänzen | 2026-09-22T06:44:11Z |
| [#131](https://github.com/Juloc/FullWorth/issues/131) | Architektur/UX: Einheitlicher Kontoimport für Finanzguru, CSV/XLSX, Kontoauszüge und Bank-PDFs | 2026-09-27T07:48:17Z |
| [#172](https://github.com/Juloc/FullWorth/issues/172) | C24: PSD2/XS2A-Anbindung statt geratenem FinTS-Profil | 2026-09-22T06:44:10Z |
| [#179](https://github.com/Juloc/FullWorth/issues/179) | Rechner: Dienstrad/JobRad – echte Gesamtrechnung inkl. Rente und Lohnersatzleistungen | 2026-09-27T08:48:50Z |

## Offene Pull Requests (nur Referenz, nicht als Issues archiviert)

- [PR #181](https://github.com/Juloc/FullWorth/pull/181) — Add isolated clean-start foundation based on Jularr architecture
- [PR #81](https://github.com/Juloc/FullWorth/pull/81) — Sync main into frontend architecture finish

## Vollständige Issue-Texte

### Issue #119: Bug: „Umbuchen“-Checkbox soll Änderungen direkt speichern

- Quelle: https://github.com/Juloc/FullWorth/issues/119
- Status zum Stichtag: `open`
- Erstellt: 2026-09-14T17:52:28Z
- Zuletzt aktualisiert: 2026-09-14T17:52:28Z
- Labels: keine

#### Ursprünglicher Issue-Text (inhaltlich unverändert)

## Problem
Die **„Umbuchen“-/Umbuchungs-Checkbox** speichert ihren Zustand aktuell nicht direkt beim Umschalten. Dadurch ist für eine einfache Ja/Nein-Einstellung ein zusätzlicher Speicherschritt nötig bzw. der geänderte Zustand kann verloren gehen.

## Erwartetes Ergebnis
- Beim Aktivieren oder Deaktivieren der Checkbox wird der neue Zustand **sofort gespeichert**.
- Es ist kein zusätzlicher „Speichern“-Klick nur für diese Checkbox erforderlich.
- Die UI zeigt den gespeicherten Zustand anschließend zuverlässig an.
- Bei einem Speicherfehler darf die Oberfläche nicht so wirken, als wäre die Änderung erfolgreich übernommen worden.

## Acceptance Criteria
- [ ] Checkbox aktivieren -> Zustand wird unmittelbar persistiert.
- [ ] Checkbox deaktivieren -> Zustand wird unmittelbar persistiert.
- [ ] Nach Reload bleibt der zuletzt gespeicherte Zustand erhalten.
- [ ] Kein zusätzlicher Speichern-Button/-Schritt ist für diese Änderung nötig.
- [ ] Bei API-/Speicherfehler wird der vorherige Zustand wiederhergestellt oder der Fehler klar angezeigt.
- [ ] Relevante UI-/API-Tests decken Aktivieren und Deaktivieren ab.

## Hinweis
Nur als Bug/Backlog aufnehmen. **Noch nicht implementieren.**

---

### Issue #130: FinTS/Banking: ING protokollkonform reparieren, C24 korrekt anbinden und vollständige sichere Diagnostik ergänzen

- Quelle: https://github.com/Juloc/FullWorth/issues/130
- Status zum Stichtag: `open`
- Erstellt: 2026-09-14T20:39:37Z
- Zuletzt aktualisiert: 2026-09-22T06:44:11Z
- Labels: keine

#### Ursprünglicher Issue-Text (inhaltlich unverändert)

## Problem / Ziel

Die aktuelle FinTS-Implementierung ist für ING Deutschland nur teilweise korrekt und enthält mehrere protokollrelevante Annahmen, die zu nicht funktionierenden Verbindungen, falschen Segmenten, fehlerhaften TAN-Flows und unvollständigen Depotdaten führen können.

Zusätzlich darf C24 nicht einfach als weiteres `KnownBanks`-FinTS-Profil ergänzt werden: Für C24 ist die dokumentierte Drittanbieter-Anbindung PSD2/XS2A; ein offizieller C24-FinTS-Endpunkt ist nicht Teil der belegten Integration. C24 selbst ist als eigener Task nach #172 ausgelagert; dieses Issue verlangt hier nur, dass kein geratenes FinTS-Profil für C24 entsteht.

Ziel dieses Tasks:

- ING Deutschland über FinTS 3.0 sauber und bankkonform anbinden
- generische FinTS-Kernlogik dort korrigieren, wo aktuell Segmentversionen/Datentypen falsch behandelt werden
- C24 **nicht** als erfundene FinTS-Bank konfigurieren (die eigentliche PSD2/XS2A-Anbindung ist #172)
- vollständige, strukturierte und sichere Diagnostik ergänzen, damit Fehler ohne PIN/TAN/Finanzdaten-Leak nachvollziehbar sind
- Root-Cause-Fixes im kanonischen Banking-/FinTS-Pfad; keine Workarounds oder parallelen Sonderpfade

---

## Aktueller Stand

### ING-Profil

Aktuell existiert in `src/FullWorth.FinTs/KnownBanks.cs` nur ING:

- Bank-ID: `ing-de`
- BLZ: `50010517`
- BIC: `INGDDEFFXXX`
- Endpoint: `https://fints.ing.de/fints/`
- Capabilities aktuell inklusive `Tan` und `DecoupledTan`

Der Endpoint und die Bankkennung sind grundsätzlich korrekt.

### C24

Es existiert aktuell kein C24-FinTS-Profil. Das soll **nicht** dadurch gelöst werden, dass einfach BLZ/BIC/URL geraten bzw. hart codiert werden.

C24 dokumentiert für Drittanbieter den PSD2/XS2A-Zugriff. Deshalb muss C24 über den dafür vorgesehenen Banking-Provider-/PSD2-Pfad geplant/angebunden werden.

---

# 1. ING Zugang / Login

Die UI bzw. API soll für ING nur die tatsächlich benötigten Daten abfragen:

- Zugangsnummer / Benutzername
- PIN / Passwort
- ggf. den technisch notwendigen registrierten FinTS-Produkt-Identifier aus der FullWorth-Konfiguration

Keine zusätzliche Kunden-ID erzwingen, wenn ING diese nicht verwendet.

Der `ProductId` bleibt Teil des FinTS-Protokolls und muss aus der kanonischen Konfiguration kommen. Kein versteckter Fallback und kein zufälliger Default.

---

# 2. ING TAN-/SCA-Verhalten korrigieren

Die aktuelle Implementierung interpretiert `3920`, `HITANS` und erkannte TAN-Verfahren generisch und kann dadurch automatisch von Security Function `999` auf Zwei-Schritt-/Decoupled-TAN wechseln.

Für ING muss das reale FinTS-Verhalten separat berücksichtigt werden. Der reine Lesezugriff darf nicht durch einen generischen TAN-Pfad kaputtkonfiguriert werden.

Prüfen und korrigieren:

- welche Security Function ING für den unterstützten Read-only-FinTS-Zugriff tatsächlich erwartet
- wann `HKTAN` überhaupt gesendet werden darf/muss
- ob `DecoupledTan` im ING-Profil für diesen Pfad überhaupt eine korrekte Capability ist
- `3920` nicht blind als Aufforderung behandeln, irgendeine angebotene Security Function zu wählen
- SCA-/Freigabezustände sauber als bankseitigen Zustand modellieren
- kein TAN-Polling, wenn die Bank/Methode das nicht tatsächlich unterstützt

Keine bankübergreifende Heuristik, die ING zufällig zum Funktionieren bringt.

---

# 3. `AccountGroup()` nach Segmentversion korrekt implementieren

Aktuell wird grob unterschieden:

- Version >= 6 -> `IBAN + BIC`
- ältere Version -> `AccountNumber + SubAccount + 280 + BIC`

Das ist nicht ausreichend und für relevante Segmentversionen falsch.

Die Kontoverbindung muss entsprechend der tatsächlichen FinTS-Datenelementgruppe des jeweiligen Segmentes aufgebaut werden.

Mindestens sauber trennen:

- v5: klassisches Konto/Subkonto/Land/BLZ-Modell
- v6: korrektes Account3-/Kontoverbindungsmodell
- v7: korrektes KTI1-/IBAN-Modell

Nicht BIC an eine Position schreiben, an der die Bank BLZ erwartet.

Die Version darf ausschließlich aus den BPD/Bankparametern bzw. einer explizit belegten unterstützten Version kommen.

---

# 4. Keine erfundenen Segmentversionen / saubere BPD-Auswertung

Aktuell gibt es Fallbacks wie z. B. für `HKWPD` auf Version 7.

Das ist für Depotabfrage nicht zulässig, wenn diese Segmentversion in der Spezifikation/Bankparametern nicht existiert.

Anforderungen:

- Segmentversionen primär aus BPD ableiten
- nur explizit unterstützte Fallbacks verwenden
- keine Version nur deshalb wählen, weil sie numerisch höher ist
- bei nicht unterstützter Version klar und diagnostizierbar abbrechen
- Request- und Response-Segmentversionen getrennt korrekt abbilden

---

# 5. ING Direkt-Depot / `HKWPD` / `HIWPD` korrekt umsetzen

Der aktuelle Holding-Parser behandelt `HIWPD` sinngemäß als Textgruppen mit einzelnen Wertpapierfeldern.

Das ist für die relevanten Depotantworten falsch: Depotbestände werden über MT535 transportiert und müssen entsprechend geparst werden.

Umsetzung:

- nur tatsächlich unterstützte `HKWPD`-Versionen verwenden
- binäre `HIWPD`-Nutzdaten korrekt extrahieren
- MT535 robust parsen
- mindestens erhalten:
  - ISIN
  - WKN soweit vorhanden
  - Wertpapiername
  - Stückzahl / Nominal
  - Kurs
  - Kurswährung
  - Kursdatum
  - Marktwert
  - Marktwertwährung
  - Börsen-/Handelsplatz soweit geliefert
- Rohdaten nicht als Ersatz für fachliche Felder verwenden
- unbekannte optionale MT535-Felder tolerant behandeln
- keine falschen Werte raten

Wenn einzelne Felder fehlen, bleibt das Feld leer; keine erfundenen Defaultwerte.

---

# 6. MT940-Umsätze korrekt zusammensetzen

Aktuell werden `HIKAZ`-Binärdaten segmentweise geparst.

FinTS-Antworten können MT940-Daten über mehrere Antwortsegmente / Nachrichtenbestandteile verteilen.

Deshalb:

1. zu einer fachlichen Antwort gehörende gebuchte MT940-Nutzdaten in korrekter Reihenfolge zusammensetzen
2. anschließend als Gesamtstrom parsen
3. vorgemerkte Umsätze entsprechend getrennt behandeln
4. erst danach Transaktionen erzeugen

Weiterhin sicherstellen:

- Buchungsdatum
- Valutadatum
- Betrag/Vorzeichen
- Währung
- Gegenpartei
- Verwendungszweck
- bankseitige Referenzen soweit verfügbar
- Pending/Booked

Die bestehende `ExternalKey`-Bildung darf bei Pagination/erneutem Sync nicht zu Duplikaten führen.

---

# 7. Touchdown / Pagination

`3040`-Touchdown wird grundsätzlich bereits verarbeitet. Diesen Pfad beibehalten, aber härten:

- Touchdown exakt dem betreffenden Geschäftsvorfall zuordnen
- keine TAN-Anforderung bei Folgepages erneut falsch erzeugen
- MaxPages nur als Sicherheitsgrenze
- bei Erreichen von MaxPages Warnung/Fehler diagnostizierbar machen; nicht still unvollständige Daten als vollständig markieren
- Dubletten zwischen Seiten verhindern
- Endzustand klar erkennen

ING-Historie weiterhin auf den bankseitig unterstützten Zeitraum begrenzen; aktuell maximal 90 Tage.

---

# 8. HITANS / TAN-Methoden versioniert parsen

Der aktuelle Parser liest mehrere Felder über feste Indizes und grobe `version >= 6` / `>= 7`-Abfragen.

Das ist für einen generischen FinTS-Parser zu fragil.

Umsetzung:

- unterstützte `HITANS`-Versionen explizit modellieren/parsen
- Feldpositionen nicht über eine einzige gemeinsame Heuristik interpretieren
- Security Function
- Prozessvariante
- Methodenname
- TAN-Medium-Pflicht
- Decoupled-Flag
- Polling-Grenzen
- Wartezeiten
- Segmentversion

müssen pro Version korrekt gelesen werden.

Unbekannte Version -> klarer `unsupported_segment_version`-Fehler statt Raten.

---

# 9. FinTS Response Codes / Fehlerzustände

Die vorhandene Zuordnung für z. B. falsche PIN / gesperrten Zugang erweitern und systematisieren.

Mindestens unterscheiden:

- falsche Zugangsdaten
- Zugang gesperrt
- TAN/SCA erforderlich
- Freigabe noch ausstehend
- Verfahren nicht unterstützt
- Segmentversion nicht unterstützt
- Produkt-ID ungültig/nicht zugelassen
- Dialog abgelaufen / ungültiger Dialog
- Bank temporär nicht verfügbar
- Request fachlich abgelehnt
- unbekannter Bankfehler

Bankcode und bankseitige Meldung sollen intern diagnostizierbar bleiben, ohne Secrets oder komplette Finanznutzdaten zu loggen.

---

# 10. C24 nicht als FinTS-Profil vortäuschen

Keine Änderung wie:

```csharp
KnownBanks.C24 = new FinTsBankProfile(... geraten ...)
```

Die eigentliche C24-Anbindung (PSD2/XS2A, Provider-Abstraktion, Consent-Lifecycle) ist als eigener Folge-Task in #172 herausgelöst. Dieses Issue verlangt nur, dass hier keine geratene FinTS-Sonderbehandlung für C24 entsteht.
---

# 11. Verpflichtendes strukturiertes Logging / Diagnostik

Dieser Teil ist Voraussetzung der Umsetzung, nicht optionales Debugging.

FinTS/Banking muss so geloggt werden, dass ein Fehler aus Produktionslogs nachvollziehbar ist, ohne Zugangsdaten oder Kontoinhalte offenzulegen.

## Pro Operation loggen

Strukturierte Felder, keine zusammengesetzten Freitext-Strings als einzige Information:

- `provider` (`fints`, später ggf. `c24-xs2a`)
- `bankId`
- `connectionId`
- `operation`
  - synchronize
  - open-dialog
  - close-dialog
  - accounts
  - balance
  - transactions
  - portfolio
  - tan-submit
  - tan-poll
  - ingest
- `dialogId` nur wenn sicher/sinnvoll
- `messageNumber`
- Request-Segmenttyp, z. B. `HKSAL`, `HKKAZ`, `HKWPD`, `HKTAN`
- verwendete Segmentversion
- ausgewählte Security Function
- TAN-Verfahrenskennung **ohne TAN selbst**
- Bank-Response-Code
- Segmentreferenz
- Ergebniszustand
- Dauer in ms
- Pagination-Seite
- Touchdown vorhanden ja/nein, aber nicht unnötig kompletter Tokeninhalt
- Anzahl gelesener Konten
- Anzahl Buchungen
- Anzahl Depotpositionen
- Anzahl an Backend übergebener Datensätze
- relevante Retry-/Polling-Nummer

## Log-Level

### Information

- Start/Ende einer Verbindung/Synchronisierung
- erkannte Anzahl Accounts/Depots
- abgeschlossene Seiten
- erfolgreicher Abschluss
- TAN/SCA-Zustandswechsel ohne vertrauliche Daten

### Warning

- Bank liefert unbekannte optionale Daten
- Pagination-Sicherheitslimit erreicht
- erwartetes optionales Feld fehlt
- temporärer Bankfehler
- Poll noch pending
- Bankparameter ändern sich unerwartet

### Error

- Protokollfehler
- nicht unterstützte Segmentversion
- Parserfehler
- Bank lehnt Request ab
- Dialog kann nicht fortgesetzt werden
- Ingest scheitert

Exception mitloggen, aber sanitisiert.

## Niemals loggen

Auch nicht auf Debug/Trace:

- PIN
- TAN
- Passwort
- OAuth Client Secret
- Access-/Refresh-Token
- komplette Authorization-Header
- vollständige User-ID/Zugangsnummer, wenn sie Personenbezug ermöglicht
- vollständige IBAN
- komplette Kontonummer
- kompletter MT940-/MT535-Rohinhalt
- kompletter Verwendungszweck
- komplette Bankantwort mit Finanzdaten
- `authorizationId` / serialisierte Connection-Secrets

IBAN/Konto höchstens über vorhandene interne Account-/Connection-ID bzw. kontrolliert maskierte letzte Stellen referenzieren, falls überhaupt notwendig.

Die zentrale Vorgabe aus `AGENTS.md` gilt ausdrücklich: **keine Credentials/Secrets in Code, Docs, Issues, Kommentaren oder Logs**.

## Korrelation

Eine komplette Sync-Ausführung braucht eine stabile Korrelation, z. B. `syncRunId`/`correlationId`, damit folgende Schritte zusammen auffindbar sind:

```text
connect/open
-> account discovery
-> balance
-> transaction page 1..n
-> depot
-> ingest
-> close
```

Keine neue konkurrierende State-Quelle dafür erzeugen; nur Request-/Log-Korrelation.

## Nutzer-/Operator-Diagnostik

`lastError` darf nicht nur `FINTS_SYNC_FAILED` sagen, wenn ein konkreter sanitizierter Fehlercode bekannt ist.

Beispiele:

- `FINTS_PIN_WRONG`
- `FINTS_ACCESS_LOCKED`
- `FINTS_PRODUCT_ID_REJECTED`
- `FINTS_UNSUPPORTED_SEGMENT_VERSION`
- `FINTS_TAN_REQUIRED`
- `FINTS_TAN_PENDING`
- `FINTS_DIALOG_EXPIRED`
- `FINTS_BANK_TEMPORARILY_UNAVAILABLE`
- `FINTS_PROTOCOL_ERROR`
- `FINTS_PARSE_ERROR`

UI muss daraus eine verständliche Aktion ableiten können; technische Details bleiben im Log.

---

# 12. Architektur / Single Source of Truth

Gemäß `AGENTS.md`:

- Root Cause im `FullWorth.FinTs`-/Banking-Kern beheben
- keine zweite parallele ING-Implementierung
- keine C24-FinTS-Sonderbehandlung ohne offiziellen FinTS-Pfad (C24-Anbindung selbst: #172)
- Segmentdefinition/-version genau an einer Stelle modellieren
- Bankprofil enthält nur bankbezogene Unterschiede, keine Kopie des gesamten Protokolls
- Connection-/SCA-Zustand hat einen kanonischen Speicherpfad
- keine Environment-Variable als Notfall-Override für falsche BPD-/Bankdaten
- keine stillen Runtime-Fallbacks auf alte/falsche Segmentformate

---

# 13. Tests

FinTS benötigt deutlich mehr Tests als die derzeit sehr kleine Testsuite.

Mindestens ergänzen:

### Wire / Segmente

- `HKSAL` v5 korrekt
- `HKSAL` v6 korrekt
- `HKSAL` v7 korrekt
- `HKKAZ` relevante Versionen korrekt
- `HKWPD` nur unterstützte Versionen
- unbekannte Version -> expliziter Fehler

### Bankparameter

- BPD/UPD Merge
- Security-Function-Auswahl
- ING-spezifisches Verhalten
- HITANS je unterstützter Version

### TAN/SCA

- kein TAN erforderlich
- TAN erforderlich
- decoupled pending
- polling
- ungültige/abgelaufene Challenge
- Bankfehler während TAN

### MT940

- eine Antwort
- über mehrere `HIKAZ`-Segmente aufgeteilt
- mehrere Touchdown-Seiten
- pending + booked
- Sonderzeichen/Latin1
- stabile Deduplizierung

### MT535

- echte repräsentative Fixture(s)
- mehrere Positionen
- fehlende optionale Werte
- Fremdwährung
- korrekter Marktwert/Kurs/Stückzahl

### Logging

Tests/Guards, dass sicherheitskritische Werte nicht in Logs erscheinen:

- PIN nicht enthalten
- TAN nicht enthalten
- vollständige IBAN nicht enthalten
- Roh-MT940/MT535 nicht enthalten
- Connection-Secret nicht enthalten

Gleichzeitig prüfen, dass Fehlercode, Operation, Segmenttyp/-version und Correlation-ID vorhanden sind.

---

# 14. Akzeptanzkriterien

- [ ] ING-Endpoint/Bankprofil bleiben korrekt und zentral definiert.
- [ ] ING kann mit Zugangsnummer/Benutzername + PIN über den belegten FinTS-Leseweg verbunden werden.
- [ ] Keine unnötige Kunden-ID wird verlangt.
- [ ] `ProductId` kommt ausschließlich aus der kanonischen Konfiguration und Fehler dafür sind klar sichtbar.
- [ ] ING wird nicht durch falsche automatische TAN-/Security-Function-Auswahl blockiert.
- [ ] `AccountGroup` entspricht für jede unterstützte Segmentversion der richtigen FinTS-Datenelementgruppe.
- [ ] BLZ/BIC werden nicht verwechselt.
- [ ] Keine erfundene Segmentversion wird verwendet.
- [ ] BPD bestimmen die unterstützten Geschäftsvorfallversionen.
- [ ] ING Girokonto und Extra-Konto liefern Saldo und Umsätze korrekt.
- [ ] Umsätze bis zum bankseitig erlaubten Maximum werden mit Touchdown vollständig geladen.
- [ ] Mehrere `HIKAZ`-Fragmente werden vor MT940-Parsing korrekt zusammengesetzt.
- [ ] Pending/Booked bleiben unterscheidbar.
- [ ] Direkt-Depot wird über korrekte MT535-Auswertung gelesen.
- [ ] Depotpositionen enthalten die tatsächlich verfügbaren ISIN/WKN/Name/Menge/Kurs/Marktwert-Daten korrekt.
- [ ] HITANS wird je unterstützter Version korrekt geparst.
- [ ] Unbekannte Protokoll-/Segmentversionen führen zu verständlichem, explizitem Fehler.
- [ ] Bankresponse-Codes werden sinnvoll in kanonische FullWorth-Fehlercodes übersetzt.
- [ ] C24 wird nicht als geratenes FinTS-Profil implementiert. Die eigentliche C24-Anbindung ist als #172 ausgelagert.
- [ ] Jede Banking-Sync-Ausführung ist über eine Correlation-ID vollständig nachvollziehbar.
- [ ] Logs enthalten Operation, Bank, Segmenttyp/-version, Response-Code, Status, Dauer und Mengenangaben.
- [ ] Logs enthalten niemals PIN, TAN, Tokens, Secrets, vollständige IBAN/Kontonummer oder komplette MT940/MT535-Finanzdaten.
- [ ] `lastError` ist bei bekannten Fehlern spezifisch und UI-verwertbar.
- [ ] Relevante Unit-/Parser-/Banking-Tests sind vorhanden.
- [ ] `dotnet test tests/FullWorth.FinTs.Tests/FullWorth.FinTs.Tests.csproj --nologo` ist grün.
- [ ] relevante `FullWorth.Banking.Tests` sind grün.
- [ ] `dotnet build FullWorth.slnx -c Release --nologo` ist grün.
- [ ] Betroffene technische Dokumentation ist aktualisiert.

---

## Umsetzung / Agent-Control

Dieses Issue ist der dauerhafte Backlog-Eintrag gemäß `AGENTS.md`.

Vor Implementierungsbeginn muss der ausführende Agent:

1. aktuellen `main` holen
2. relevante offene Issues/PRs erneut auf Überschneidungen prüfen
3. `Juloc/agent-control` Issue #1 lesen und `currentLedgerIssue` auflösen
4. Coordination State prüfen
5. engen Claim für `src/FullWorth.FinTs`, relevante `src/FullWorth.Banking`-Pfade und zugehörige Tests setzen
6. direkt nach dem Claim den Coordination State erneut prüfen
7. Root-Cause-Fix im kanonischen Pfad durchführen; keine parallelen Fallback-Implementierungen
8. relevante FinTS-/Banking-Tests und Release-Build ausführen
9. Dokumentation aktualisieren, falls Verhalten/Operator-Erwartungen geändert wurden
10. Claim nach Abschluss/PR sauber freigeben bzw. als done markieren

Kein Agent-Control-Claim wird allein für das Anlegen dieses Issues benötigt; er beginnt erst unmittelbar vor der tatsächlichen Implementierung.


---

### Issue #131: Architektur/UX: Einheitlicher Kontoimport für Finanzguru, CSV/XLSX, Kontoauszüge und Bank-PDFs

- Quelle: https://github.com/Juloc/FullWorth/issues/131
- Status zum Stichtag: `open`
- Erstellt: 2026-09-14T21:25:59Z
- Zuletzt aktualisiert: 2026-09-27T07:48:17Z
- Labels: keine

#### Ursprünglicher Issue-Text (inhaltlich unverändert)

## Ziel

FullWorth soll für **importierte bzw. nicht dauerhaft verbindbare Konten** einen einzigen, konsistenten Importpfad verwenden.

Betroffen sind insbesondere:

- Finanzguru XLSX
- generische CSV/XLSX-Exporte
- direkte Bankexporte, z. B. C24 CSV/XLSX
- MT940 / CAMT
- Bank-Kontoauszüge als PDF, z. B. Ikano
- weitere spätere Datei-/App-Adapter

Die Quelle soll nur ein **Adapter vor derselben kanonischen Import-Engine** sein. Es darf nicht je Quelle ein eigenes Konto-, Saldo-, Historien- und UX-Modell entstehen.

---

## Aktuelles Problem

Der Import ist heute in mehrere fachlich unterschiedliche Flows aufgeteilt:

- allgemeiner CSV/XLSX-Buchungsimport
- eigener Finanzguru-XLSX-Flow
- MT940/CAMT-Kontoauszugimport
- separater Broker-PDF-Flow

Dadurch verhalten sich Importe an verschiedenen Stellen unterschiedlich.

Besonders problematisch ist der Finanzguru-Pfad:

- Finanzguru erzeugt spezielle `finanzguru-import`-/Historienkonten.
- Diese werden später mit einem echten Konto verknüpft und Buchungen werden verschoben/zusammengeführt.
- Finanzguru ist dadurch technisch fast wie ein eigener Kontoprovider modelliert, obwohl es nur die Importquelle ist.
- Konto-Zuordnung, Balance-Bestätigung und Reconciliation haben Sonderlogik.
- Fehler wie #112 entstehen dadurch an einem separaten Link-/Matching-Pfad.

Zusätzlich bezeichnet die UI MT940/CAMT bereits als Lösung für Banken ohne Verbindung, nennt dabei z. B. Ikano, obwohl Ikano Kontoauszüge praktisch als PDF bereitstellt und der aktuelle Statement-Import keine Bank-PDFs akzeptiert.

---

# Zielmodell

## 1. Ein kanonischer Import-Kern

Alle Quellen laufen nach der Erkennung in dasselbe Modell:

```text
Datei
  -> Adapter erkennen
  -> normalisierte Konten / Buchungen / Salden / Metadaten
  -> bestehende Konten zuordnen
  -> Duplikate + Überschneidungen prüfen
  -> Vorschau
  -> Commit
  -> gemeinsamer ImportBatch / Provenienz / Rollback
```

Quelladapter dürfen Daten erkennen und normalisieren, aber **keine eigene parallele Konto- oder Historienarchitektur** aufbauen.

---

## 2. Importquelle ist nicht Kontotyp

Ein C24-Konto bleibt ein C24-Konto, auch wenn seine Daten aus Finanzguru oder einer CSV kommen.

Ein Ikano-Konto bleibt ein Ikano-Konto, auch wenn es ausschließlich über PDF-Kontoauszüge aktualisiert wird.

Ein PayPal-Konto bleibt ein PayPal-Konto, unabhängig davon, ob Daten von PayPal selbst, Finanzguru oder CSV stammen.

Am Konto soll stattdessen die Herkunft transparent sichtbar sein, z. B.:

```text
C24 Girokonto
Aktualisierung: Dateiimport
Letzter Import: 14.09.2026
Buchungen bis: 13.09.2026
Saldo vom: 31.08.2026
Quellen: Finanzguru XLSX, C24 CSV
```

Kein künstliches `Finanzguru Import`-Konto nur für denselben realen Account.

---

## 3. Konto-Matching vor dem Commit

Nach Analyse der Datei erkennt FullWorth die enthaltenen Quellkonten und schlägt Zuordnungen vor.

Beispiel Finanzguru:

```text
C24 Girokonto      -> C24 Girokonto      eindeutig
PayPal             -> PayPal             wahrscheinlich
Ikano Ratenkredit  -> noch nicht vorhanden
DKB Girokonto      -> DKB Girokonto      eindeutig / live verbunden
```

Regeln:

- eindeutige starke Identität darf automatisch vorgeschlagen werden
- unsichere Zuordnung muss bestätigt werden
- neue Konten können direkt im Import angelegt werden
- einmal bestätigte Zuordnungen werden für spätere Importe derselben Quelle wiederverwendet
- Wallets und Konten ohne IBAN, insbesondere PayPal, Wise, Revolut und manuelle Konten, dürfen nicht durch IBAN-zentrierte Logik ausgeschlossen werden
- Multi-Currency-Konten müssen berücksichtigt werden

#112 soll durch den gemeinsamen Matching-Pfad fachlich mitgelöst werden, nicht durch einen weiteren Finanzguru-Sonderfall.

---

## 4. Kein notwendiges Historien-/Shadow-Konto

Der bisherige Finanzguru-Pfad mit separatem Archiv-/Historienkonto soll langfristig entfallen, sofern ein kanonisches Zielkonto feststeht.

Neue Daten werden direkt dem kanonischen Konto zugeordnet.

Falls die Zuordnung beim ersten Import noch ungeklärt ist, sollen Daten in einem **Import-Stagingzustand** bleiben und nicht als dauerhaftes sichtbares Schattenkonto im normalen Kontomodell landen.

Migration bestehender `finanzguru-import`-Konten berücksichtigen:

- bestehende explizite Links respektieren
- Buchungen nicht verlieren
- keine Doppelzählung im Nettovermögen
- bestehende Nutzeränderungen/Verknüpfungen bewahren

---

## 5. Einheitlicher UX-Flow

Für Buchungs-/Kontoimporte grundsätzlich:

### Schritt 1: Datei auswählen

Alternativ aus einem Konto heraus:

`Konto -> Aktualisieren -> Datei importieren`

### Schritt 2: Automatische Erkennung

FullWorth zeigt z. B.:

```text
Quelle erkannt: Finanzguru
4 Konten gefunden
312 Buchungen
Zeitraum 01.01.2024 – 13.09.2026
```

### Schritt 3: Konten zuordnen

Nur Konten mit unsicherer/neuer Zuordnung brauchen Interaktion.

### Schritt 4: Änderungen prüfen

Beispiel:

```text
42 neu
181 bereits vorhanden
6 ergänzt/verbessert
2 unsicher
Saldo vom 31.08.2026 erkannt
1 Datenlücke erkannt
```

### Schritt 5: Importieren

Danach gemeinsame Importhistorie/Rollback.

Beim Wiederholungsimport mit gespeichertem Mapping soll der Flow deutlich kürzer sein.

---

# 6. Deduplizierung über Quellen hinweg

Eine reale Buchung darf nicht doppelt entstehen, nur weil sie aus mehreren Quellen kam.

Beispiel:

- C24-Buchung zuerst über Finanzguru importiert
- später dieselbe Buchung in C24 CSV

Erwartung:

- vorhandene reale Buchung erkennen
- nicht duplizieren
- ggf. bessere/neue Felder ergänzen
- Provenienz um zweite Quelle erweitern

Matching sollte stufenweise arbeiten:

1. stabile Quell-/Provider-ID, falls vorhanden
2. bekannte Identität aus vorherigem Import
3. starke fachliche Signatur, z. B. Konto + Datum + Betrag + Gegenpartei/Referenz
4. fuzzy/heuristischer Match nur als Vorschlag, nie still bei Unsicherheit

Nutzeränderungen an Kategorien, Splits, Verträgen, Tags usw. dürfen durch spätere Importe nicht ungefragt überschrieben werden.

---

# 7. Feld-Prioritäten / Merge

Importquellen können verschiedene Qualität haben.

Beispiel:

- direkte Bankdatei liefert verlässliche Bank-/Buchungsdaten
- Finanzguru liefert zusätzlich gute Kategorien, Umbuchungskennzeichnung und ggf. Vertragsinformationen

Deshalb Feldweise-Provenienz bzw. klare Merge-Regeln vorsehen.

Mindestens unterscheiden:

- Bank-/Provider-Rohdaten
- importierte Quellwerte
- FullWorth-Regeln/Erkennung
- Nutzeränderungen

Nutzeränderung hat höchste Priorität und darf nicht automatisch überschrieben werden.

Rohdaten der Quelle weiterhin sicher erhalten, soweit sinnvoll.

---

# 8. Finanzguru-Adapter verbessern

Bestehenden Finanzguru-Reader weiterverwenden, aber an den gemeinsamen Import-Kern anbinden.

Weiterhin übernehmen:

- Buchungstag
- Betrag / Währung
- Gegenpartei
- Verwendungszweck
- E-Ref
- Haupt-/Unterkategorie
- Umbuchungskennzeichnung
- Splits
- Buchungs-ID
- Quellrohfelder

Zusätzlich prüfen und strukturiert übernehmen, soweit im Export vorhanden:

- Vertrags-/Aboinformationen
- weitere nützliche Analysefelder
- Kontoidentität und Anzeigename getrennt

Der Finanzguru-Export ist besonders nützlich, weil mehrere Konten in einer Datei aktualisiert werden können.

---

# 9. Generisches CSV/XLSX und direkte Bankexporte

Der vorhandene allgemeine Mapping-/Stage-/Commit-Pfad soll die Basis werden.

Für bekannte Quellen können Adapter/Presets die manuelle Spaltenzuordnung überspringen, z. B.:

- C24 CSV/XLSX
- Finanzfluss
- Outbank
- spätere bekannte Bankformate

Unbekannte CSV/XLSX bleiben über das generische Mapping importierbar.

Ein erkannter bekannter Export soll trotzdem dieselbe Review-/Commit-Komponente verwenden.

---

# 10. MT940 / CAMT

Bestehenden Statement-Parser und die aktuelle datierte Saldo-Logik weiterverwenden.

Besonders wichtig:

- Schlussstand mit echtem `ReferenceDate`
- älterer Import darf neueren Live-/manuellen Saldo nicht überschreiben
- Buchungen + Saldo im selben gemeinsamen Review anzeigen
- dieselbe Import-Provenienz und Deduplizierung wie CSV/Finanzguru

---

# 11. Bank-PDF-Import

Neben Broker-PDFs braucht FullWorth **Bank-/Kreditkontoauszüge als PDF**.

Erster konkreter Zieladapter: Ikano.

Pipeline:

```text
PDF
 -> eingebetteten Text lesen
 -> falls nötig lokale OCR
 -> Bank/Format erkennen
 -> strukturierte Statement-Daten extrahieren
 -> normalisierte Buchungen + Saldo + Zeitraum
 -> gemeinsamer Import-Review
```

Die vorhandene Broker-PDF-Technik für Text/OCR/Confidence soll möglichst als gemeinsame PDF-Extraktionskomponente wiederverwendet werden; kein zweiter OCR-Stack.

Erkennen, soweit im Dokument vorhanden:

- Bank / Produkttyp
- Konto-/Kreditkennung
- Auszugszeitraum
- Buchungsdatum
- Valutadatum
- Betrag
- Währung
- Gegenpartei / Beschreibung
- Anfangs-/Schlussstand bzw. Kredit-/Restsaldo
- Saldo-Stichtag

Unsichere OCR-/Parserwerte müssen im Review sichtbar sein und dürfen nicht still übernommen werden.

---

# 12. Datenstand und Saldo-Stichtag sichtbar machen

Importierte Konten haben oft keinen Live-Stand. Die UI darf daher nicht suggerieren, dass der angezeigte Wert von heute ist.

Am Konto / Detail mindestens verfügbar:

- letzter Importzeitpunkt
- jüngste Buchung
- Saldo-Stichtag
- Quelle des Saldos (`provider`, `manual`, `import`)
- Quelle(n) der Buchungen

Beispiel:

```text
Saldo 4.312,81 €
Stand 31.08.2026 · Ikano Kontoauszug
Letzter Import heute
```

Nicht einfach nur `4.312,81 €`, wenn der Stand zwei Wochen alt ist.

---

# 13. Datenlücken erkennen

FullWorth soll pro Konto erkennen, wenn zwischen vorhandenen Daten auffällige Lücken bestehen.

Beispiel:

```text
Datenlücke erkannt
01.07.2026 – 14.07.2026
```

Mögliche Ursachen:

- Bankverbindung war zeitweise getrennt
- historische API-Reichweite begrenzt
- Dateiexport deckt nicht den gesamten Zeitraum ab

Die Lücke ist ein Hinweis, kein automatisches Erfinden fehlender Buchungen.

Import einer Datei, die die Lücke schließt, soll den Hinweis entsprechend aktualisieren/entfernen.

---

# 14. ImportBatch / Provenienz / Rollback vereinheitlichen

Der bestehende Import-Job-/Provenienz-/Rollback-Mechanismus soll zum gemeinsamen Modell werden.

Jeder Import erhält nachvollziehbar:

- Adapter / Quelle
- Dateiname / Fingerprint
- Importzeitpunkt
- erkannte Konten
- Zeitraum
- erzeugte Buchungen
- gematchte vorhandene Buchungen
- ergänzte Felder
- Saldo-Snapshots
- Warnungen / unsichere Zeilen
- Status

Rollback darf Nutzerarbeit an später bearbeiteten Buchungen nicht zerstören; vorhandene Schutzlogik weiterverwenden.

---

# 15. UI vereinheitlichen

Die heutige Trennung in unterschiedlich funktionierende Spezialseiten reduzieren.

Ziel:

```text
Import

[ Datei auswählen / hineinziehen ]

oder

Quelle
Finanzguru | Bankexport | Kontoauszug | Depot
```

Nach Upload bestimmt der erkannte Dokumenttyp den Adapter und die benötigten Review-Schritte.

Keine komplett andere Bedienlogik nur weil die Datei von Finanzguru statt C24 stammt.

Depotimporte können fachlich eigene Felder haben, sollen aber gemeinsame Shell-/Status-/Importhistorie-Komponenten verwenden.

Mobile und Desktop dieselbe fachliche Pipeline; keine zweite mobile Sonderimplementierung.

---

# Abgrenzung

Dieses Issue behandelt **Konten/Buchungen/Salden aus Finanzquellen**.

Beleg-/Receipt-Import aus Paperless/Kamera/Datei bleibt in #127/#128/#129 und soll nicht mit Bankkontoauszügen verwechselt werden.

Broker-/Depot-PDF kann seine Investment-Domänenlogik behalten; wiederverwendet werden sollen aber gemeinsame PDF-Extraktion, Importstatus und UX-Komponenten.

---

# Zusammenhang

- #112 Finanzguru-Import kann nicht mit PayPal-Konto verknüpft werden -> soll durch gemeinsames Konto-Matching mitgelöst werden
- #125 Konten-UI -> Datenstand/Importquelle sinnvoll in Kontodetails integrieren
- #126 Buchungen-UI -> importierte und live synchronisierte Buchungen müssen dort identisch dargestellt werden
- #127/#128/#129 -> nur Beleg-/Paperless-Import, fachlich getrennt halten

---

# Akzeptanzkriterien

- [x] Ein gemeinsamer Import-Kern verarbeitet normalisierte Konto-/Buchungsdaten unabhängig von der Quelle.
- [x] Finanzguru verwendet nicht mehr dauerhaft einen fachlich separaten sichtbaren Kontotyp für importierte Historie, wenn ein kanonisches Zielkonto feststeht.
- [x] Ein Import kann vorhandene Konten zuordnen oder neue Konten anlegen.
- [x] Bestätigte Konto-Zuordnungen werden für spätere Importe wiederverwendet.
- [x] PayPal und andere Konten ohne IBAN können normal als Ziel verwendet werden.
- [x] Multi-Currency-Konten werden nicht durch eine starre Ein-Währungs-Zuordnung unbrauchbar.
- [x] Finanzguru-Import läuft über dieselbe Review-/Commit-/History-Grundstruktur wie andere Buchungsimporte.
- [x] Finanzguru-Splits und Kategorien bleiben erhalten.
- [x] Relevante zusätzliche Finanzguru-Felder werden geprüft und strukturiert übernommen.
- [x] Bekannte CSV/XLSX-Quellen können per Adapter/Preset automatisch erkannt werden.
- [x] Generische CSV/XLSX bleiben per manueller Spaltenzuordnung nutzbar.
- [x] MT940/CAMT verwenden weiterhin den dokumentierten Saldo-Stichtag.
- [x] Bank-PDFs werden unterstützt; mindestens Ikano bekommt einen getesteten Adapter.
- [ ] PDF-Text und OCR verwenden eine gemeinsame Extraktionspipeline mit Confidence/Warnungen.
- [x] Mehrfach-/Überlappungsimporte erzeugen keine doppelten Buchungen.
- [x] Dieselbe reale Buchung aus unterschiedlichen Quellen wird gematcht und ihre Provenienz erweitert.
- [x] Nutzeränderungen an Buchungen werden durch spätere Importe nicht ungefragt überschrieben.
- [x] Saldoquelle und Saldo-Stichtag sind intern korrekt gespeichert und in Kontodetails sichtbar.
- [x] Importierte Konten zeigen transparent, wie aktuell ihre Daten sind.
- [x] Datenlücken können erkannt und im Konto sichtbar gemacht werden.
- [x] ImportBatch/Provenienz/Rollback funktionieren quellenübergreifend einheitlich.
- [x] Bestehende `finanzguru-import`-Daten können ohne Buchungsverlust und Doppelzählung migriert werden.
- [x] #112 wird durch den gemeinsamen Matching-Pfad behoben oder danach als obsolet/gelöst geschlossen.
- [x] Relevante Backend-, Import-, Migration-, UI- und E2E-Tests decken wiederholte/überlappende Imports, Konto-Matching, Multi-Currency, PDF/OCR und Rollback ab.

---

## Umsetzung / Agent-Control

Vor Implementierungsbeginn muss der ausführende Agent:

1. aktuellen `main`-Stand und offene Import-/Account-PRs erneut prüfen
2. `Juloc/agent-control` Issue #1 -> `currentLedgerIssue` auflösen
3. enge Claims für die tatsächlich geänderten Import-/Account-/UI-Pfade setzen
4. vorhandene `ImportParityModule`, `ImportMappingParityModule`, `ImportTransactionProvenance`, `BankStatementFile`, `StatementBalanceAnchor`, Finanzguru-Import/Reconciliation und Broker-PDF-Pipeline zuerst auf Wiederverwendung prüfen
5. keine zweite parallele Import-Engine hinzufügen
6. bestehende Finanzguru-Datenmigration ausdrücklich testen
7. UI-Harness Desktop + Mobile verwenden
8. gezielte Tests plus `dotnet build FullWorth.slnx -c Release --nologo` ausführen
9. Claim nach Abschluss sauber freigeben/abschließen

Kein Agent-Control-Claim allein für die Planung; Claim erst unmittelbar vor Codeänderungen.


---

### Issue #172: C24: PSD2/XS2A-Anbindung statt geratenem FinTS-Profil

- Quelle: https://github.com/Juloc/FullWorth/issues/172
- Status zum Stichtag: `open`
- Erstellt: 2026-09-17T11:21:28Z
- Zuletzt aktualisiert: 2026-09-22T06:44:10Z
- Labels: keine

#### Ursprünglicher Issue-Text (inhaltlich unverändert)

## Kontext

Herausgelöst aus #130 (FinTS/Banking-Protokollkorrektur für ING), das ausdrücklich verlangt, C24 **nicht** als geratenes FinTS-Profil zu implementieren, sondern als eigenen Connectivity-Task zu behandeln. Dieses Issue ist dieser Folge-Task.

## Problem / Ziel

Für C24 existiert kein offiziell dokumentierter FinTS-Endpunkt. Die dokumentierte Drittanbieter-Anbindung ist **PSD2/XS2A**. C24 darf deshalb nicht durch geratene/hartcodierte BLZ/BIC/URL-Werte als weiteres `KnownBanks`-FinTS-Profil vorgetäuscht werden.

Ziel dieses Tasks:

- C24 über den dokumentierten PSD2/XS2A-Zugang anbinden
- die vorhandene Banking-Provider-Abstraktion prüfen und wiederverwenden statt eine C24-Sonderimplementierung zu bauen
- OAuth-/SCA-/Consent-Lifecycle für PSD2 sauber modellieren
- Konten, Salden und Umsätze in dieselbe kanonische FullWorth-Ingest-Pipeline schreiben wie jede andere Bankverbindung
- keine C24-Sonderdatenbanktabellen, wenn das gemeinsame Banking-Modell ausreicht

## Nicht Ziel

- Kein FinTS-Fallback oder geratenes FinTS-Profil für C24 (das ist explizit ausgeschlossen, siehe #130)
- Keine Parallel-Implementierung neben der bestehenden Banking-Provider-Architektur

## Architektur

- vorhandene Banking-Provider-Abstraktion (siehe `src/FullWorth.Banking`) als Ausgangspunkt prüfen: reicht sie für einen PSD2/XS2A-Provider, oder fehlt eine Erweiterung?
- Consent-/Token-/SCA-Zustand braucht einen kanonischen Speicherpfad, analog zum bestehenden FinTS-Verbindungszustand — keine zweite parallele State-Quelle
- Root-Cause-Fix/-Erweiterung im kanonischen Banking-Pfad, keine Sonderpfade nur für C24

## Sicherheit / Diagnostik

Gilt sinngemäß wie in #130 beschrieben:

- strukturiertes Logging pro Operation (`provider: c24-xs2a`, `connectionId`, `operation`, Ergebniszustand, Dauer, Mengenangaben)
- niemals Access-/Refresh-Token, vollständige Authorization-Header, Client Secret, vollständige IBAN/Kontonummer oder komplette Kontoumsätze loggen
- zentrale Vorgabe aus `AGENTS.md`: keine Credentials/Secrets in Code, Docs, Issues, Kommentaren oder Logs

## Akzeptanzkriterien

- [ ] C24 ist nicht als FinTS-Profil implementiert (kein geratenes `KnownBanks`-Profil)
- [ ] C24-Zugang läuft über den dokumentierten PSD2/XS2A-Pfad
- [ ] bestehende Banking-Provider-Abstraktion wird wiederverwendet oder gezielt erweitert, nicht dupliziert
- [ ] OAuth/SCA/Consent-Lifecycle ist als kanonischer Zustand modelliert
- [ ] Konten/Salden/Umsätze landen in derselben kanonischen Ingest-Pipeline wie andere Banken
- [ ] keine C24-spezifischen Sonderdatenbanktabellen ohne nachgewiesene Notwendigkeit
- [ ] strukturiertes Logging vorhanden, ohne Secrets/Tokens/vollständige Kontodaten
- [ ] relevante Banking-Tests grün
- [ ] `dotnet build FullWorth.slnx -c Release --nologo` grün

## Umsetzung / Agent-Control

Wie bei #130: vor Implementierungsbeginn `main` aktualisieren, offene Issues/PRs auf Überschneidungen prüfen, `Juloc/agent-control` Issue #1 lesen, engen Claim für die betroffenen `src/FullWorth.Banking`-Pfade setzen, nach Abschluss freigeben.


---

### Issue #179: Rechner: Dienstrad/JobRad – echte Gesamtrechnung inkl. Rente und Lohnersatzleistungen

- Quelle: https://github.com/Juloc/FullWorth/issues/179
- Status zum Stichtag: `open`
- Erstellt: 2026-09-26T12:18:07Z
- Zuletzt aktualisiert: 2026-09-27T08:48:50Z
- Labels: keine

#### Ursprünglicher Issue-Text (inhaltlich unverändert)

## Ziel

FullWorth bekommt einen **anbieterneutralen Dienstrad-/JobRad-Rechner**, der nicht nur die beworbene Nettoersparnis zeigt, sondern die **echte finanzielle Gesamtwirkung** einer Gehaltsumwandlung.

Referenz des Users:
https://youtu.be/KcGOKiFfDqw

Der Rechner soll ausdrücklich auch die langfristigen und indirekten Effekte berücksichtigen, insbesondere **gesetzliche Rente**, Sozialversicherung und relevante Lohnersatzleistungen.

## Grundprinzip

Kein separater vereinfachter Netto-Rechner. Die Berechnung muss auf der bestehenden deutschen Compensation-Engine aufbauen:

- `src/FullWorth.Compensation.Core/GermanCompensationCalculator.cs`
- `src/FullWorth.Compensation.Core/TaxYearParameters.cs`

Steuer-/SV-Werte dürfen nicht als zweiter Satz hartcodierter Konstanten im JobRad-Rechner entstehen. Es gilt ein kanonischer, jahresabhängiger Berechnungspfad.

## Eingaben

### Person / Gehalt

Wenn vorhanden aus FullWorth Compensation vorbefüllen, aber editierbar:

- Berechnungsjahr
- Monats-/Jahresbrutto
- Steuerklasse
- Bundesland
- Kirchensteuer
- Kinder / Pflegeversicherung
- gesetzlich/privat krankenversichert und Zusatzbeitrag
- weitere für die bestehende Netto-Berechnung notwendige Parameter

### Fahrrad / Barkauf

- UVP / Bruttolistenpreis
- tatsächlicher Barkaufpreis bzw. Händlerrabatt
- optional Finanzierungskosten
- optionale Vergleichskosten für Versicherung, Wartung/Service und Verschleiß

Wichtig: Der Vergleich darf nicht nur gegen die UVP laufen. Entscheidend ist auch ein realistischer tatsächlicher Barkaufpreis.

### Leasing

- Anbieter: JobRad, BusinessBike, Lease a Bike, Eurorad, anderer / manuell
- Laufzeit, Standard 36 Monate
- Brutto-Umwandlungsrate bzw. Leasingfaktor
- Versicherung
- Servicepakete
- Arbeitgeber-Zuschuss
- vom Arbeitgeber übernommene Versicherung/Services
- Übernahmepreis am Ende: Prozent oder fixer Betrag
- mögliche pauschale Versteuerung der Übernahme: Anbieter/Arbeitgeber/Arbeitnehmer
- Variante:
  - Gehaltsumwandlung
  - echtes Gehaltsextra
- Fahrradtyp/Steuerregel so modellieren, dass normale Fahrräder/Pedelecs und abweichende Fahrzeugklassen nicht vermischt werden

Alle Anbieterwerte sind nur Presets und müssen manuell überschreibbar sein. Konditionen ändern sich.

## Berechnung

### 1. Echte monatliche Nettobelastung

Zwei vollständige Gehaltsberechnungen vergleichen:

1. normales Gehalt ohne Dienstrad
2. Gehalt mit Entgeltumwandlung + steuerpflichtigem geldwertem Vorteil

Ergebnis:

- Brutto-Umwandlungsrate
- Steuerersparnis
- SV-Ersparnis Arbeitnehmer
- geldwerter Vorteil
- tatsächlicher Nettoverlust pro Monat

Bei der 0,25-%-Regel die steuerlich korrekte Bemessung verwenden, nicht nur pauschal `UVP * 0,25 %`, sofern die Rundungsregel relevant ist.

### 2. Gesamtkosten über die Laufzeit

- Nettoverlust × Laufzeit
- einmalige Kosten
- Service/Versicherung
- Übernahmebetrag
- ggf. Steuer auf Übernahme
- Gesamtkosten bis das Rad tatsächlich Eigentum des Nutzers ist
- alternativ Gesamtkosten bei Rückgabe

Daneben Barkauf-Gesamtkosten mit denselben vergleichbaren Leistungen.

### 3. Gesetzliche Rente

Die Rentenauswirkung muss als echte Entgeltpunkt-Differenz berechnet werden.

Pro Zeitraum:

```
beitragspflichtiges Einkommen vorher
- beitragspflichtiges Einkommen nach Gehaltsumwandlung
= rentenwirksame Entgeltdifferenz
```

Dabei Beitragsbemessungsgrenze berücksichtigen. Liegt das Einkommen vor und nach Umwandlung oberhalb der RV-BBG, entsteht für diesen Monat **kein Rentenverlust**.

Dann:

```
verlorene Entgeltpunkte
= rentenwirksame Entgeltdifferenz / Durchschnittsentgelt des jeweiligen Jahres

monatliche Bruttorenten-Differenz
= verlorene Entgeltpunkte * aktueller Rentenwert
```

Ausgabe:

- verlorene Entgeltpunkte
- geschätzte monatliche Rentendifferenz in heutigen Werten
- jährliche Differenz
- kumulierte Differenz für frei wählbare Rentenbezugsdauer, z. B. 10/20/25 Jahre
- klarer Hinweis, dass zukünftige Rentenwerte nicht exakt prognostizierbar sind

2026 als Referenzwerte, aber **über Tax-/Sozialparameter versionieren, nicht hartcodieren**:
- vorläufiges Durchschnittsentgelt: 51.944 €
- RV-BBG: 8.450 €/Monat
- aktueller Rentenwert ab 01.07.2026: 42,52 €

### 4. Weitere Folgen der niedrigeren SV-/Bemessungsbasis

Als eigene, nachvollziehbare Delta-Szenarien:

- Krankengeld
- Arbeitslosengeld I
- Elterngeld, soweit die Gehaltsumwandlung in den relevanten Bemessungszeitraum fällt
- optional weitere lohnabhängige Leistungen, wenn fachlich sauber berechenbar

Nicht mit Scheingenauigkeit arbeiten. Wenn eine Leistung wegen fehlender Angaben nur geschätzt werden kann, muss UI und Ergebnis klar **Schätzung** sagen und die verwendete Annahme anzeigen.

### 5. Arbeitgeberseite

Optional anzeigen:

- eingesparte Arbeitgeber-SV durch die Gehaltsumwandlung
- davon an Arbeitnehmer weitergegebener Zuschuss
- Differenz

Das hilft zu erkennen, ob ein AG-Zuschuss lediglich einen Teil der Arbeitgeberersparnis zurückgibt.

### 6. Risiko-/Sonderszenarien

Keine erfundenen Wahrscheinlichkeiten, sondern manuell aktivierbare Szenarien:

- Jobwechsel vor Leasingende
- längere Krankheit / Krankengeld
- Elternzeit / unbezahlte Zeit
- vorzeitige Vertragsbeendigung
- kein Übernahmeangebot
- abweichender Übernahmepreis

Je nach eingegebenen Vertragsbedingungen die finanzielle Auswirkung zeigen.

## Ergebnis-UX

Keine Marketing-/Wizard-UI. Kompakte FullWorth-Seite mit direkter Berechnung.

Oben nur die wichtigsten Ergebnisse:

- **Netto pro Monat**
- **Gesamtkosten Leasing bis Eigentum**
- **vergleichbarer Barkauf**
- **Differenz Leasing vs. Kauf**
- **Rentenwirkung pro Monat**
- **verlorene Entgeltpunkte**

Darunter aufklappbare Details:

- Steuer & Sozialversicherung
- Rente
- Krankengeld / ALG I / Elterngeld
- Leasingende
- Arbeitgeberanteil
- Annahmen / Rechenweg

Der Nutzer muss jeden Wert nachvollziehen können. Jede Ergebniszahl braucht eine verständliche Herleitung statt Black-Box-„du sparst X %“.

## Integration in FullWorth

- sinnvoll unter **Gehalt & Benefits / Rechner**
- bestehende Compensation-Profile als Datenquelle verwenden
- optional Berechnung als Szenario speichern, damit verschiedene Räder/Konditionen vergleichbar sind
- keine zweite Steuer-/SV-Engine bauen
- Berechnungslogik UI-unabhängig im Compensation-Core halten; Web/WASM konsumieren dieselbe Logik
- Berechnung muss ohne Cloud funktionieren

## Akzeptanzkriterien

- [ ] Vergleich Leasing vs. realistischer Barkaufpreis vorhanden
- [ ] echte Netto-Differenz wird über die bestehende Compensation-Engine berechnet
- [ ] AG-Zuschuss, Versicherung, Service und Übernahme sind modellierbar
- [ ] Gehaltsumwandlung und Gehaltsextra werden getrennt behandelt
- [ ] geldwerter Vorteil wird nach gültiger Regel des gewählten Jahres berechnet
- [ ] Rentenwirkung wird über Entgeltpunkte, BBG, Durchschnittsentgelt und Rentenwert berechnet
- [ ] oberhalb der RV-BBG wird korrekt erkannt, dass ggf. kein Rentenverlust entsteht
- [ ] Krankengeld-/ALG-I-/Elterngeld-Auswirkungen werden, soweit berechenbar, transparent als Delta/Schätzung gezeigt
- [ ] Gesamtkosten bis Eigentumsübergang enthalten Übernahmepreis und relevante Nebenkosten
- [ ] Anbieterwerte sind Presets, keine unänderbaren Hardcodes
- [ ] jede wesentliche Zahl zeigt Rechenweg/Annahmen
- [ ] responsive und kompakt auf Mobile/Desktop
- [ ] keine Cloud-Abhängigkeit
- [ ] Unit-Tests für typische Grenzfälle: unter/über KV- und RV-BBG, AG-Zuschuss, Gehaltsextra, Übernahme, Steuerklassen
- [ ] `dotnet build FullWorth.slnx -c Release --nologo` grün

## Fachliche Referenzen

- JobRad zur Gehaltsumwandlung / 0,25-%-Regel:
  https://www.jobrad.org/wissen/gehaltsumwandlung
- JobRad zur Rentenwirkung:
  https://www.jobrad.org/wissen/rente
- Deutsche Rentenversicherung – Entgeltpunkte / Rentenberechnung:
  https://www.deutsche-rentenversicherung.de/DRV/DE/Rente/Allgemeine-Informationen/Wie-wird-meine-Rente-berechnet/Wie_wird_meine_Rente_berechnet
- § 70 SGB VI:
  https://www.gesetze-im-internet.de/sgb_6/__70.html

Die Referenzen dienen der fachlichen Validierung. Anbieter-Marketingwerte dürfen nicht ungeprüft als Rechenlogik übernommen werden.

---
