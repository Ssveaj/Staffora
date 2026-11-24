const attached = new WeakMap();

export function focusRow(list, rowKey) {
  if (!list || !list.isConnected) return;
  const active = document.activeElement;
  if (active && active !== document.body && active.isConnected && !list.contains(active)) return;

  const row = Array.from(list.querySelectorAll('[data-row]')).find((candidate) => candidate.getAttribute('data-row') === rowKey);
  const target = row?.querySelector('.list-row__open')
    ?? list.querySelector('.list-table')
    ?? list.querySelector('.list-cards')
    ?? list.querySelector('.list-view__summary');
  target?.focus();
}

export function focusControl(list, id) {
  if (!list || !list.isConnected) return;
  const control = list.querySelector(`[id="${CSS.escape(id)}"]`);
  control?.focus();
}

export function openPopover(list, popId) {
  if (!list || !list.isConnected) return;
  const listbox = list.querySelector(`[id="${CSS.escape(popId)}"]`);
  if (!listbox) return;
  const option = listbox.querySelector('[role="option"][aria-selected="true"]') ?? listbox.querySelector('[role="option"]');
  option?.focus();
}

export function attachPopovers(list, dotnet) {
  if (!list || attached.has(list)) return;

  const openHost = () => list.querySelector('[data-pop-host] > .list-pop')?.parentElement ?? null;
  const close = () => dotnet.invokeMethodAsync('ClosePopover').catch(() => {  });

  const onKeyDown = (event) => {
    const host = event.target instanceof Element ? event.target.closest('[data-pop-host]') : null;
    const listbox = host?.querySelector(':scope > .list-pop');
    if (!host || !listbox) return;

    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      host.querySelector('[aria-haspopup]')?.focus();
      close();
      return;
    }

    if (!listbox.contains(event.target)) return;
    const options = Array.from(listbox.querySelectorAll('[role="option"]'));
    const index = options.indexOf(event.target);
    let next = -1;
    if (event.key === 'ArrowDown') next = Math.min(index + 1, options.length - 1);
    else if (event.key === 'ArrowUp') next = Math.max(index - 1, 0);
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = options.length - 1;
    if (next < 0) return;
    event.preventDefault();
    options[next]?.focus();
  };

  const onFocusOut = (event) => {
    const host = openHost();
    if (!host || !host.contains(event.target)) return;
    const to = event.relatedTarget;
    if (to instanceof Node && !host.contains(to)) close();
  };

  const onPointerDown = (event) => {
    const host = openHost();
    if (host && !(event.target instanceof Node && host.contains(event.target))) close();
  };

  list.addEventListener('keydown', onKeyDown);
  list.addEventListener('focusout', onFocusOut);
  document.addEventListener('pointerdown', onPointerDown, true);
  attached.set(list, { onKeyDown, onFocusOut, onPointerDown });
}

export function detachPopovers(list) {
  const handlers = list ? attached.get(list) : undefined;
  if (!handlers) return;
  list.removeEventListener('keydown', handlers.onKeyDown);
  list.removeEventListener('focusout', handlers.onFocusOut);
  document.removeEventListener('pointerdown', handlers.onPointerDown, true);
  attached.delete(list);
}
