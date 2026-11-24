let listening = null;

const typingSelector = 'input, textarea, select, [contenteditable]:not([contenteditable="false"])';

function isTyping(target) {
  return target instanceof Element && (target.isContentEditable || target.closest(typingSelector) !== null);
}

const dialogOpen = () => document.querySelector('.rz-dialog-wrapper, [role="dialog"], [role="alertdialog"]') !== null;

export function listen(shortcut, owner) {
  if (listening) document.removeEventListener('keydown', listening.handler);
  const handler = (event) => {
    if (event.key !== '/' || event.ctrlKey || event.metaKey || event.altKey || event.isComposing || event.defaultPrevented) return;
    if (isTyping(event.target) || dialogOpen()) return;
    event.preventDefault();
    shortcut.invokeMethodAsync('OpenFromShortcutAsync');
  };
  document.addEventListener('keydown', handler);
  listening = { owner, handler };
}

export function stop(owner) {
  if (!listening || listening.owner !== owner) return;
  document.removeEventListener('keydown', listening.handler);
  listening = null;
}

export function holdCaret(input) {
  if (!input || input._searchCaretHeld) return;
  input.addEventListener('keydown', (event) => {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp' || event.key === 'Enter') event.preventDefault();
  });
  input._searchCaretHeld = true;
}

export function reveal(optionId) {
  document.getElementById(optionId)?.scrollIntoView({ block: 'nearest' });
}
