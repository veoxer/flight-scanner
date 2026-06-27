(function () {
    function todayIso() {
        var now = new Date();
        var offsetDate = new Date(now.getTime() - now.getTimezoneOffset() * 60000);
        return offsetDate.toISOString().slice(0, 10);
    }

    function bindDatePair() {
        var depart = document.querySelector("[data-flight-date='depart']");
        var ret = document.querySelector("[data-flight-date='return']");
        if (!depart || !ret || depart.dataset.boundFlightDates === "true") {
            return;
        }

        depart.dataset.boundFlightDates = "true";
        ret.dataset.boundFlightDates = "true";

        var min = todayIso();
        depart.min = min;
        ret.min = min;

        depart.addEventListener("change", function () {
            if (depart.value && depart.value < min) {
                depart.value = "";
                depart.dispatchEvent(new Event("input", { bubbles: true }));
                return;
            }

            if (depart.value && ret.value && depart.value > ret.value) {
                ret.value = depart.value;
                depart.value = "";
                depart.dispatchEvent(new Event("input", { bubbles: true }));
                ret.dispatchEvent(new Event("input", { bubbles: true }));
            }
        });

        ret.addEventListener("change", function () {
            if (ret.value && ret.value < min) {
                ret.value = "";
                ret.dispatchEvent(new Event("input", { bubbles: true }));
                return;
            }

            if (depart.value && ret.value && ret.value < depart.value) {
                depart.value = ret.value;
                ret.value = "";
                depart.dispatchEvent(new Event("input", { bubbles: true }));
                ret.dispatchEvent(new Event("input", { bubbles: true }));
            }
        });
    }

    document.addEventListener("DOMContentLoaded", bindDatePair);
    document.addEventListener("enhancedload", bindDatePair);
})();
