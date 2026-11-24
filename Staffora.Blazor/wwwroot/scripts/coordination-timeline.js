export function focusItem(root, key, row) {
  if (!root || !root.isConnected) return;
  const active = document.activeElement;
  if (active && active !== document.body && active.isConnected && !root.contains(active)) return;

  const byData = (name, value) => Array.from(root.querySelectorAll(`[${name}]`)).find((candidate) => candidate.getAttribute(name) === value);
  const target = byData('data-board-focus', key)
    ?? byData('data-board-row', row)?.querySelector('.timeline-name__title, .timeline-name__open')
    ?? root.querySelector('.timeline-toolbar__day');
  target?.focus();
}

export function scrollToHour(root, hour, place, smoothly) {
  const scroller = root?.querySelector('.timeline-scroll');
  const hours = root?.querySelector('.timeline-hours');
  if (!scroller || !hours) return;
  const hourWidth = hours.getBoundingClientRect().width / 24;
  const name = root.querySelector('.timeline-corner')?.getBoundingClientRect().width ?? 0;
  const visible = scroller.clientWidth - name;
  const at = hour * hourWidth;
  const left = place === 'third' ? at - (visible / 3) : at - (hourWidth / 4);
  const instant = !smoothly || matchMedia('(prefers-reduced-motion: reduce)').matches;
  scroller.scrollTo({ left: Math.max(0, left), behavior: instant ? 'auto' : 'smooth' });
}

const watched = new WeakMap();

export function watchMore(root) {
  const scroller = root?.querySelector('.timeline-scroll');
  const hint = root?.querySelector('[data-more-staff]');
  if (!scroller || !hint) return;
  let count = watched.get(scroller);
  if (!count) {
    count = () => {
      const text = hint.querySelector('[data-more-staff-text]');
      const bottom = scroller.scrollTop + scroller.clientHeight;
      const rows = scroller.querySelectorAll('[data-board-row]:not([data-board-row="staff"])');
      let below = 0;
      for (const row of rows) {
        if (row.offsetTop >= bottom - 4) below++;
      }

      const words = below === 1 ? '1 more staff member below' : `${below} more staff below`;
      hint.hidden = below === 0;
      hint.setAttribute('aria-label', words);
      if (text) text.textContent = words;
    };
    watched.set(scroller, count);
    scroller.addEventListener('scroll', count, { passive: true });
    new ResizeObserver(count).observe(scroller);
    hint.addEventListener('click', () => scroller.scrollBy({ top: scroller.clientHeight * 0.8, behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' }));
  }

  count();
}

const panning = new WeakSet();

export function dragToScroll(root) {
  const scroller = root?.querySelector('.timeline-scroll');
  if (!scroller || panning.has(scroller)) return;
  panning.add(scroller);
  let drag = null;
  scroller.addEventListener('contextmenu', (event) => {
    if (drag?.moved || event.button === 2) event.preventDefault();
  });
  scroller.addEventListener('pointerdown', (event) => {
    if (event.button !== 2) return;
    drag = { x: event.clientX, left: scroller.scrollLeft, id: event.pointerId, moved: false };
    scroller.setPointerCapture(event.pointerId);
    scroller.classList.add('timeline-scroll--panning');
    event.preventDefault();
  });
  scroller.addEventListener('pointermove', (event) => {
    if (!drag || event.pointerId !== drag.id) return;
    const dx = event.clientX - drag.x;
    if (Math.abs(dx) > 2) drag.moved = true;
    scroller.scrollLeft = drag.left - dx;
  });
  const end = (event) => {
    if (!drag || event.pointerId !== drag.id) return;
    scroller.releasePointerCapture(event.pointerId);
    scroller.classList.remove('timeline-scroll--panning');
    drag = null;
  };
  scroller.addEventListener('pointerup', end);
  scroller.addEventListener('pointercancel', end);
}
