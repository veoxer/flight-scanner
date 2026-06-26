const cacheName = "flight-scanner-v1";
const coreAssets = ["/", "/manifest.webmanifest", "/app.css", "/favicon.png"];

self.addEventListener("install", event => {
  event.waitUntil(caches.open(cacheName).then(cache => cache.addAll(coreAssets)));
  self.skipWaiting();
});

self.addEventListener("activate", event => {
  event.waitUntil(
    caches.keys().then(keys => Promise.all(keys.filter(key => key !== cacheName).map(key => caches.delete(key))))
  );
  self.clients.claim();
});

self.addEventListener("fetch", event => {
  if (event.request.method !== "GET") {
    return;
  }

  event.respondWith(
    fetch(event.request).catch(() => caches.match(event.request).then(response => response || caches.match("/")))
  );
});

self.addEventListener("push", event => {
  const data = event.data ? event.data.json() : {};
  event.waitUntil(self.registration.showNotification(data.title || "Flight alert", {
    body: data.body || "A watched fare matched your target.",
    icon: "/favicon.png",
    badge: "/favicon.png",
    data: data.url || "/alerts"
  }));
});

self.addEventListener("notificationclick", event => {
  event.notification.close();
  event.waitUntil(clients.openWindow(event.notification.data || "/alerts"));
});
