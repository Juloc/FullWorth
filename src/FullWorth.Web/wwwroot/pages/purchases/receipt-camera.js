import { createDialog } from '../../components/dialog.js';
import { ButtonRole, buttonClass } from '../../components/buttons.js';
import { toGray, sharpness, motion, findReceipt, outputSize, warp } from './receipt-vision.js';

// Die Belegkamera: Livebild, Randerkennung, automatische Aufnahme.
//
// Das Ergebnis sind gerade ausgerichtete JPEG-Dateien in der Reihenfolge der Aufnahme. Was damit
// passiert, entscheidet das Scan-Set: dort werden sie geprueft, umsortiert und als EIN Beleg
// analysiert. Die Kamera kennt weder den Server noch den Kauf.
//
// Die Grenzwerte unten sind an Testbildern gemessen, nicht an einem echten Handy. Deshalb gibt es den
// Ausloeser von Hand immer: wer die Automatik nicht mag oder die sie nicht trifft, verliert nichts.

const ANALYSIS_WIDTH = 320;
const FRAME_MS = 125;
const STEADY_FRAMES = 5;
const STEADY_SHIFT = 0.015;
const STEADY_MOTION = 4;
const REARM_SHIFT = 0.06;
const SHARP_SHARE = 0.85;
const SHARP_FLOOR = 15;
const AUTO_PAUSE_MS = 1500;
const MAX_SIDE = 3200;
const GHOST_SHARE = 0.18;

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
  const shots = [];
  const analysis = document.createElement('canvas');
  const analysisContext = analysis.getContext('2d', { willReadFrequently: true });
  let stream = null;
  let timer = 0;
  let finished = false;
  let capturing = false;
  let armed = true;
  let lastShotAt = 0;
  let previousGray = null;
  let previousCorners = null;
  let steady = 0;
  let peak = 0;
  let current = null;
  let torchOn = false;

  const dialog = createDialog(`<div class="dialog-card receipt-camera">
    <div class="panel-head"><h2>${t('Beleg fotografieren', 'Photograph a receipt')}</h2></div>
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

  // Geschlossen wird ueber "Fertig", "Abbrechen", das Kreuz oder Escape. Wer schon Fotos gemacht hat und
  // das Kreuz trifft, bekommt sie trotzdem: sie zu verwerfen kostet mehr als einen Fingertipp im
  // Scan-Set, sie dort wieder loszuwerden.
  dialog.addEventListener('close', () => stop(dialog.returnValue === 'discard' ? [] : shots.map(shot => shot.file)));
  dialog.querySelector('[data-cancel]').addEventListener('click', () => dialog.close('discard'));
  done.addEventListener('click', () => dialog.close('done'));
  shutter.addEventListener('click', () => capture());
  torch.addEventListener('click', toggleTorch);
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
    say(t('Beleg ins Bild legen - am besten auf dunklem Untergrund.', 'Put the receipt in view - preferably on a dark surface.'));
    tick();
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

    const still = motion(previousGray, full.gray) < STEADY_MOTION;
    previousGray = full.gray;

    if (!found) {
      current = null; previousCorners = null; steady = 0; peak = 0;
      if (!armed) armed = true;
      draw(null, false);
      if (!capturing) say(t('Beleg ins Bild legen - am besten auf dunklem Untergrund.', 'Put the receipt in view - preferably on a dark surface.'));
      return;
    }

    const shift = previousCorners ? maxShift(found.corners, previousCorners) : 1;
    previousCorners = found.corners;
    current = found;
    if (!armed && (!still || shift > REARM_SHIFT)) armed = true;

    steady = still && shift < STEADY_SHIFT ? steady + 1 : 0;
    const sharp = sharpness(full.gray, full.width, full.height, boundingBox(found.corners, full.width, full.height));
    peak = steady === 0 ? sharp : Math.max(peak * 0.98, sharp);
    const sharpEnough = peak > SHARP_FLOOR && sharp >= peak * SHARP_SHARE;
    const ready = steady >= STEADY_FRAMES && sharpEnough;
    draw(found.corners, ready);

    if (shots.length >= limit) say(t(`Mehr als ${limit} Fotos gehen nicht in einen Beleg.`, `A receipt takes at most ${limit} photos.`));
    else if (!armed) say(t('Aufgenommen - für den nächsten Abschnitt das Handy weiterbewegen.', 'Captured - move the phone on for the next section.'));
    else if (steady < STEADY_FRAMES) say(t('Ruhig halten …', 'Hold still …'));
    else if (!sharpEnough) say(t('Noch unscharf - kurz warten.', 'Still blurry - wait a moment.'));
    else say(autoBox.checked ? t('Gleich …', 'Any moment …') : t('Bereit - auslösen.', 'Ready - take the photo.'));

    if (autoBox.checked && armed && ready && shots.length < limit && Date.now() - lastShotAt > AUTO_PAUSE_MS) void capture();
  }

  async function capture() {
    if (capturing || finished || shots.length >= limit || !video.videoWidth) return;
    capturing = true;
    armed = false;
    lastShotAt = Date.now();
    say(t('Wird aufbereitet …', 'Processing …'));
    try {
      const width = video.videoWidth, height = video.videoHeight;
      const source = document.createElement('canvas');
      source.width = width;
      source.height = height;
      const context = source.getContext('2d', { willReadFrequently: true });
      context.drawImage(video, 0, 0, width, height);

      // Ohne erkannten Rand wird das ganze Bild genommen: lieber ein Foto mit Tisch als keins.
      const seen = current;
      let output = source;
      if (seen) {
        const corners = seen.corners.map(p => ({ x: p.x * width, y: p.y * height }));
        const size = outputSize(corners, MAX_SIDE);
        const pixels = warp(context.getImageData(0, 0, width, height).data, width, height, corners, size.width, size.height);
        output = document.createElement('canvas');
        output.width = size.width;
        output.height = size.height;
        output.getContext('2d').putImageData(new ImageData(pixels, size.width, size.height), 0, 0);
      }

      const blob = await new Promise(resolveBlob => output.toBlob(resolveBlob, 'image/jpeg', 0.9));
      if (!blob) throw new Error(t('Das Foto konnte nicht erstellt werden.', 'The photo could not be created.'));
      const file = new File([blob], `beleg-${Date.now()}-${shots.length + 1}.jpg`, { type: 'image/jpeg' });
      shots.push({ file, url: URL.createObjectURL(file) });
      showGhost(seen?.continuesDown ? output : null);
      stage.classList.remove('is-flash');
      void stage.offsetWidth;
      stage.classList.add('is-flash');
      renderShots();
    } catch (error) {
      toast?.(error.message || t('Aufnahme fehlgeschlagen.', 'Capture failed.'));
    } finally {
      capturing = false;
    }
  }

  /** Das untere Stueck des letzten Fotos, blass ueber dem Livebild: der Beleg geht weiter, die naechste Aufnahme soll dort anschliessen. */
  function showGhost(canvas) {
    if (!canvas) { ghost.hidden = true; return; }
    const strip = document.createElement('canvas');
    const height = Math.max(1, Math.round(canvas.height * GHOST_SHARE));
    strip.width = Math.min(canvas.width, 480);
    strip.height = Math.max(1, Math.round(height * strip.width / canvas.width));
    strip.getContext('2d').drawImage(canvas, 0, canvas.height - height, canvas.width, height, 0, 0, strip.width, strip.height);
    ghost.src = strip.toDataURL('image/jpeg', 0.7);
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
        if (!shots.length) ghost.hidden = true;
        renderShots();
      });
      return item;
    }));
    done.disabled = shots.length === 0;
    done.textContent = shots.length ? t(`Fertig (${shots.length})`, `Done (${shots.length})`) : t('Fertig', 'Done');
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
