import { createDialog } from '../../components/dialog.js';
import { ButtonRole, applyButtonRole, buttonClass } from '../../components/buttons.js';
import { toGray, sharpness, motion, findReceipt, outputSize, stripCorners, warp } from './receipt-vision.js';
import { splitIntoPages, stitchStrips, verticalShift } from './receipt-stitch.js';

// Die Belegkamera: Livebild, Randerkennung, automatische Aufnahme - und ein Modus fuer lange Bons.
//
// Das Ergebnis sind gerade ausgerichtete JPEG-Dateien in der Reihenfolge der Aufnahme. Was damit
// passiert, entscheidet das Scan-Set: dort werden sie geprueft, umsortiert und als EIN Beleg
// analysiert. Die Kamera kennt weder den Server noch den Kauf.
//
// Zwei Arten aufzunehmen:
//   Einzelfoto  Der Bon liegt ganz im Bild (oder man macht mehrere Fotos mit Ueberlappung): die Kamera
//               loest aus, sobald das Bild ruhig und scharf ist.
//   Langer Bon  Man startet und faehrt langsam am Bon entlang. Die Kamera vergleicht ihr Livebild mit dem
//               zuletzt aufgenommenen Stueck, loest aus, wenn etwa die Haelfte neu ist, und setzt am Ende
//               alle Stuecke zu einem Bild zusammen - aber nur an Naehten, die sich sicher finden
//               lassen (receipt-stitch.js). Wo nicht, bleiben getrennte Teile.
//
// Die Grenzwerte sind an kuenstlichen Bildern gemessen (ops/receipt-vision-bench), nicht an einem
// echten Handy. Deshalb gibt es den Ausloeser von Hand immer: wer die Automatik nicht mag oder die sie
// nicht trifft, verliert nichts.

const ANALYSIS_WIDTH = 320;
const FRAME_MS = 125;
const STEADY_FRAMES = 3;
const STEADY_SHIFT = 0.03;
const STEADY_MOTION = 5;
// Bewegung wird auf einem Viertel der Analysebreite gemessen: Sensorrauschen und das Zittern einer
// Hand, die ruhig zu halten meint, mitteln sich dort weg. Auf voller Analysebreite lag sie bei einem
// Beleg voller Schrift staendig ueber der Schwelle, und die Kamera wartete ewig auf ein "ruhiges" Bild.
const MOTION_DIVISOR = 4;
// Eine Messung, die ein Geraet nie erfuellt, darf nicht fuer immer warten. Je laenger ein erkannter Beleg
// auf die Aufnahme wartet, desto weiter geben die Grenzen nach - nach PATIENCE_FRAMES (etwa 2 s) auf das
// Mehrfache. Die Handaufnahme gibt es ohnehin immer; das hier ist fuer den, der sie nicht braucht.
const PATIENCE_FRAMES = 16;
const PATIENCE_LOOSENING = 2;
const REARM_SHIFT = 0.06;
const SHARP_SHARE = 0.85;
const SHARP_SHARE_PATIENT = 0.55;
const SHARP_FLOOR = 15;
const AUTO_PAUSE_MS = 1500;
const AUTO_GRACE_MS = 2500;
const MAX_SIDE = 3200;
const GHOST_SHARE = 0.18;

// Langer Bon
const LIVE_WIDTH = 240;
// Wie viel vom Bild neu sein soll, bevor das naechste Stueck aufgenommen wird. Gemessen wird die Fahrt der
// Kamera am Bon entlang von Livebild zu Livebild (receipt-stitch.js, verticalShift) - nicht die Naht zum
// letzten Stueck: die liegt gleich nach einer Aufnahme ausserhalb dessen, was eine Suche zuverlaessig
// findet, und auf echten Fotos mit Rauschen blieb sie unsicher, so dass nie ein zweites Stueck kam.
const CAPTURE_TRAVEL = 0.45;
const TOO_FAST_TRAVEL = 0.68;
// Von Livebild zu Livebild gemessen, in einem Fenster um den letzten Schritt (siehe verticalShift): so
// bleibt die Messung bei der richtigen Textzeile, auch wenn die Zeile darunter fast genauso aussieht.
const STEP_WINDOW = 0.12;
const STEP_MAX = 0.35;
const STEP_BACK = 0.08;
const SHIFT_SCORE = 0.6;
const LOST_TICKS = 8;
const END_TICKS = 8;
const MIN_STRIP_WIDTH = 480;
const MAX_STRIP_WIDTH = 1400;

/** Oeffnet die Kamera; liefert die aufgenommenen Fotos (leer, wenn abgebrochen wurde oder keine Kamera da ist). */
export function openReceiptCamera({ limit = 20, toast } = {}) {
  if (!navigator.mediaDevices?.getUserMedia) {
    toast?.(t('Die Kamera steht hier nicht zur Verfügung - der Browser erlaubt sie nur über eine sichere Verbindung. Wähle stattdessen ein Foto aus.',
      'The camera is not available here - browsers only allow it over a secure connection. Choose a photo instead.'));
    return Promise.resolve([]);
  }
  return new Promise(resolve => run({ limit, toast, resolve }));
}

function run({ limit, toast, resolve }) {
  const shots = [];          // Dateien in der Liste unten: Einzelfotos, spaeter die zusammengesetzten Stuecke
  const strips = [];         // Langer Bon: die Stuecke als Pixel, bis sie zusammengesetzt werden
  const analysis = document.createElement('canvas');
  const analysisContext = analysis.getContext('2d', { willReadFrequently: true });
  let stream = null;
  let timer = 0;
  let finished = false;
  let capturing = false;
  let armed = true;
  let lastShotAt = 0;
  let startedAt = 0;
  let previousGray = null;
  let previousCorners = null;
  let steady = 0;
  let waiting = 0;
  let peak = 0;
  let current = null;
  let torchOn = false;
  let mode = 'single';
  let recording = false;
  let stitched = false;
  let stripWidth = 0;
  let previousLive = null;  // das Livebild davor: von dort aus wird die Fahrt am Bon entlang gemessen
  let travel = 0;           // Zeilen, die die Kamera seit dem letzten Stueck gefahren ist
  let step = 0;             // der letzte Schritt von Bild zu Bild
  let lostTicks = 0;
  let endTicks = 0;
  let stripPeak = 0;

  const dialog = createDialog(`<div class="dialog-card receipt-camera">
    <div class="panel-head"><h2>${t('Beleg fotografieren', 'Photograph a receipt')}</h2></div>
    <div class="receipt-camera-modes" role="group" aria-label="${t('Aufnahmeart', 'Capture mode')}">
      <button type="button" data-mode="single" aria-pressed="true">${t('Einzelfoto', 'Single photo')}</button>
      <button type="button" data-mode="strip" aria-pressed="false">${t('Langer Bon', 'Long receipt')}</button>
    </div>
    <div class="receipt-camera-stage" data-stage>
      <video playsinline muted></video>
      <img class="receipt-camera-ghost" alt="" hidden>
    </div>
    <p class="receipt-camera-hint row-sub" data-hint aria-live="polite">${t('Kamera startet …', 'Starting camera …')}</p>
    <div class="receipt-camera-bar">
      <label class="receipt-camera-auto"><input type="checkbox" data-auto checked> ${t('Automatisch', 'Automatic')}</label>
      <button type="button" class="${buttonClass(ButtonRole.Primary, 'receipt-camera-shutter')}" data-shutter aria-label="${t('Foto aufnehmen', 'Take photo')}" disabled></button>
      <button type="button" class="${buttonClass(ButtonRole.Secondary)} receipt-camera-torch" data-torch>${t('Licht', 'Light')}</button>
    </div>
    <ol class="receipt-camera-shots" data-shots></ol>
    <div class="dialog-actions">
      <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${t('Abbrechen', 'Cancel')}</button>
      <button type="button" class="${buttonClass(ButtonRole.Primary)}" data-done disabled>${t('Fertig', 'Done')}</button>
    </div>
  </div>`, { className: 'receipt-camera-dialog', closeLabel: t('Schließen', 'Close') });

  const stage = dialog.querySelector('[data-stage]');
  const video = dialog.querySelector('video');
  // Der Rahmen ist Geometrie aus Messwerten, kein Icon - er entsteht im Code und nicht als Markup.
  const frame = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  frame.classList.add('receipt-camera-frame');
  frame.setAttribute('aria-hidden', 'true');
  const polygon = document.createElementNS('http://www.w3.org/2000/svg', 'polygon');
  frame.append(polygon);
  video.after(frame);
  const ghost = dialog.querySelector('.receipt-camera-ghost');
  const hint = dialog.querySelector('[data-hint]');
  const autoBox = dialog.querySelector('[data-auto]');
  const shutter = dialog.querySelector('[data-shutter]');
  const torch = dialog.querySelector('[data-torch]');
  const list = dialog.querySelector('[data-shots]');
  const done = dialog.querySelector('[data-done]');
  const modeButtons = [...dialog.querySelectorAll('[data-mode]')];
  setMode('single');

  // Geschlossen wird ueber "Fertig", "Abbrechen", das Kreuz oder Escape. Wer schon Fotos gemacht hat und
  // das Kreuz trifft, bekommt sie trotzdem: sie zu verwerfen kostet mehr als einen Fingertipp im
  // Scan-Set, sie dort wieder loszuwerden.
  dialog.addEventListener('close', () => stop(dialog.returnValue === 'discard' ? [] : shots.map(shot => shot.file)));
  dialog.querySelector('[data-cancel]').addEventListener('click', () => dialog.close('discard'));
  done.addEventListener('click', onDone);
  shutter.addEventListener('click', onShutter);
  torch.addEventListener('click', toggleTorch);
  modeButtons.forEach(button => button.addEventListener('click', () => setMode(button.dataset.mode)));
  dialog.showModal();
  void start();

  async function start() {
    try {
      stream = await navigator.mediaDevices.getUserMedia({
        audio: false,
        video: { facingMode: { ideal: 'environment' }, width: { ideal: 2160 }, height: { ideal: 3840 } }
      });
    } catch (error) {
      say(cameraError(error));
      return;
    }
    if (finished) { stream.getTracks().forEach(track => track.stop()); return; }
    video.srcObject = stream;
    try { await video.play(); } catch { say(t('Die Kamera lässt sich nicht starten.', 'The camera cannot be started.')); return; }
    if (!video.videoWidth) await new Promise(ready => video.addEventListener('loadedmetadata', ready, { once: true }));
    // Dieselbe Einpassung wie das Video (object-fit: contain), damit der Rahmen genau auf dem Beleg liegt.
    frame.setAttribute('viewBox', `0 0 ${video.videoWidth} ${video.videoHeight}`);
    const track = stream.getVideoTracks()[0];
    const capabilities = track.getCapabilities?.() ?? {};
    // Dauerautofokus ist eine Zugabe: lehnt das Geraet ihn ab, laeuft die Kamera mit dem, was sie hat.
    if (capabilities.focusMode?.includes('continuous'))
      await track.applyConstraints({ advanced: [{ focusMode: 'continuous' }] }).catch(() => {});
    if (capabilities.torch) torch.classList.add('is-available');
    shutter.disabled = false;
    // Eine kurze Schonfrist, bevor die Automatik das erste Foto macht: wer einen langen Bon aufnehmen will,
    // braucht die Zeit, die Aufnahmeart umzuschalten, und hat sonst schon ein Einzelfoto in der Liste.
    startedAt = Date.now();
    say(searchingHint());
    tick();
  }

  function setMode(next) {
    // Umschalten nur bei leerer Liste: Einzelfotos und Stuecke eines langen Bons gehoeren nicht in dieselbe Liste
    if (recording || shots.length) return;
    mode = next;
    modeButtons.forEach(button => {
      const active = button.dataset.mode === mode;
      button.setAttribute('aria-pressed', String(active));
      applyButtonRole(button, active ? ButtonRole.Primary : ButtonRole.Secondary);
    });
    autoBox.disabled = mode === 'strip';
    shutter.setAttribute('aria-label', mode === 'strip' ? t('Aufnahme starten', 'Start recording') : t('Foto aufnehmen', 'Take photo'));
    shutter.classList.toggle('is-record', mode === 'strip');
    say(searchingHint());
  }

  function searchingHint() {
    return mode === 'strip'
      ? t('Langer Bon: ans obere Ende halten, starten und langsam nach unten fahren.', 'Long receipt: hold at the top end, start, then move slowly down.')
      : t('Beleg ins Bild legen - am besten auf dunklem Untergrund.', 'Put the receipt in view - preferably on a dark surface.');
  }

  function tick() {
    if (finished) return;
    if (video.readyState >= 2 && !capturing) analyse();
    timer = setTimeout(tick, FRAME_MS);
  }

  function analyse() {
    const width = ANALYSIS_WIDTH;
    const height = Math.round(width * video.videoHeight / video.videoWidth);
    analysis.width = width;
    analysis.height = height;
    analysisContext.drawImage(video, 0, 0, width, height);
    const rgba = analysisContext.getImageData(0, 0, width, height).data;
    const full = toGray(rgba, width, height, width);
    const found = findReceipt(toGray(rgba, width, height, width / 2));

    const coarse = toGray(rgba, width, height, width / MOTION_DIVISOR).gray;
    const moved = motion(previousGray, coarse);
    previousGray = coarse;

    if (!found) {
      current = null; previousCorners = null; steady = 0; waiting = 0; peak = 0;
      if (!armed) armed = true;
      draw(null, false);
      if (!capturing) say(recording ? t('Bon verloren - zurück zum zuletzt aufgenommenen Stück.', 'Receipt lost - go back to the last captured part.') : searchingHint());
      return;
    }

    const shift = previousCorners ? maxShift(found.corners, previousCorners) : 1;
    previousCorners = found.corners;
    current = found;
    if (!armed && (moved >= STEADY_MOTION || shift > REARM_SHIFT)) { armed = true; waiting = 0; }

    // Die Geduld wird gezaehlt, solange eine Aufnahme moeglich waere (scharf gestellt, Beleg erkannt) - beim
    // langen Bon seit dem letzten Stueck.
    if (armed) waiting++;
    const patience = Math.min(waiting / PATIENCE_FRAMES, 1);
    const loosen = 1 + PATIENCE_LOOSENING * patience;
    steady = moved < STEADY_MOTION * loosen && shift < STEADY_SHIFT * loosen ? steady + 1 : 0;
    const sharp = sharpness(full.gray, full.width, full.height, boundingBox(found.corners, full.width, full.height));
    peak = steady === 0 ? sharp : Math.max(peak * 0.98, sharp);
    const sharpEnough = peak > SHARP_FLOOR && sharp >= peak * (SHARP_SHARE - (SHARP_SHARE - SHARP_SHARE_PATIENT) * patience);
    const ready = steady >= STEADY_FRAMES && sharpEnough;
    draw(found.corners, ready);

    if (mode === 'strip') {
      if (recording) stripStep(found, rgba, width, height, sharp, ready, patience);
      // Nach einer Pause oder dem Zusammensetzen steht dort, was geschehen ist - nicht wieder die Anleitung
      else if (!strips.length && !stitched) say(searchingHint());
      return;
    }

    if (shots.length >= limit) say(t(`Mehr als ${limit} Fotos gehen nicht in einen Beleg.`, `A receipt takes at most ${limit} photos.`));
    else if (!armed) say(t('Aufgenommen - für den nächsten Abschnitt das Handy weiterbewegen.', 'Captured - move the phone on for the next section.'));
    else if (steady < STEADY_FRAMES) say(t('Ruhig halten …', 'Hold still …'));
    else if (!sharpEnough) say(t('Noch unscharf - kurz warten.', 'Still blurry - wait a moment.'));
    else say(autoBox.checked ? t('Gleich …', 'Any moment …') : t('Bereit - auslösen.', 'Ready - take the photo.'));

    if (autoBox.checked && armed && ready && shots.length < limit && Date.now() - lastShotAt > AUTO_PAUSE_MS && Date.now() - startedAt > AUTO_GRACE_MS) void capture();
  }

  // ---------------------------------------------------------------- Einzelfoto

  async function capture() {
    if (capturing || finished || shots.length >= limit || !video.videoWidth) return;
    capturing = true;
    armed = false;
    waiting = 0;
    lastShotAt = Date.now();
    say(t('Wird aufbereitet …', 'Processing …'));
    try {
      const { source, context, width, height } = grabFrame();
      // Ohne erkannten Rand wird das ganze Bild genommen: lieber ein Foto mit Tisch als keins.
      const seen = current;
      let output = source;
      if (seen) {
        const corners = seen.corners.map(p => ({ x: p.x * width, y: p.y * height }));
        const size = outputSize(corners, MAX_SIDE);
        output = canvasOf(warp(context.getImageData(0, 0, width, height).data, width, height, corners, size.width, size.height), size.width, size.height);
      }
      await addShot(output);
      showGhost(seen?.continuesDown ? output : null);
      flash();
    } catch (error) {
      toast?.(error.message || t('Aufnahme fehlgeschlagen.', 'Capture failed.'));
    } finally {
      capturing = false;
    }
  }

  // ---------------------------------------------------------------- Langer Bon

  function onShutter() {
    if (mode === 'single') { void capture(); return; }
    if (stitched) return;
    recording = !recording;
    shutter.classList.toggle('is-recording', recording);
    shutter.setAttribute('aria-label', recording ? t('Aufnahme beenden', 'Stop recording') : t('Aufnahme starten', 'Start recording'));
    if (recording) { lostTicks = 0; endTicks = 0; stripPeak = 0; say(t('Läuft: ruhig halten, dann langsam nach unten fahren.', 'Recording: hold still, then move slowly down.')); }
    else say(strips.length ? t('Angehalten. „Fertig" setzt die Stücke zusammen.', 'Paused. "Done" joins the parts.') : searchingHint());
  }

  /** Ein Schritt der Aufnahme: das Livebild mit dem letzten Stueck vergleichen und entscheiden, ob ein neues faellig ist. */
  function stripStep(found, rgba, width, height, sharp, ready, patience) {
    if (strips.length >= limit) { say(t(`Mehr als ${limit} Stücke gehen nicht in einen Beleg.`, `A receipt takes at most ${limit} parts.`)); return; }
    const live = liveStrip(rgba, width, height, found);
    stripPeak = Math.max(stripPeak * 0.985, sharp);

    if (!strips.length) {
      say(ready ? t('Los: langsam nach unten fahren.', 'Go: move slowly down.') : t('Ruhig halten …', 'Hold still …'));
      // Passt der ganze Bon ins Bild, ist das erste Stueck zugleich das letzte
      if (ready) void captureStrip(found, live, !found.continuesDown);
      return;
    }
    const lastStrip = strips[strips.length - 1];
    if (!lastStrip.continuesDown) { say(t('Das Ende des Bons ist aufgenommen - „Fertig" tippen.', 'The end of the receipt is captured - tap "Done".')); return; }

    // Nach dem Entfernen eines Stuecks fehlt das Vergleichsbild: von hier aus neu messen
    const before = previousLive;
    if (!before) { previousLive = live; travel = 0; step = 0; say(t('Weiter nach unten fahren …', 'Keep moving down …')); return; }

    // Das untere Ende des Bons ist im Bild: das letzte Stueck aufnehmen, sobald es sich etwa eine Sekunde lang
    // hält (ein einzelnes Bild ohne erkannte Unterkante ist oft nur ein Ausreisser der Randerkennung).
    endTicks = found.continuesDown ? Math.max(0, endTicks - 2) : endTicks + 1;
    if (endTicks >= END_TICKS) { endTicks = 0; void captureStrip(found, live, true); return; }

    // Zwei Livebilder, deren Hoehe sich stark unterscheidet, zeigen nicht dieselbe Stelle: die Randerkennung hat
    // sich verschaetzt. Das ist keine Messung - und die Hoehe des Vorbilds bleibt der Massstab fuer die Fahrt.
    const h = before.height;
    if (Math.abs(live.height - h) > 0.15 * h) {
      if (++lostTicks > LOST_TICKS) { lostTicks = 0; void captureStrip(found, live); }
      return;
    }
    const measured = verticalShift(before, live, Math.max(-STEP_BACK * h, step - STEP_WINDOW * h), Math.min(STEP_MAX * h, step + STEP_WINDOW * h));
    if (measured.score < SHIFT_SCORE) {
      // Leere Stelle ohne Schrift, oder zu schnell gefahren: nach einer Weile trotzdem aufnehmen
      if (++lostTicks > LOST_TICKS) { lostTicks = 0; void captureStrip(found, live); }
      else say(t('Stelle nicht erkannt - weiter oder ein Stück zurück.', 'Spot not recognised - go on or back a little.'));
      return;
    }
    lostTicks = 0;
    previousLive = live;
    step = measured.shift;
    travel += step;
    const share = travel / h;

    if (share < CAPTURE_TRAVEL) { say(t('Weiter nach unten fahren …', 'Keep moving down …')); return; }
    if (share > TOO_FAST_TRAVEL) {
      say(t('Zu schnell - ein Stück zurück, sonst reißt die Naht.', 'Too fast - go back a little or the seam will break.'));
      void captureStrip(found, live);
      return;
    }
    // Jetzt ist fast die Haelfte neu: aufnehmen, sobald das Bild so scharf ist wie zuletzt - aber nicht
    // beliebig lange warten, sonst fehlt die Ueberlappung fuer die Naht
    if (sharp >= stripPeak * (0.75 - 0.3 * patience) || share > TOO_FAST_TRAVEL - 0.08) void captureStrip(found, live);
    else say(t('Jetzt - kurz ruhiger …', 'Now - steadier for a moment …'));
  }

  // `last`: das Ende des Bons ist erreicht. Das entscheidet nicht das einzelne Bild, in dem aufgenommen wird -
  // dort fehlt die Unterkante manchmal nur, weil die Randerkennung einmal danebenlag, und eine Aufnahme gilt
  // dann als letzte und beendet die ganze Fahrt -, sondern der Zaehler in stripStep.
  async function captureStrip(found, live, last = false) {
    if (capturing || finished) return;
    capturing = true;
    waiting = 0;
    try {
      const { context, width, height } = grabFrame();
      const quad = found.corners.map(p => ({ x: p.x * width, y: p.y * height }));
      const rect = stripCorners(quad, found.continuesUp, found.continuesDown);
      const [tl, tr, br, bl] = rect;
      const widthPx = (Math.hypot(tr.x - tl.x, tr.y - tl.y) + Math.hypot(br.x - bl.x, br.y - bl.y)) / 2;
      const heightPx = (Math.hypot(bl.x - tl.x, bl.y - tl.y) + Math.hypot(br.x - tr.x, br.y - tr.y)) / 2;
      // Alle Stuecke gleich breit: das erste legt die Breite fest, damit sich die anderen daran messen.
      if (!stripWidth) stripWidth = Math.round(Math.min(MAX_STRIP_WIDTH, Math.max(MIN_STRIP_WIDTH, widthPx)));
      const stripHeight = Math.max(8, Math.round(heightPx * stripWidth / widthPx));
      const pixels = warp(context.getImageData(0, 0, width, height).data, width, height, rect, stripWidth, stripHeight);
      strips.push({ rgba: pixels, width: stripWidth, height: stripHeight, continuesDown: !last });
      previousLive = live;
      travel = 0;
      step = 0;
      endTicks = 0;
      stripPeak = 0;
      const canvas = canvasOf(pixels, stripWidth, stripHeight);
      await addShot(canvas);
      showGhost(last ? null : canvas);
      flash();
      say(!last ? t('Aufgenommen - weiter nach unten.', 'Captured - keep going down.') : t('Das Ende des Bons ist aufgenommen - „Fertig" tippen.', 'The end of the receipt is captured - tap "Done".'));
    } catch (error) {
      toast?.(error.message || t('Aufnahme fehlgeschlagen.', 'Capture failed.'));
    } finally {
      capturing = false;
    }
  }

  async function onDone() {
    if (mode === 'strip' && !stitched && strips.length >= 2) { await joinStrips(); return; }
    dialog.close('done');
  }

  /** Die Stuecke zu einem Bild zusammensetzen und die Liste durch das Ergebnis ersetzen. */
  async function joinStrips() {
    recording = false;
    shutter.classList.remove('is-recording');
    done.disabled = true;
    say(t('Stücke werden zusammengesetzt …', 'Joining the parts …'));
    await new Promise(resolveFrame => requestAnimationFrame(() => setTimeout(resolveFrame, 0)));
    try {
      const result = stitchStrips(strips);
      shots.splice(0).forEach(shot => URL.revokeObjectURL(shot.url));
      // Jedes zusammenhaengende Stueck wird in lesbar grosse Seiten ohne Ueberlappung geteilt (siehe splitIntoPages)
      let pageCount = 0;
      for (const segment of result.segments) {
        for (const page of splitIntoPages(segment)) { await addShot(canvasOf(page.rgba, page.width, page.height), 'lang'); pageCount++; }
      }
      stitched = true;
      ghost.hidden = true;
      const broken = result.seams.filter(seam => !seam.ok).length;
      const checked = t(`${strips.length} Stücke, ${result.seams.length - broken} von ${result.seams.length} Nähten sicher`, `${strips.length} parts, ${result.seams.length - broken} of ${result.seams.length} seams certain`);
      say(broken === 0
        ? t(`Zusammengesetzt (${checked}) und in ${pageCount} Seite(n) ohne Überlappung geteilt.`, `Joined (${checked}) and split into ${pageCount} page(s) without overlap.`)
        : t(`Teilweise zusammengesetzt (${checked}): an ${broken} Stelle(n) bleibt der Bon getrennt. ${pageCount} Seite(n).`, `Partly joined (${checked}): the receipt stays separate at ${broken} place(s). ${pageCount} page(s).`));
    } catch (error) {
      toast?.(error.message || t('Zusammensetzen fehlgeschlagen.', 'Joining failed.'));
    } finally {
      renderShots();
    }
  }

  // ---------------------------------------------------------------- gemeinsam

  function grabFrame() {
    const width = video.videoWidth, height = video.videoHeight;
    const source = document.createElement('canvas');
    source.width = width;
    source.height = height;
    const context = source.getContext('2d', { willReadFrequently: true });
    context.drawImage(video, 0, 0, width, height);
    return { source, context, width, height };
  }

  /** Die Aufnahme als JPEG-Datei in die Liste. */
  async function addShot(canvas, kind = 'foto') {
    const blob = await new Promise(resolveBlob => canvas.toBlob(resolveBlob, 'image/jpeg', 0.9));
    if (!blob) throw new Error(t('Das Foto konnte nicht erstellt werden.', 'The photo could not be created.'));
    const file = new File([blob], `beleg-${kind}-${Date.now()}-${shots.length + 1}.jpg`, { type: 'image/jpeg' });
    shots.push({ file, url: URL.createObjectURL(file) });
    renderShots();
  }

  function flash() {
    stage.classList.remove('is-flash');
    void stage.offsetWidth;
    stage.classList.add('is-flash');
  }

  /** Das untere Stueck des letzten Fotos, blass ueber dem Livebild: der Beleg geht weiter, die naechste Aufnahme soll dort anschliessen. */
  function showGhost(canvas) {
    if (!canvas) { ghost.hidden = true; return; }
    const piece = document.createElement('canvas');
    const rows = Math.max(1, Math.round(canvas.height * GHOST_SHARE));
    piece.width = Math.min(canvas.width, 480);
    piece.height = Math.max(1, Math.round(rows * piece.width / canvas.width));
    piece.getContext('2d').drawImage(canvas, 0, canvas.height - rows, canvas.width, rows, 0, 0, piece.width, piece.height);
    ghost.src = piece.toDataURL('image/jpeg', 0.7);
    ghost.hidden = false;
  }

  function renderShots() {
    list.replaceChildren(...shots.map((shot, index) => {
      const item = document.createElement('li');
      item.className = 'receipt-camera-shot';
      item.innerHTML = `<img alt="${t('Foto', 'Photo')} ${index + 1}"><button type="button" class="${buttonClass(ButtonRole.Secondary)}" aria-label="${t('Foto entfernen', 'Remove photo')}">×</button>`;
      item.querySelector('img').src = shot.url;
      item.querySelector('button').addEventListener('click', () => {
        URL.revokeObjectURL(shot.url);
        shots.splice(shots.indexOf(shot), 1);
        // Ein Stueck, das fehlt, laesst sich nicht mehr zusammensetzen: dann zaehlt, was uebrig ist
        if (mode === 'strip' && !stitched) { strips.splice(index, 1); previousLive = null; travel = 0; }
        if (!shots.length) { ghost.hidden = true; stitched = false; stripWidth = 0; strips.length = 0; }
        renderShots();
      });
      return item;
    }));
    const joinable = mode === 'strip' && !stitched && strips.length >= 2;
    modeButtons.forEach(button => { button.disabled = recording || shots.length > 0; });
    done.disabled = shots.length === 0;
    done.textContent = joinable ? t(`Zusammensetzen (${strips.length})`, `Join (${strips.length})`)
      : shots.length ? t(`Fertig (${shots.length})`, `Done (${shots.length})`) : t('Fertig', 'Done');
  }

  async function toggleTorch() {
    try {
      await stream.getVideoTracks()[0].applyConstraints({ advanced: [{ torch: !torchOn }] });
      torchOn = !torchOn;
    } catch {
      toast?.(t('Das Licht lässt sich an diesem Gerät nicht schalten.', 'The light cannot be switched on this device.'));
    }
  }

  function draw(corners, ready) {
    if (!corners) { polygon.setAttribute('points', ''); return; }
    polygon.setAttribute('points', corners.map(p => `${(p.x * video.videoWidth).toFixed(1)},${(p.y * video.videoHeight).toFixed(1)}`).join(' '));
    frame.classList.toggle('is-ready', ready);
  }

  function say(text) { if (hint.textContent !== text) hint.textContent = text; }

  function stop(files) {
    if (finished) return;
    finished = true;
    clearTimeout(timer);
    stream?.getTracks().forEach(track => track.stop());
    // Die Dateien gehoeren jetzt dem Aufrufer; nur die Vorschau-Adressen sind noch meine.
    shots.forEach(shot => URL.revokeObjectURL(shot.url));
    resolve(files);
  }
}

/** Das Livebild als kleines, gerades Stueck des Bons - nur zum Vergleichen mit dem letzten Stueck. */
function liveStrip(rgba, width, height, found) {
  const quad = found.corners.map(p => ({ x: p.x * width, y: p.y * height }));
  const rect = stripCorners(quad, found.continuesUp, found.continuesDown);
  const [tl, tr, br, bl] = rect;
  const widthPx = (Math.hypot(tr.x - tl.x, tr.y - tl.y) + Math.hypot(br.x - bl.x, br.y - bl.y)) / 2;
  const heightPx = (Math.hypot(bl.x - tl.x, bl.y - tl.y) + Math.hypot(br.x - tr.x, br.y - tr.y)) / 2;
  const liveHeight = Math.max(8, Math.round(heightPx * LIVE_WIDTH / widthPx));
  return { rgba: warp(rgba, width, height, rect, LIVE_WIDTH, liveHeight), width: LIVE_WIDTH, height: liveHeight };
}

function canvasOf(rgba, width, height) {
  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  canvas.getContext('2d').putImageData(new ImageData(rgba, width, height), 0, 0);
  return canvas;
}

function cameraError(error) {
  if (error?.name === 'NotAllowedError' || error?.name === 'SecurityError')
    return t('Der Kamerazugriff ist gesperrt. Erlaube ihn in den Browser-Einstellungen oder wähle ein Foto aus.', 'Camera access is blocked. Allow it in the browser settings or choose a photo.');
  if (error?.name === 'NotFoundError' || error?.name === 'OverconstrainedError')
    return t('Auf diesem Gerät wurde keine Kamera gefunden.', 'No camera was found on this device.');
  if (error?.name === 'NotReadableError')
    return t('Die Kamera wird gerade von einer anderen App benutzt.', 'The camera is in use by another app.');
  return t('Die Kamera lässt sich nicht starten.', 'The camera cannot be started.');
}

function maxShift(a, b) {
  let max = 0;
  for (let i = 0; i < 4; i++) max = Math.max(max, Math.hypot(a[i].x - b[i].x, a[i].y - b[i].y));
  return max;
}

function boundingBox(corners, width, height) {
  const xs = corners.map(p => p.x * width), ys = corners.map(p => p.y * height);
  return { x0: Math.floor(Math.min(...xs)), x1: Math.ceil(Math.max(...xs)), y0: Math.floor(Math.min(...ys)), y1: Math.ceil(Math.max(...ys)) };
}

function t(de, en) { return document.documentElement.lang?.toLowerCase().startsWith('en') ? en : de; }
