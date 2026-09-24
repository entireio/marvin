export type Appearance = 'light' | 'dark' | 'system';
const key = 'marvin-appearance';
export function getAppearance(): Appearance {
 try { const saved = localStorage.getItem(key); if (saved === 'light' || saved === 'dark') return saved; } catch { /* Storage may be unavailable. */ }
 return 'system';
}
export function applyAppearance(mode: Appearance) {
 document.documentElement.dataset.appearance = mode;
}
export function saveAppearance(mode: Appearance) {
 try { localStorage.setItem(key, mode); } catch { /* Keep the choice for this page. */ }
 applyAppearance(mode);
}
applyAppearance(getAppearance());
window.addEventListener('storage', event => { if (event.key === key || event.key === null) applyAppearance(getAppearance()); });
