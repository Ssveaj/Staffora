const openers = new Map();

const dialogsByTitle = new Map();

export function nameDialog(title) {
  if (!title || !title.isConnected || !title.id) return;
  armEscape(title);
  const dialog = title.closest('[role="dialog"]');
  if (dialog) {
    dialog.setAttribute('aria-labelledby', title.id);
    dialog.removeAttribute('aria-label');
    takeOpenerOver(dialog, title.id);
    dialogsByTitle.set(title.id, dialog);
  }

  const active = document.activeElement;
  if (!openers.has(title.id) && active && active !== document.body && (!dialog || !dialog.contains(active))) {
    openers.set(title.id, active);
  }

  if ((!dialog || !active || !dialog.contains(active)) && isTopDialog(dialog)) {
    title.focus({ preventScroll: true });
  }
}

function armEscape(title) {
  const content = title.closest('.rz-dialog-content');
  if (!content || content._escapeArmed) return;
  let written = content.options;
  Object.defineProperty(content, 'options', {
    configurable: true,
    get: () => ({ ...(written || {}), closeDialogOnEsc: true }),
    set: (value) => { written = value; },
  });
  content._escapeArmed = true;
}

function isTopDialog(dialog) {
  if (!dialog) return true;
  const dialogs = document.querySelectorAll('[role="dialog"], [role="alertdialog"]');
  for (let index = dialogs.length - 1; index >= 0; index--) {
    const candidate = dialogs[index];
    if (candidate === dialog || dialog.contains(candidate)) return true;
    if (!candidate.contains(dialog)) return false;
  }
  return true;
}

function takeOpenerOver(dialog, titleId) {
  for (const [earlier, element] of dialogsByTitle) {
    if (earlier === titleId || element !== dialog) continue;
    if (openers.has(earlier) && !openers.has(titleId)) openers.set(titleId, openers.get(earlier));
    openers.delete(earlier);
    dialogsByTitle.delete(earlier);
  }
}

export function handOffDialog(titleId) {
  const dialog = dialogsByTitle.get(titleId);
  if (!dialog) return;
  const next = dialog.getAttribute('aria-labelledby');
  if (next && next !== titleId && document.getElementById(next)) {
    takeOpenerOver(dialog, next);
  }
}

export function releaseDialog(titleId) {
  const title = document.getElementById(titleId);
  const closing = dialogsByTitle.get(titleId) || (title ? title.closest('[role="dialog"]') : null);
  dialogsByTitle.delete(titleId);
  const opener = openers.get(titleId);
  openers.delete(titleId);
  if (!opener || !opener.isConnected || (closing && closing.contains(opener))) return;

  const active = document.activeElement;
  const focusIsLost = !active || active === document.body || (closing && closing.isConnected && closing.contains(active));
  if (focusIsLost) {
    opener.focus({ preventScroll: true });
  }
}

export function restoreFocus(footer, key) {
  if (!footer || !footer.isConnected) return;
  const dialog = footer.closest('[role="dialog"]') || footer.closest('.state-entity-dialog');
  if (!dialog) return;

  const active = document.activeElement;
  if (active && active !== document.body && active !== dialog && dialog.contains(active)) return;

  const button = Array.from(footer.querySelectorAll('button[data-transition], button[data-dialog-action]'))
    .find(element => element.dataset.transition === key || element.dataset.dialogAction === key);
  const target = button || dialog.querySelector('.state-entity-dialog-header__title');
  if (target) target.focus({ preventScroll: true });
}

const minimumCardHeight = 160;

const cardViewportGap = 16;

export function holdEscape(root, pill, component) {
  if (!root || root._releaseEscape) return;
  const isOpen = () => root.isConnected && root.dataset.open === 'true';

  const onKey = (event) => {
    const isEscape = event.key === 'Escape' || event.key === 'Esc';
    if (!isEscape || !isOpen() || !focusBelongsToCard(root)) return;
    event.stopPropagation();
    event.preventDefault();
    if (pill) pill.focus({ preventScroll: true });
    component.invokeMethodAsync('CloseCardByEscape').catch(() => { });
  };

  const onOutside = (event) => {
    if (!isOpen() || !(event.target instanceof Node) || root.contains(event.target)) return;
    component.invokeMethodAsync('CloseCardFromOutside').catch(() => { });
  };

  const card = root.querySelector('.state-pill__card');
  const fit = () => {
    if (isOpen() && card) fitCard(card);
  };
  const observer = new MutationObserver(fit);
  observer.observe(root, { attributes: true, attributeFilter: ['data-open'] });

  document.addEventListener('keydown', onKey, true);
  document.addEventListener('pointerdown', onOutside, true);
  document.addEventListener('focusin', onOutside, true);
  window.addEventListener('resize', fit);
  fit();
  root._releaseEscape = () => {
    document.removeEventListener('keydown', onKey, true);
    document.removeEventListener('pointerdown', onOutside, true);
    document.removeEventListener('focusin', onOutside, true);
    window.removeEventListener('resize', fit);
    observer.disconnect();
  };
}

function focusBelongsToCard(root) {
  const active = document.activeElement;
  if (active && root.contains(active)) return true;

  const own = root.closest('.rz-dialog, [role="dialog"]');
  if (!own) return false;
  const dialogs = document.querySelectorAll('.rz-dialog');
  const top = dialogs.length ? dialogs[dialogs.length - 1] : own;
  const inOwn = !active || active === document.body || own.contains(active);
  return top === own && inOwn;
}

export function fitCard(card) {
  if (!card || !card.isConnected) return;
  const top = card.getBoundingClientRect().top;
  const room = Math.floor(window.innerHeight - top - cardViewportGap);
  card.style.maxHeight = `${Math.max(minimumCardHeight, room)}px`;
}

export function returnFocus(pill) {
  if (!pill || !pill.isConnected) return;
  const own = pill.closest('.rz-dialog, [role="dialog"]');
  const active = document.activeElement;
  const lost = !active || active === document.body || (own ? !own.contains(active) : true);
  if (lost) pill.focus({ preventScroll: true });
}

export function returnFocusWithin(host) {
  const button = host?.querySelector('button');
  if (button) returnFocus(button);
}

export function releaseEscape(root) {
  if (root && root._releaseEscape) {
    root._releaseEscape();
    delete root._releaseEscape;
  }
}

const tabKeys = new Set(['ArrowLeft', 'ArrowRight', 'Home', 'End']);

export function attachTabs(tablist) {
  if (!tablist || tablist.dataset.tabKeys === 'attached') return;
  tablist.dataset.tabKeys = 'attached';
  tablist.addEventListener('keydown', (event) => {
    if (!tabKeys.has(event.key) || !(event.target instanceof HTMLElement) || event.target.getAttribute('role') !== 'tab') return;
    const tabs = Array.from(tablist.querySelectorAll('[role="tab"]'));
    const index = tabs.indexOf(event.target);
    if (index < 0 || tabs.length === 0) return;
    event.preventDefault();
    const next = event.key === 'Home' ? 0
      : event.key === 'End' ? tabs.length - 1
      : event.key === 'ArrowRight' ? (index + 1) % tabs.length
      : (index - 1 + tabs.length) % tabs.length;
    tabs[next].focus();
  });
}
