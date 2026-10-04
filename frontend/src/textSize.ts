// Leona follows the iPhone's text size (Settings → Display & Brightness → Text Size), like the native apps.
// Safari exposes it as the -apple-system-body font, 17 px at the default size; the ratio goes into --dt,
// which the phone styles multiply their sizes by. Other browsers keep the default sizes.
const defaultBody = 17;
// From the smallest setting up to the largest regular one; the accessibility sizes would break the layout.
const smallest = 14;
const largest = 23;

export function followSystemTextSize() {
  if (!CSS.supports('font', '-apple-system-body') || !matchMedia('(pointer: coarse)').matches) {
    return;
  }
  const probe = document.createElement('span');
  probe.setAttribute('aria-hidden', 'true');
  probe.style.cssText =
    'font: -apple-system-body; position: absolute; visibility: hidden; pointer-events: none;';
  document.body.append(probe);

  const update = () => {
    const size = parseFloat(getComputedStyle(probe).fontSize);
    if (!size) {
      return;
    }
    const ratio = Math.min(largest, Math.max(smallest, size)) / defaultBody;
    document.documentElement.style.setProperty('--dt', ratio.toFixed(3));
  };
  update();
  // The setting can change while Leona is in the background.
  document.addEventListener('visibilitychange', update);
}
