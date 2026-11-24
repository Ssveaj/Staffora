const treeKeys = new Set(['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Home', 'End', ' ']);

export function attach(tree) {
  if (!tree || tree.dataset.unitTreeKeys === 'attached') return;
  tree.dataset.unitTreeKeys = 'attached';
  tree.addEventListener('keydown', (event) => {
    if (treeKeys.has(event.key) && event.target instanceof HTMLElement && event.target.getAttribute('role') === 'treeitem') {
      event.preventDefault();
    }
  });
}
