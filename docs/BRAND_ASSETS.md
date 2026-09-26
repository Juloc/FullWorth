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
