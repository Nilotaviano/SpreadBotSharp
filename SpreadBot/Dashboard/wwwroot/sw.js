// The dashboard's service worker: keeps the app shell available offline and shows push notifications.
// The API is never cached: the page keeps its own last known state for when the bot can't be reached.

const SHELL = 'spreadbot-shell-v1';
const FILES = ['/', '/index.html', '/styles.css', '/app.js', '/manifest.webmanifest', '/icon-192.png', '/icon-512.png', '/badge-96.png', '/apple-touch-icon.png'];

self.addEventListener('install', event => {
  event.waitUntil(caches.open(SHELL).then(cache => cache.addAll(FILES)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', event => {
  event.waitUntil(
    caches.keys()
      .then(keys => Promise.all(keys.filter(key => key !== SHELL).map(key => caches.delete(key))))
      .then(() => self.clients.claim()));
});

// Shell: network first, so an updated bot serves its updated page; the cache only when the bot is unreachable
self.addEventListener('fetch', event => {
  const url = new URL(event.request.url);
  if (event.request.method !== 'GET' || url.origin !== self.location.origin || url.pathname.startsWith('/api/')) return;

  event.respondWith(
    fetch(event.request)
      .then(response => {
        if (response.ok) {
          const copy = response.clone();
          caches.open(SHELL).then(cache => cache.put(event.request, copy));
        }
        return response;
      })
      .catch(() => caches.match(event.request).then(cached => cached || caches.match('/index.html'))));
});

self.addEventListener('push', event => {
  let message = { title: 'SpreadBot', body: '' };
  try {
    message = event.data ? event.data.json() : message;
  } catch {
    message.body = event.data ? event.data.text() : '';
  }

  event.waitUntil(self.registration.showNotification(message.title || 'SpreadBot', {
    body: message.body || '',
    tag: message.tag,
    renotify: message.category === 'halt' || message.category === 'errors',
    requireInteraction: message.category === 'halt',
    icon: '/icon-192.png',
    badge: '/badge-96.png',
    data: { url: message.url || '/' }
  }));
});

self.addEventListener('notificationclick', event => {
  event.notification.close();
  const target = new URL(event.notification.data?.url || '/', self.location.origin);

  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    for (const client of windows) {
      if (new URL(client.url).origin === target.origin) {
        await client.focus();
        client.postMessage({ navigate: target.hash || '#overview' });
        return;
      }
    }
    await self.clients.openWindow(target.href);
  })());
});
