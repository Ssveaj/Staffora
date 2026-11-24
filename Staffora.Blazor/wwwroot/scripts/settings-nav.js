const moveKeys = new Set(['ArrowDown', 'ArrowUp', 'Home', 'End']);

const visibleLinks = (nav) => [...nav.querySelectorAll('[data-settings-panels] a')].filter((link) => link.offsetParent !== null);

export function attach(nav) {
  if (!nav || nav.dataset.settingsNavKeys === 'attached') return;
  nav.dataset.settingsNavKeys = 'attached';
  nav.addEventListener('keydown', (event) => {
    if (!moveKeys.has(event.key) || event.altKey || event.ctrlKey || event.metaKey) return;
    const links = visibleLinks(nav);
    const index = links.indexOf(document.activeElement);
    if (index < 0) return;
    event.preventDefault();
    const next = event.key === 'Home' ? 0
      : event.key === 'End' ? links.length - 1
      : (index + (event.key === 'ArrowDown' ? 1 : links.length - 1)) % links.length;
    links[next].focus();
  });
}
