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
| 4 | KI nennt Kurzname (→ Spiegel) oder Domain (→ `BrandLogoFetcher`) | ja | ja | Freigabe nötig | Bild + Alias @ 0,55 |

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

### Sprosse 4: die KI, und was sie zurückschreibt

Eine KI-Antwort gilt erst als fertig, wenn sie eine deterministische Zeile erzeugt hat. Zwei
Formen, in dieser Reihenfolge:

1. **Die KI nennt einen Simple-Icons-Kurznamen** (Form 4a). Der wird — genau wie in Sprosse 3 —
   beim gepinnten Spiegel geholt und mit `BrandAssetVerifier` geprüft. Das ist der sicherere erste
   Griff: ein Kurzname ist ein deterministischer Pfad zu einem bereits vertrauten Ort, eine Domain
   ein Rateversuch über mehrere mögliche Bildpfade.
2. **Nur wenn das nichts ergibt** (kein Kurzname genannt, die Form nicht erfüllt, der Spiegel führt
   ihn nicht, oder das Ergebnis kein sicheres SVG ist), fällt es auf die genannte **Domain** zurück
   (Form 4b) — `BrandLogoFetcher` rät dort die üblichen Logo-Pfade, wie schon vor dieser Sprosse.

Beide Formen schreiben mit `Source = "ai"` und `Confidence = 0,55` — derselbe Deckel wie beim
Renten-Strukturierer: ein Modell darf ergänzen, nie überstimmen. Die Zeile trägt zusätzlich
`RunId`, den `AiRun`-Protokolleintrag, der sie vorgeschlagen hat — ohne diesen Beleg sieht man
einer KI-Zeile nicht an, welches Modell, welcher Anbieter und welcher Tag sie geschrieben hat, und
findet eine falsche Charge nur durch Zufall.

### Der kürzeste eindeutige Namensteil statt des vollen Namens

Alle drei schreibenden Sprossen (Ableitung, Spiegel, KI) legen nicht den vollen Händlernamen ab,
sondern den kürzesten Namensteil, der noch eindeutig auf die Marke zeigt — `BrandSlugDerivation
.UnambiguousStem`. Aus „EDEKA MARKT 4711 BERLIN" wird die Zeile „EDEKA": die Oberfläche matcht
ohnehin an Wortgrenzen (`ux-kit.js`), also deckt eine Zeile jede Filiale einer Kette ab statt nur
die eine, die zuerst gebucht wurde — die nächste Filiale ist danach bereits „bekannt", ohne
erneuten Abruf.

Trifft der kürzeste Namensteil bereits eine **andere** Marke, gibt es keinen Stamm — die Zeile
trägt dann den vollen Namen (`AliasKind = "exact"`, statt `"stem"`). Ein zweiter Rateversuch mit
einem längeren Namensteil findet nicht statt: das wäre genau die stille Fehlzuordnung, die eine
Katalogpflege nie wieder findet.

### Korrektur: "Logo ist falsch"

Ein Mensch darf jede selbst recherchierte Zuordnung ablehnen - im `⋯`-Menü auf `/merchants`,
ohne Admin-Freigabe: wer ein falsches Logo sieht, darf es abstellen. `POST
/api/intelligence/brands/reject` (`{ name }`) findet die passende `ResearchedBrandAlias`-Zeile
über dieselbe Wortgrenzen-Regel wie die Oberfläche und setzt `Status = "rejected"`. Ein
mitgeliefertes oder eigenes Paket-Logo lässt sich hier nicht ablehnen - dafür ist die
Pack-Verwaltung da, nicht dieser Schalter.

Zwei Sperren halten die Ablehnung dauerhaft, nicht nur bis zum nächsten Lauf:

- `IsAlreadyCoveredAsync` zählt eine **abgelehnte** Zeile weiterhin als "schon entschieden" - der
  Statusfilter gilt nur für die Auslieferung, nicht für die Frage, ob noch gesucht werden muss.
  Eine andere Filiale derselben Kette löst deshalb keinen erneuten Abruf aus.
- Beide Sprossen-Vermerke (Spiegel und KI) bekommen zusätzlich `user_rejected` - der einzige
  Vermerk, der **nie** nach dreißig Tagen verfällt. Zweite Sicherung, falls die
  Wortgrenzen-Prüfung diesen einen Namen künftig einmal nicht mehr träfe.
