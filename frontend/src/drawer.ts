import { useEffect, useRef } from 'react';

// The conversation list as a drawer on phones: Escape closes it, swiping right pulls it in like going
// back, and swiping left puts it away.
export function useDrawerGestures(drawer: boolean, setDrawer: (open: boolean) => void) {
  useEffect(() => {
    if (!drawer) {
      return;
    }
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') {
        setDrawer(false);
      }
    }
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [drawer, setDrawer]);

  // The list follows the finger and settles open or closed on release.
  const drawerRef = useRef(drawer);
  drawerRef.current = drawer;
  useEffect(() => {
    const phone = window.matchMedia('(max-width: 650px)');
    let start: { x: number; y: number; time: number; open: boolean } | null = null;
    let dragging = false;
    let offset = 0;

    function aside() {
      return document.getElementById('sidebar');
    }
    function onStart(event: TouchEvent) {
      const target = event.target as Element;
      start =
        phone.matches &&
        event.touches.length === 1 &&
        !document.querySelector('dialog[open], .sheet-backdrop, .row-menu') &&
        // Sideways scrolling in code and tables, and text fields, keep their own gestures.
        !target.closest('pre, table, input, textarea, select, .rename-field')
          ? {
              x: event.touches[0].clientX,
              y: event.touches[0].clientY,
              time: Date.now(),
              open: drawerRef.current,
            }
          : null;
      dragging = false;
    }
    function onMove(event: TouchEvent) {
      const panel = aside();
      if (!start || !panel) {
        return;
      }
      const dx = event.touches[0].clientX - start.x;
      const dy = event.touches[0].clientY - start.y;
      if (!dragging) {
        if (Math.abs(dy) > 10 && Math.abs(dy) > Math.abs(dx)) {
          start = null;
          return;
        }
        if (Math.abs(dx) < 10 || (start.open ? dx > 0 : dx < 0)) {
          return;
        }
        dragging = true;
      }
      event.preventDefault();
      const width = panel.offsetWidth;
      offset = start.open ? Math.min(0, Math.max(-width, dx)) : Math.max(0, Math.min(width, dx));
      panel.style.transition = 'none';
      panel.style.visibility = 'visible';
      panel.style.transform = `translateX(${start.open ? offset : offset - width}px)`;
    }
    function onEnd() {
      const panel = aside();
      if (!start || !dragging || !panel) {
        start = null;
        return;
      }
      const width = panel.offsetWidth;
      const speed = Math.abs(offset) / Math.max(1, Date.now() - start.time);
      const far = Math.abs(offset) > width * 0.35 || speed > 0.6;
      const open = start.open ? !far : far;
      // Hand the panel back to the stylesheet, which animates it from here to where it settles.
      panel.style.transition = '';
      panel.style.visibility = '';
      panel.style.transform = '';
      start = null;
      dragging = false;
      setDrawer(open);
    }

    document.addEventListener('touchstart', onStart, { passive: true });
    document.addEventListener('touchmove', onMove, { passive: false });
    document.addEventListener('touchend', onEnd);
    document.addEventListener('touchcancel', onEnd);
    return () => {
      document.removeEventListener('touchstart', onStart);
      document.removeEventListener('touchmove', onMove);
      document.removeEventListener('touchend', onEnd);
      document.removeEventListener('touchcancel', onEnd);
    };
  }, [setDrawer]);
}
