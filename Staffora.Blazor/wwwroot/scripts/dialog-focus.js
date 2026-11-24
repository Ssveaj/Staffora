const openers = new Map();

const headingSelector = '.state-entity-dialog-header__title, .form-dialog-header__title';

export function trackDialog(root, closeOnEsc) {
  if (!root || !root.isConnected || !root.id) return;
  const dialog = dialogOf(root);
  armEscape(root.closest('.rz-dialog-content'), closeOnEsc !== false);
  const active = document.activeElement;
  if (!openers.has(root.id) && active && active !== document.body && !dialog.contains(active)) {
    openers.set(root.id, active);
  }

  if (!dialog.contains(document.activeElement)) {
    const content = root.closest('.rz-dialog-content') || root;
    const first = content.querySelector('[data-dialog-autofocus]') || focusableIn(content)[0];
    if (first) first.focus({ preventScroll: true });
  }
}

export function releaseDialog(rootId) {
  const opener = openers.get(rootId);
  openers.delete(rootId);
  const root = document.getElementById(rootId);
  const closing = root ? dialogOf(root) : null;

  const active = document.activeElement;
  const lost = !active || active === document.body || !active.isConnected || (closing !== null && closing.contains(active));
  if (!lost || !opener) return;

  if (opener.isConnected && !isInert(opener) && !(closing && closing.contains(opener))) {
    opener.focus({ preventScroll: true });
    if (document.activeElement === opener) return;

    if (opener.offsetParent === null) return;
  }

  const heading = headingOfTopDialog(closing);
  if (heading) heading.focus({ preventScroll: true });
}

function armEscape(content, closeOnEsc) {
  if (!content || content._escapeArmed) return;
  let written = content.options;
  Object.defineProperty(content, 'options', {
    configurable: true,
    get: () => ({ ...(written || {}), closeDialogOnEsc: closeOnEsc }),
    set: (value) => { written = value; },
  });
  content._escapeArmed = true;
}

function dialogOf(root) {
  return root.closest('[role="dialog"], [role="alertdialog"]') || root;
}

function isInert(element) {
  return !!element.closest('[inert]') || element.disabled === true;
}

function headingOfTopDialog(closing) {
  const dialogs = Array.from(document.querySelectorAll('[role="dialog"], [role="alertdialog"]'))
    .filter((dialog) => dialog !== closing && !(closing && closing.contains(dialog)));
  for (let index = dialogs.length - 1; index >= 0; index--) {
    const heading = dialogs[index].querySelector(headingSelector);
    if (heading && !isInert(heading)) return heading;
  }
  return null;
}

function focusableIn(element) {
  return Array.from(element.querySelectorAll('a[href], button, input, textarea, select, summary, [contenteditable], [tabindex]'))
    .filter((candidate) => !candidate.hasAttribute('disabled') && candidate.offsetParent !== null && candidate.tabIndex > -1 && !isInert(candidate));
}
