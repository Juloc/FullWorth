# Markenlogos

FullWorth zeigt neben Buchungen, Händlern und Verträgen das Logo der Marke. Woher es kommt, ist in
drei Stufen geregelt — von der teuersten Entscheidung (deine) bis zur billigsten (mitgeliefert).

| Rang | Quelle | Tabelle | Wer schreibt |
| --- | --- | --- | --- |
| ≥ 1 | Eigene Markenpakete | `CustomBrandAssets` | du, über *Einstellungen → Intelligence* |
| 0 | **Mitgeliefert** | `OfficialBrandAssets` | `BundledBrandCatalogInstaller` bei jedem Start |
| −1 | Selbst recherchiert | `ResearchedBrandAssets` | `BrandLogoResearchService` |

Die Bytes liegen inhaltsadressiert in `BrandAssetBlobs` und werden zwischen allen drei Stufen
geteilt: dieselbe Datei gibt es dort nur einmal.

## Der mitgelieferte Katalog

100 Marken, rund 140 kB, eingebettet in `FullWorth.Backend.dll`. Kein Netzzugriff beim Start, keine
Anmeldung, kein Vertrauensanker — eine frisch installierte Instanz zeigt ihre Logos sofort.

- **Quelle:** ausschließlich [Simple Icons](https://github.com/simple-icons/simple-icons), gepinnt
  auf **16.29.0**.
- **Lizenz:** CC0-1.0 für die Dateien. Die Marken selbst bleiben Eigentum ihrer Inhaber; jedes Asset
  trägt `SourceName`, `SourceUrl` (auf die exakte Fassung) und `LicenseNote` mit sich, und die
  Oberfläche bekommt sie über `GET /api/intelligence/brand-catalog` mitgeliefert.
- **Unverändert** bis auf eine deterministische Politur: `width`/`height` entfallen und
  `preserveAspectRatio="xMidYMid meet"` kommt dazu, damit das Logo den Kreis füllt statt darin zu
  schrumpfen. Nie umgefärbt, nie beschnitten, nie zusammengesetzt.

Eine Quelle und eine Lizenz für alles ist Absicht. Gemischte Herkunft skaliert man nicht auf hundert
Marken hoch: bei einem Einspruch müsste man sonst je Logo nachsehen, woher es kam.

## Neu erzeugen oder erweitern

```bash
node ops/brand-icons/build.mjs                  # gegen die gepinnte Fassung auffrischen
node ops/brand-icons/build.mjs --check          # nur melden, was neu wäre oder entfallen ist
node ops/brand-icons/build.mjs --version 17.0.0 # Pin anheben
```

Die Markenliste entsteht aus den Händleraliasen in
`src/FullWorth.Backend/Modules/Categories/GermanyCategorizationCatalog.cs` — wer dort einen Händler
ergänzt, bekommt sein Logo ohne zweite Liste. Dazu kommen:

- `ops/brand-icons/extra-brands.json` — Gegenparteien, die der Kategorisierungskatalog bewusst
  **nicht** als Händler führt: PayPal, Klarna, Banken, Zahlungsnetze. Ein Logo ist keine
  Kategorisierung.
- `ops/brand-icons/slug-overrides.json` — wo die Ableitung am Katalog vorbeigeht
  (`hm` → `handm`, `aldi` → `aldinord`, `telekom` → `deutschetelekom`). Jeder Eintrag ist ein
  belegter Treffer. `deny` nimmt eine Marke ganz heraus; das ist der Weg, auf den Einspruch eines
  Rechteinhabers zu reagieren — eine Zeile, ein Lauf, ein Commit.
- `ops/brand-icons/misses.json` — die rund 360 Kurznamen, die es bei Simple Icons nachweislich nicht
  gibt. Ohne diese Liste wären über die Hälfte aller Anfragen je Lauf garantierte Fehlschläge.

**Der Generator prüft keine SVGs.** Das wäre eine zweite Prüfung in JavaScript neben
`BrandAssetVerifier` — und zwei Prüfungen laufen auseinander. Er schreibt Dateien; geprüft wird jede
einzelne offline in `BundledBrandCatalogTests` mit demselben Validator, durch den auch ein fremdes
Markenpaket geht.

## Was Simple Icons nicht hat

Der Katalog ist entwicklerlastig. Von den deutschen Handels-, Versorger- und Vergleichsmarken fehlen
unter anderem OBI, Vattenfall, E.ON, Douglas, Decathlon, FlixBus, Eurowings, CHECK24, comdirect,
Postbank und Trade Republic — nachgeprüft, nicht vermutet. Diese Händler werden weiterhin
kategorisiert; sie bekommen nur das Monogramm-Ersatzbild aus `ux-kit.js` statt eines Logos, oder ein
Logo aus einem eigenen Paket.

## Die Leiter: wie ein unbekannter Händler zu seinem Logo kommt

Zeigt keine der drei Stufen oben ein Logo, arbeitet die Instanz eine Leiter ab. Jede Sprosse ist
teurer als die darüber, und keine wird erreicht, solange eine darüber antwortet.

| # | Sprosse | Netz | KI | Standard | Schreibt |
| --- | --- | --- | --- | --- | --- |
| 1 | Bekannter Alias in einer der drei Tabellen | – | – | an | – |
| 2 | Kurznamen ableiten und gegen den mitgelieferten Katalog halten | – | – | an | Alias @ 0,90 |
| 3 | Beim Simple-Icons-Spiegel nachschlagen | ja | – | **an** | Bild + Alias @ 0,80 |
| 4 | KI nennt eine Domain, `BrandLogoFetcher` holt das SVG | ja | ja | Freigabe nötig | Bild + Alias @ 0,55 |

Die Sprossen 1–3 laufen im geplanten Auftrag **vor** dem KI-Tor. Das ist kein Detail: ohne KI
verschiebt sich der Auftrag alle sechs Stunden mit `ai_disabled`, und alles dahinter liefe nie.
Eine Installation ganz ohne KI bekommt so trotzdem Logos.

### Was Sprosse 3 kostet

Nicht Geld — den Namen. Der abgeleitete Kurzname verlässt die Maschine, weil er in der Adresse
steht: `https://cdn.jsdelivr.net/npm/simple-icons@<Fassung>/icons/<kurzname>.svg`. Bei einer Kette
ist das folgenlos (`REWE SAGT DANKE` → `rewe`), bei einem Einzelunternehmer nicht
(`MUELLER FLIESENLEGER` → `muellerfliesenleger`). Ein Logo lässt sich nicht holen, ohne zu sagen,
wessen Logo.

Deshalb:

- **Abschaltbar** unter *Einstellungen → Intelligence → Logos nachschlagen*, mit genau diesem Text
  daneben. Standard ist **an** — die Entscheidung ist, dass eine Instanz zuerst selbst sucht.
- **Nur der Kurzname**, und nur wenn er die Form `[a-z0-9]{2,60}` erfüllt. Was nicht hineinpasst,
  wird nicht abgeschickt.
- **Höchstens drei Kurznamen je Händler**, ein Vermerk je Händler, 25 Abrufe je Lauf.
- **429 oder 5xx beendet den Lauf sofort.** Ein fremder Spiegel, der gerade nicht mag, wird nicht
  fünfundzwanzigmal gefragt.
- **Die Fassung kommt aus `catalog.json`**, nicht aus einer zweiten Konstante — sonst holt die
  Instanz Bilder aus einer anderen Menge als die, die sie schon mitbringt.
- **Geprüft wird trotzdem.** `BrandAssetVerifier` härtet jedes SVG von dort wie jedes andere; der
  Spiegel wird nicht geglaubt, weil er der Spiegel ist.

`BrandLogoResearchAttempts.Rung` trennt die Vermerke der Sprossen 3 und 4. Ohne diese Spalte hätte
ein „kennt der Spiegel nicht" dreißig Tage lang auch den einen KI-Versuch verbraucht.
