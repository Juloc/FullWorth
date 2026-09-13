const SVG_NS = 'http://www.w3.org/2000/svg';

function svgEl(name, className) {
  const el = document.createElementNS(SVG_NS, name);
  if (className) el.setAttribute('class', className);
  return el;
}

function viewBox(svg) {
  const vb = svg.viewBox?.baseVal;
  if (vb && vb.width > 0 && vb.height > 0) return { x: vb.x, y: vb.y, width: vb.width, height: vb.height };
  return { x: 0, y: 0, width: svg.clientWidth || 1, height: svg.clientHeight || 1 };
}

function localX(svg, event) {
  const matrix = svg.getScreenCTM();
  if (!matrix) return null;
  const point = svg.createSVGPoint();
  point.x = event.clientX;
  point.y = event.clientY;
  return point.matrixTransform(matrix.inverse()).x;
}

function nearestIndex(points, x) {
  let index = 0;
  let distance = Infinity;
  points.forEach((point, i) => {
    const next = Math.abs(Number(point.x) - x);
    if (next < distance) {
      distance = next;
      index = i;
    }
  });
  return index;
}

/**
 * Adds a finance-app style crosshair/scrubber to an SVG chart.
 *
 * points: [{ x, label, markers: [{ y, className }], data }]
 * callbacks receive the original point object. Mouse hover tracks immediately;
 * touch/pen press can be held and dragged. Vertical page scrolling remains enabled.
 */
export function bindChartScrubber(svg, points, {
  onChange = null,
  onReset = null,
  formatAria = null,
  initialIndex = null
} = {}) {
  if (!svg || !Array.isArray(points) || !points.length) return () => {};

  const clean = points
    .map(point => ({ ...point, x: Number(point.x), markers: Array.isArray(point.markers) ? point.markers : [] }))
    .filter(point => Number.isFinite(point.x));
  if (!clean.length) return () => {};

  svg.classList.add('fw-chart-scrubbable');
  if (!svg.hasAttribute('tabindex')) svg.setAttribute('tabindex', '0');

  const box = viewBox(svg);
  const overlay = svgEl('g', 'fw-chart-scrub-overlay');
  overlay.setAttribute('visibility', 'hidden');
  overlay.setAttribute('aria-hidden', 'true');

  const guide = svgEl('line', 'fw-chart-scrub-guide');
  guide.setAttribute('y1', String(box.y));
  guide.setAttribute('y2', String(box.y + box.height));
  overlay.appendChild(guide);

  const markers = svgEl('g', 'fw-chart-scrub-markers');
  overlay.appendChild(markers);

  const label = svgEl('text', 'fw-chart-scrub-label');
  label.setAttribute('y', String(box.y + 16));
  overlay.appendChild(label);
  svg.appendChild(overlay);

  let activeIndex = -1;
  let dragging = false;
  let pointerId = null;

  function render(index, notify = true) {
    index = Math.max(0, Math.min(clean.length - 1, index));
    const point = clean[index];
    activeIndex = index;

    guide.setAttribute('x1', String(point.x));
    guide.setAttribute('x2', String(point.x));

    markers.replaceChildren();
    point.markers.forEach(marker => {
      const y = Number(marker.y);
      if (!Number.isFinite(y)) return;
      const circle = svgEl('circle', `fw-chart-scrub-marker ${marker.className || ''}`.trim());
      circle.setAttribute('cx', String(point.x));
      circle.setAttribute('cy', String(y));
      circle.setAttribute('r', '5');
      markers.appendChild(circle);
    });

    label.textContent = String(point.label || '');
    const ratio = box.width ? (point.x - box.x) / box.width : .5;
    if (ratio < .18) {
      label.setAttribute('x', String(point.x + 9));
      label.setAttribute('text-anchor', 'start');
    } else if (ratio > .82) {
      label.setAttribute('x', String(point.x - 9));
      label.setAttribute('text-anchor', 'end');
    } else {
      label.setAttribute('x', String(point.x));
      label.setAttribute('text-anchor', 'middle');
    }

    overlay.setAttribute('visibility', 'visible');
    const aria = formatAria ? formatAria(point, index) : point.label;
    if (aria) svg.setAttribute('aria-valuetext', String(aria));
    if (notify) onChange?.(point, index);
  }

  function reset() {
    activeIndex = -1;
    overlay.setAttribute('visibility', 'hidden');
    svg.removeAttribute('aria-valuetext');
    onReset?.();
  }

  function moveFromEvent(event) {
    const x = localX(svg, event);
    if (!Number.isFinite(x)) return;
    render(nearestIndex(clean, x));
  }

  function onPointerDown(event) {
    if (event.pointerType === 'mouse' && event.button !== 0) return;
    dragging = true;
    pointerId = event.pointerId;
    try { svg.setPointerCapture(event.pointerId); } catch {}
    moveFromEvent(event);
  }

  function onPointerMove(event) {
    if (event.pointerType !== 'mouse' && !dragging) return;
    if (event.pointerType === 'mouse' || dragging) moveFromEvent(event);
  }

  function onPointerUp(event) {
    if (pointerId !== null && event.pointerId !== pointerId) return;
    dragging = false;
    try { svg.releasePointerCapture(event.pointerId); } catch {}
    pointerId = null;
    // Keep the selected point visible on touch/pen so the value can be read after lifting.
    if (event.pointerType === 'mouse') moveFromEvent(event);
  }

  function onPointerLeave(event) {
    if (!dragging && event.pointerType === 'mouse') reset();
  }

  function onPointerCancel() {
    dragging = false;
    pointerId = null;
    reset();
  }

  function onKeyDown(event) {
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End', 'Escape'].includes(event.key)) return;
    event.preventDefault();
    if (event.key === 'Escape') { reset(); return; }
    if (event.key === 'Home') { render(0); return; }
    if (event.key === 'End') { render(clean.length - 1); return; }
    const fallback = initialIndex == null ? clean.length - 1 : Math.max(0, Math.min(clean.length - 1, initialIndex));
    const current = activeIndex < 0 ? fallback : activeIndex;
    render(current + (event.key === 'ArrowLeft' ? -1 : 1));
  }

  svg.addEventListener('pointerdown', onPointerDown);
  svg.addEventListener('pointermove', onPointerMove);
  svg.addEventListener('pointerup', onPointerUp);
  svg.addEventListener('pointerleave', onPointerLeave);
  svg.addEventListener('pointercancel', onPointerCancel);
  svg.addEventListener('keydown', onKeyDown);
  svg.addEventListener('blur', reset);

  return () => {
    svg.removeEventListener('pointerdown', onPointerDown);
    svg.removeEventListener('pointermove', onPointerMove);
    svg.removeEventListener('pointerup', onPointerUp);
    svg.removeEventListener('pointerleave', onPointerLeave);
    svg.removeEventListener('pointercancel', onPointerCancel);
    svg.removeEventListener('keydown', onKeyDown);
    svg.removeEventListener('blur', reset);
    overlay.remove();
    svg.classList.remove('fw-chart-scrubbable');
  };
}
