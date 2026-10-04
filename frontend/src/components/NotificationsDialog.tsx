import { useEffect, useState } from 'react';
import { api, send, type AppNotification, errorText } from '../api';
import { Icon } from '../icons';
import { Dialog } from './Dialog';
import { weekdayTime } from '../format';

function base64UrlToBytes(value: string) {
  const padded =
    value.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - (value.length % 4)) % 4);
  return Uint8Array.from(atob(padded), (c) => c.charCodeAt(0));
}

// Web Push needs a secure page (HTTPS or localhost), a service worker and, on iPhone, Leona added to the home screen.
function pushSupport() {
  if (!window.isSecureContext) {
    return 'Push needs HTTPS. Open Leona through Tailscale (https://…ts.net) to turn it on.';
  }
  if (!('serviceWorker' in navigator) || !('PushManager' in window)) {
    return /iPhone|iPad/.test(navigator.userAgent)
      ? 'On iPhone: Share › Add to Home Screen, then open Leona from the home screen.'
      : 'This browser does not support push notifications.';
  }
  return null;
}

export async function registerServiceWorker() {
  if (window.isSecureContext && 'serviceWorker' in navigator) {
    await navigator.serviceWorker.register('/sw.js').catch(() => undefined);
  }
}

export function NotificationsDialog({
  open,
  onClose,
  onRead,
  onOpenLink,
}: {
  open: boolean;
  onClose: () => void;
  onRead: () => void;
  // Opens the chat a notification belongs to, without reloading the page.
  onOpenLink: (url: string) => void;
}) {
  const [items, setItems] = useState<AppNotification[]>([]);
  const [pushState, setPushState] = useState<'unknown' | 'on' | 'off'>('unknown');
  const [message, setMessage] = useState('');
  const unsupported = pushSupport();

  useEffect(() => {
    if (!open) {
      return;
    }
    setMessage('');
    api<{ items: AppNotification[] }>('/notifications')
      .then((data) => {
        setItems(data.items);
        return send('/notifications/read', 'POST');
      })
      .then(onRead)
      .catch((e) => setMessage(errorText(e)));
    if (!unsupported) {
      navigator.serviceWorker.ready
        .then((registration) => registration.pushManager.getSubscription())
        .then((subscription) => setPushState(subscription ? 'on' : 'off'))
        .catch(() => setPushState('off'));
    }
  }, [open]);

  async function enablePush() {
    setMessage('');
    try {
      if ((await Notification.requestPermission()) !== 'granted') {
        setMessage(
          'Notifications are blocked. Allow them for Leona in the browser or phone settings.',
        );
        return;
      }
      await registerServiceWorker();
      const registration = await navigator.serviceWorker.ready;
      const { publicKey } = await api<{ publicKey: string }>('/push/key');
      const subscription = await registration.pushManager.subscribe({
        userVisibleOnly: true,
        applicationServerKey: base64UrlToBytes(publicKey),
      });
      const json = subscription.toJSON();
      await send('/push/subscribe', 'POST', { endpoint: json.endpoint, keys: json.keys });
      setPushState('on');
      setMessage('Notifications are on for this device.');
    } catch (e) {
      setMessage(errorText(e));
    }
  }

  async function disablePush() {
    const registration = await navigator.serviceWorker.ready;
    const subscription = await registration.pushManager.getSubscription();
    if (subscription) {
      await send('/push/unsubscribe', 'POST', { endpoint: subscription.endpoint });
      await subscription.unsubscribe();
    }
    setPushState('off');
  }

  return (
    <Dialog title="Notifications" open={open} onClose={onClose}>
      <section className="push-panel" aria-label="Notifications on this device">
        <Icon name="bell" size={18} />
        <div>
          <b>This device</b>
          <small>
            {unsupported ??
              (pushState === 'on'
                ? 'Push notifications are on.'
                : 'Get morning briefs, approvals and watches as notifications.')}
          </small>
        </div>
        {!unsupported &&
          (pushState === 'on' ? (
            <>
              <button className="secondary small" onClick={() => void send('/push/test', 'POST')}>
                Test
              </button>
              <button className="secondary small" onClick={() => void disablePush()}>
                Turn off
              </button>
            </>
          ) : (
            <button className="primary small" onClick={() => void enablePush()}>
              Turn on
            </button>
          ))}
      </section>
      {message && (
        <p className="notice-text" role="status">
          {message}
        </p>
      )}
      <ul className="notification-list">
        {items.map((n) => (
          <li key={n.id} className={n.read ? '' : 'unread'}>
            <a
              href={n.url ?? '/'}
              target={n.url?.startsWith('http') ? '_blank' : undefined}
              rel="noopener noreferrer"
              onClick={(e) => {
                if (n.url && !n.url.startsWith('http')) {
                  e.preventDefault();
                  onOpenLink(n.url);
                }
              }}
            >
              <span className="notification-head">
                <b>{n.title}</b>
                <time dateTime={n.createdAt}>{weekdayTime(n.createdAt)}</time>
              </span>
              <span className="notification-body">{n.body}</span>
            </a>
          </li>
        ))}
        {!items.length && <li className="hint">No notifications yet.</li>}
      </ul>
    </Dialog>
  );
}
