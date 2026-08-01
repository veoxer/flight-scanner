var navigationSequence = 0;
var navigationStartedAt = 0;
var navigationHideTimer = null;
var navigationSafetyTimer = null;
var navigationLocationTimer = null;
var navigationOriginUrl = null;

function navigationLoader() {
    return document.querySelector("[data-app-navigation-loading]");
}

function showNavigationLoader() {
    var loader = navigationLoader();
    if (!loader) {
        return;
    }

    // A captured link click and Blazor's enhanced-navigation event can both report
    // the same navigation. Keep one sequence so the second signal can't orphan the
    // completion timer used by interactive client-side routes.
    if (!loader.hidden) {
        return;
    }

    navigationSequence++;
    navigationStartedAt = Date.now();
    navigationOriginUrl = window.location.href;
    window.clearTimeout(navigationHideTimer);
    window.clearTimeout(navigationSafetyTimer);
    window.clearInterval(navigationLocationTimer);
    loader.hidden = false;
    document.documentElement.setAttribute("aria-busy", "true");

    var sequence = navigationSequence;
    navigationLocationTimer = window.setInterval(function () {
        if (sequence !== navigationSequence) {
            window.clearInterval(navigationLocationTimer);
            return;
        }

        // Interactive Blazor routing updates the URL without necessarily raising
        // enhancednavigationend. Once that happens, allow its render batch to reach
        // the DOM and complete this navigation explicitly.
        if (window.location.href !== navigationOriginUrl) {
            window.clearInterval(navigationLocationTimer);
            window.requestAnimationFrame(function () {
                window.requestAnimationFrame(function () {
                    if (sequence === navigationSequence) {
                        hideNavigationLoader(false);
                    }
                });
            });
        }
    }, 40);

    navigationSafetyTimer = window.setTimeout(function () {
        if (sequence === navigationSequence) {
            hideNavigationLoader(true);
        }
    }, 8000);
}

function hideNavigationLoader(immediately) {
    var loader = navigationLoader();
    if (!loader) {
        return;
    }

    var sequence = navigationSequence;
    var elapsed = Date.now() - navigationStartedAt;
    var delay = immediately ? 0 : Math.max(0, 450 - elapsed);
    window.clearTimeout(navigationHideTimer);
    navigationHideTimer = window.setTimeout(function () {
        if (sequence !== navigationSequence) {
            return;
        }

        loader.hidden = true;
        document.documentElement.removeAttribute("aria-busy");
        window.clearTimeout(navigationSafetyTimer);
        window.clearInterval(navigationLocationTimer);
        navigationOriginUrl = null;
    }, delay);
}

function isInternalPageLink(event, anchor) {
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey ||
        anchor.hasAttribute("download") || (anchor.target && anchor.target !== "_self")) {
        return false;
    }

    var url;
    try {
        url = new URL(anchor.href, document.baseURI);
    } catch {
        return false;
    }

    if (url.origin !== window.location.origin) {
        return false;
    }

    return url.pathname !== window.location.pathname || url.search !== window.location.search;
}

export function afterWebStarted(blazor) {
    blazor.addEventListener("enhancednavigationstart", showNavigationLoader);
    blazor.addEventListener("enhancednavigationend", function () {
        hideNavigationLoader(false);
    });
    blazor.addEventListener("enhancedload", function () {
        hideNavigationLoader(false);
    });

    document.addEventListener("click", function (event) {
        var anchor = event.target.closest && event.target.closest("a[href]");
        if (anchor && isInternalPageLink(event, anchor)) {
            showNavigationLoader();
        }
    }, true);

    document.addEventListener("submit", function (event) {
        var form = event.target;
        if (!(form instanceof HTMLFormElement) || form.matches(".search-native-form") || form.target) {
            return;
        }

        var action = new URL(form.action || window.location.href, document.baseURI);
        if (action.origin === window.location.origin) {
            showNavigationLoader();
        }
    }, true);

    window.addEventListener("pageshow", function () {
        hideNavigationLoader(true);
    });

    window.addEventListener("popstate", function () {
        showNavigationLoader();
        window.requestAnimationFrame(function () {
            window.requestAnimationFrame(function () {
                hideNavigationLoader(false);
            });
        });
    });
}
