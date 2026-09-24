import { useCallback, useEffect, useRef, useState } from 'react';

const preferenceKey = 'marvin-sidebar-pinned';
const focusable = 'a[href],button:not([disabled]),summary,input:not([disabled]),select:not([disabled]),textarea:not([disabled])';

export function useSidebar(blocked: boolean) {
  const [desktop, setDesktop] = useState(() => matchMedia('(min-width: 768px)').matches);
  const [pinned, setPinned] = useState(() => {
    try { return localStorage.getItem(preferenceKey) !== 'false'; } catch { return true; }
  });
  const [transient, setTransient] = useState(false);
  const panelRef = useRef<HTMLElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const mainRef = useRef<HTMLElement>(null);
  const leaveTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const keyboardFocus = useRef(false);
  const focusOnOpen = useRef(false);
  const hovering = useRef(false);
  const visible = desktop ? pinned || transient : transient;
  const drawer = !desktop && visible;
  const mode = desktop ? pinned ? 'pinned' : transient ? 'floating' : 'collapsed' : drawer ? 'drawer' : 'mobile';

  const cancelLeave = useCallback(() => {
    if (leaveTimer.current) clearTimeout(leaveTimer.current);
    leaveTimer.current = null;
  }, []);
  const close = useCallback((restore: 'trigger' | 'main' | false = false) => {
    cancelLeave();
    hovering.current = false;
    setTransient(false);
    focusOnOpen.current = false;
    if (restore) requestAnimationFrame(() => (restore === 'trigger' ? triggerRef : mainRef).current?.focus({ preventScroll: true }));
  }, [cancelLeave]);
  const setPreference = useCallback((value: boolean) => {
    close();
    setPinned(value);
    try { localStorage.setItem(preferenceKey, String(value)); } catch { /* Private storage may be unavailable. */ }
    if (!value) requestAnimationFrame(() => triggerRef.current?.focus({ preventScroll: true }));
  }, [close]);
  const open = useCallback(() => {
    if (blocked) return;
    cancelLeave();
    focusOnOpen.current = true;
    setTransient(true);
    if (panelRef.current && !panelRef.current.inert) {
      panelRef.current.querySelector<HTMLElement>(focusable)?.focus();
      focusOnOpen.current = false;
    }
  }, [blocked, cancelLeave]);
  const enter = useCallback(() => {
    hovering.current = true;
    cancelLeave();
    if (desktop && !pinned && !blocked) setTransient(true);
  }, [desktop, pinned, blocked, cancelLeave]);
  const leave = useCallback(() => {
    hovering.current = false;
    if (!desktop || pinned) return;
    cancelLeave();
    leaveTimer.current = setTimeout(() => {
      if (keyboardFocus.current && panelRef.current?.contains(document.activeElement)) return;
      close(panelRef.current?.contains(document.activeElement) ? 'main' : false);
    }, 150);
  }, [desktop, pinned, cancelLeave, close]);
  const navigate = useCallback(() => {
    if (!desktop || !pinned) close('main');
  }, [desktop, pinned, close]);

  useEffect(() => {
    if (!visible || !focusOnOpen.current || blocked) return;
    // Focus after inert and visibility have been applied to both the drawer
    // and workspace; focusing during the click's render is cleared by inert.
    const frame = requestAnimationFrame(() => {
      panelRef.current?.querySelector<HTMLElement>(focusable)?.focus();
      focusOnOpen.current = false;
    });
    return () => cancelAnimationFrame(frame);
  });

  useEffect(() => {
    const query = matchMedia('(min-width: 768px)');
    const resize = () => {
      if (panelRef.current?.contains(document.activeElement)) requestAnimationFrame(() => mainRef.current?.focus({ preventScroll: true }));
      setDesktop(query.matches);
      close();
    };
    const storage = (event: StorageEvent) => {
      if (event.key !== preferenceKey && event.key !== null) return;
      setPinned(event.newValue !== 'false');
      close('main');
    };
    query.addEventListener('change', resize);
    window.addEventListener('storage', storage);
    return () => { query.removeEventListener('change', resize); window.removeEventListener('storage', storage); cancelLeave(); };
  }, [close, cancelLeave]);

  useEffect(() => { if (blocked) close(); }, [blocked, close]);
  useEffect(() => {
    if (!drawer) return;
    const original = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => { document.body.style.overflow = original; };
  }, [drawer]);
  useEffect(() => {
    const keydown = (event: KeyboardEvent) => {
      keyboardFocus.current = true;
      if (blocked || !visible) return;
      if (event.key === 'Escape' && !event.defaultPrevented) {
        const disclosure = panelRef.current?.querySelector('details[open]');
        if (disclosure) { event.preventDefault(); disclosure.removeAttribute('open'); disclosure.querySelector('summary')?.focus(); return; }
      }
      if (desktop && pinned) return;
      if (event.key === 'Escape' && !event.defaultPrevented) {
        event.preventDefault(); close('trigger');
      }
      if (drawer && event.key === 'Tab') {
        const targets = [...(panelRef.current?.querySelectorAll<HTMLElement>(focusable) ?? [])].filter(el => {
          const closed = el.closest('details:not([open])');
          return el.getClientRects().length > 0 && (!closed || (el.tagName === 'SUMMARY' && el.parentElement === closed));
        });
        const first = targets[0], last = targets.at(-1);
        if (event.shiftKey && (document.activeElement === first || !panelRef.current?.contains(document.activeElement))) {
          event.preventDefault(); last?.focus();
        } else if (!event.shiftKey && document.activeElement === last) {
          event.preventDefault(); first?.focus();
        }
      }
    };
    const pointerdown = (event: PointerEvent) => {
      keyboardFocus.current = false;
      panelRef.current?.querySelectorAll('details[open]').forEach(detail => {
        if (!detail.contains(event.target as Node)) detail.removeAttribute('open');
      });
      if (desktop && transient && !panelRef.current?.contains(event.target as Node) && !triggerRef.current?.contains(event.target as Node)) close();
    };
    const pointermove = (event: PointerEvent) => {
      if (!desktop || pinned || blocked || event.pointerType !== 'mouse') return;
      if (event.clientX <= 24) enter();
      else if (transient && !panelRef.current?.contains(event.target as Node) && !(event.target as Element).closest('.sidebar-launcher')) leave();
    };
    const focusin = (event: FocusEvent) => {
      if (desktop && transient && !hovering.current && !panelRef.current?.contains(event.target as Node)) close();
    };
    const blur = () => { if (desktop && !pinned) close(); };
    document.addEventListener('keydown', keydown);
    document.addEventListener('pointerdown', pointerdown);
    document.addEventListener('focusin', focusin);
    window.addEventListener('pointermove', pointermove);
    window.addEventListener('blur', blur);
    return () => {
      document.removeEventListener('keydown', keydown);
      document.removeEventListener('pointerdown', pointerdown);
      document.removeEventListener('focusin', focusin);
      window.removeEventListener('pointermove', pointermove);
      window.removeEventListener('blur', blur);
    };
  }, [blocked, desktop, drawer, pinned, transient, visible, enter, leave, close]);

  return { desktop, pinned, visible, drawer, mode, panelRef, triggerRef, mainRef, open, close, enter, leave, navigate, setPreference };
}
