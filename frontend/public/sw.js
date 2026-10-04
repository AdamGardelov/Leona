// Shows Leona's push notifications and opens the chat a notification belongs to when it is tapped.
// A new version takes over right away so notification handling is never stale.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()));

self.addEventListener('push', (event) => {
  let data = { title: 'Leona', body: '', url: '/' };
  try {
    data = { ...data, ...event.data.json() };
  } catch {
    // A push without JSON still shows a notification.
  }
  event.waitUntil(
    self.registration.showNotification(data.title, {
      body: data.body,
      tag: data.tag,
      icon: '/icon-192.png',
      badge: '/icon-192.png',
      data: { url: data.url },
    }),
  );
});

// If Leona is already open, it is focused and told which chat to show (no reload, which home-screen
// apps on iPhone do not allow); otherwise Leona opens on that chat. Other addresses open as they are.
self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const url = new URL(event.notification.data?.url || '/', self.location.origin).href;
  event.waitUntil(
    (async () => {
      const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
      const open = windows.find((w) => w.url.startsWith(self.location.origin));
      if (open && url.startsWith(self.location.origin)) {
        await open.focus();
        open.postMessage({ type: 'open', url });
        return;
      }
      await self.clients.openWindow(url);
    })(),
  );
});
