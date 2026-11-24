const keys = new Set(['ArrowLeft', 'ArrowRight', 'Home', 'End']);

export function attach(tablist) {
  if (!tablist || tablist.dataset.tabBar === 'attached') return;
  tablist.dataset.tabBar = 'attached';
  tablist.addEventListener('keydown', (event) => {
    if (!keys.has(event.key) || !(event.target instanceof HTMLElement) || event.target.getAttribute('role') !== 'tab') return;
    const tabs = Array.from(tablist.querySelectorAll('[role="tab"]'));
    const index = tabs.indexOf(event.target);
    if (index < 0) return;
    event.preventDefault();
    const next = event.key === 'Home' ? 0
      : event.key === 'End' ? tabs.length - 1
      : event.key === 'ArrowRight' ? (index + 1) % tabs.length
      : (index - 1 + tabs.length) % tabs.length;
    tabs[next].focus();
    tabs[next].click();
  });
}
