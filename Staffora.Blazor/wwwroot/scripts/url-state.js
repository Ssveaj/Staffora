export function apply(path, changes) {
  if (!changes || decodedPath(window.location.pathname) !== decodedPath(path)) return;

  const url = new URL(window.location.href);
  let changed = false;

  for (const [key, value] of Object.entries(changes)) {
    if (value === null || value === undefined || value === '') {
      if (url.searchParams.has(key)) {
        url.searchParams.delete(key);
        changed = true;
      }
    } else if (url.searchParams.get(key) !== value) {
      url.searchParams.set(key, value);
      changed = true;
    }
  }

  if (!changed) return;

  history.replaceState(history.state, '', url.pathname + url.search + url.hash);
}

function decodedPath(path) {
  try {
    return decodeURIComponent(path);
  } catch {
    return path;
  }
}
