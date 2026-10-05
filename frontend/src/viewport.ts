// On iPhones the page itself could scroll and bounce under the app, and after the keyboard closed iOS
// sometimes left it pushed up with an empty band below. The page is now pinned (style.css) and sized to
// the visible area, so the composer sits right above the keyboard and returns to the bottom after it.
export function followVisibleArea() {
  // iOS scrolls the page to show a focused field even though nobody can scroll it by hand. The app
  // already keeps the field in view, so the page is put straight back.
  const settle = () => {
    if (document.body.scrollTop || document.documentElement.scrollTop) {
      document.body.scrollTop = 0;
      document.documentElement.scrollTop = 0;
    }
  };
  document.body.addEventListener('scroll', settle);
  window.addEventListener('scroll', settle);

  const viewport = window.visualViewport;
  if (!viewport || !matchMedia('(pointer: coarse)').matches) {
    return;
  }
  const root = document.documentElement.style;
  const update = () => {
    // Pinch zoom moves the visible area over the app; the app keeps its size then.
    if (viewport.scale > 1.01) {
      return;
    }
    root.setProperty('--app-height', `${Math.round(viewport.height)}px`);
    root.setProperty('--app-top', `${Math.round(viewport.offsetTop)}px`);
    settle();
  };
  viewport.addEventListener('resize', update);
  viewport.addEventListener('scroll', update);
  update();
}
