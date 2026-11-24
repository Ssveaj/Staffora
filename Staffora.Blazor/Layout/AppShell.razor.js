const phone = window.matchMedia('(max-width: 899px)');

const sidebarCookie = 'staffora.sidebar';
const collapsedValue = 'collapsed';

function setCollapsed(collapsed) {
  const root = document.documentElement;
  if (collapsed) root.setAttribute('data-sidebar', collapsedValue);
  else root.removeAttribute('data-sidebar');
  const secure = location.protocol === 'https:' ? '; secure' : '';
  document.cookie = collapsed
    ? `${sidebarCookie}=${collapsedValue}; path=/; max-age=31536000; samesite=lax${secure}`
    : `${sidebarCookie}=; path=/; max-age=0; samesite=lax${secure}`;
}

const shells = () => document.querySelectorAll('[data-app-shell]');
const menuButtonOf = (shell) => shell.querySelector('[data-sidebar-open]');
const sidebarOf = (shell) => shell.querySelector('[data-app-sidebar]');

const behindOf = (shell) => [shell.querySelector('[data-skip-link]'), shell.querySelector('[data-app-content]')].filter(Boolean);

function setDrawer(shell, open, returnFocus) {
  if (shell.hasAttribute('data-drawer-open') === open) return;
  shell.toggleAttribute('data-drawer-open', open);
  menuButtonOf(shell)?.setAttribute('aria-expanded', String(open));
  for (const element of behindOf(shell)) element.inert = open;
  if (open) {
    const sidebar = sidebarOf(shell);
    (sidebar?.querySelector('[data-nav-link].active') ?? sidebar?.querySelector('[data-sidebar-close]'))?.focus();
  } else if (returnFocus) {
    menuButtonOf(shell)?.focus();
  }
}

const openDrawer = () => [...shells()].find((shell) => shell.hasAttribute('data-drawer-open'));

function setHelp(button, open) {
  button.setAttribute('aria-expanded', String(open));
  const popover = document.getElementById(button.getAttribute('aria-controls'));
  if (popover) popover.hidden = !open;
}

const openHelps = () => [...document.querySelectorAll('[data-page-help-toggle][aria-expanded="true"]')];

const openAccountMenu = () => document.querySelector('details.account-menu__profile[open]');

document.addEventListener('click', (event) => {
  const target = event.target instanceof Element ? event.target : null;
  if (!target) return;

  const skip = target.closest('[data-skip-link]');
  if (skip) {
    const main = document.getElementById('main-content');
    if (main) {
      event.preventDefault();
      main.focus();
      main.scrollIntoView({ block: 'start' });
    }
    return;
  }

  const shell = target.closest('[data-app-shell]');
  if (target.closest('[data-sidebar-collapse]')) {
    setCollapsed(true);
    shell?.querySelector('[data-sidebar-expand]')?.focus();
    return;
  }
  if (target.closest('[data-sidebar-expand]')) {
    setCollapsed(false);
    shell?.querySelector('[data-sidebar-collapse]')?.focus();
    return;
  }

  if (shell && target.closest('[data-sidebar-open]')) {
    setDrawer(shell, true);
    return;
  }
  if (shell && target.closest('[data-sidebar-close]')) {
    setDrawer(shell, false, true);
    return;
  }
  if (shell && target.closest('[data-sidebar-scrim]')) {
    event.preventDefault();
    setDrawer(shell, false, true);
    return;
  }
  if (shell && target.closest('a') && sidebarOf(shell)?.contains(target)) {
    setDrawer(shell, false, false);
  }

  const help = target.closest('[data-page-help-toggle]');
  for (const button of openHelps()) {
    if (button !== help && !button.closest('[data-page-help]')?.contains(target)) setHelp(button, false);
  }
  if (help) setHelp(help, help.getAttribute('aria-expanded') !== 'true');

  const menu = openAccountMenu();
  if (menu && !menu.contains(target)) menu.open = false;
}, true);

document.addEventListener('keydown', (event) => {
  if (event.key !== 'Escape') return;
  const target = event.target instanceof Element ? event.target : null;

  const drawer = openDrawer();
  if (drawer) {
    setDrawer(drawer, false, true);
    return;
  }

  const menu = openAccountMenu();
  if (menu) {
    menu.open = false;
    menu.querySelector('summary')?.focus();
    return;
  }

  for (const button of openHelps()) {
    setHelp(button, false);
    if (button.closest('[data-page-help]')?.contains(target)) button.focus();
  }
});

phone.addEventListener('change', () => {
  for (const shell of shells()) setDrawer(shell, false, false);
});
