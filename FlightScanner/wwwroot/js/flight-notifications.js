(function () {
  async function ensureServiceWorker() {
    if (!("serviceWorker" in navigator)) {
      return null;
    }

    const registration = await navigator.serviceWorker.register("/service-worker.js");
    return registration;
  }

  function urlBase64ToUint8Array(base64String) {
    const padding = "=".repeat((4 - base64String.length % 4) % 4);
    const base64 = (base64String + padding).replace(/-/g, "+").replace(/_/g, "/");
    const rawData = window.atob(base64);
    const outputArray = new Uint8Array(rawData.length);
    for (let i = 0; i < rawData.length; ++i) {
      outputArray[i] = rawData.charCodeAt(i);
    }
    return outputArray;
  }

  window.flightNotifications = {
    async requestPermission() {
      if (!("Notification" in window)) {
        return "unsupported";
      }

      const permission = await Notification.requestPermission();
      await ensureServiceWorker();
      return permission;
    },
    async subscribePush() {
      const registration = await ensureServiceWorker();
      if (!registration || !("PushManager" in window)) {
        return "unsupported";
      }

      const keyResponse = await fetch("/api/push/public-key");
      const keyData = await keyResponse.json();
      if (!keyData.publicKey) {
        return "missing-public-key";
      }

      const subscription = await registration.pushManager.subscribe({
        userVisibleOnly: true,
        applicationServerKey: urlBase64ToUint8Array(keyData.publicKey)
      });

      await fetch("/api/push/subscribe", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(subscription)
      });
      return "subscribed";
    },
    async show(title, body) {
      const registration = await ensureServiceWorker();
      if (Notification.permission !== "granted" || !registration) {
        return;
      }

      await registration.showNotification(title, {
        body,
        icon: "/favicon.png",
        badge: "/favicon.png"
      });
    }
  };

  ensureServiceWorker();
})();
