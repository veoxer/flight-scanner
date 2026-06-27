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

  function bindNotificationButton() {
    const button = document.querySelector("[data-enable-notifications]");
    const status = document.querySelector("[data-notification-status]");
    if (!button || !status || button.dataset.boundNotifications === "true") {
      return;
    }

    button.dataset.boundNotifications = "true";
    button.addEventListener("click", async () => {
      button.disabled = true;
      status.hidden = false;
      status.textContent = "...";

      try {
        const permission = await window.flightNotifications.requestPermission();
        const subscription = await window.flightNotifications.subscribePush();
        if (subscription === "subscribed") {
          status.textContent = button.dataset.statusSubscribed || "Push notifications are enabled for this device.";
        } else if (subscription === "missing-public-key") {
          status.textContent = button.dataset.statusMissingKey || "Browser notifications are allowed. Add VAPID keys to enable push.";
        } else if (subscription === "unsupported") {
          status.textContent = button.dataset.statusUnsupported || "This browser does not support push notifications.";
        } else {
          status.textContent = (button.dataset.statusPermission || "Notification permission: {0}.").replace("{0}", permission);
        }
      } catch (error) {
        status.textContent = error.message || "Could not enable notifications.";
      } finally {
        button.disabled = false;
      }
    });
  }

  document.addEventListener("DOMContentLoaded", bindNotificationButton);
  document.addEventListener("enhancedload", bindNotificationButton);
  ensureServiceWorker();
})();
