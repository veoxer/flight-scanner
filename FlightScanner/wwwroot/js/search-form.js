(function () {
    function todayDate() {
        var now = new Date();
        return new Date(now.getFullYear(), now.getMonth(), now.getDate());
    }

    function parseDisplayDate(value) {
        var match = /^(\d{2})\/(\d{2})\/(\d{4})$/.exec((value || "").trim());
        if (!match) {
            return null;
        }

        var day = Number(match[1]);
        var month = Number(match[2]) - 1;
        var year = Number(match[3]);
        var date = new Date(year, month, day);
        if (date.getFullYear() !== year || date.getMonth() !== month || date.getDate() !== day) {
            return null;
        }

        return date;
    }

    function formatDisplayDate(date) {
        return String(date.getDate()).padStart(2, "0") + "/" +
            String(date.getMonth() + 1).padStart(2, "0") + "/" +
            date.getFullYear();
    }

    function emitInput(element) {
        element.dispatchEvent(new Event("input", { bubbles: true }));
    }

    function bindDatePair() {
        var depart = document.querySelector("[data-flight-date='depart']");
        var ret = document.querySelector("[data-flight-date='return']");
        if (!depart || !ret || depart.dataset.boundFlightDates === "true") {
            return;
        }

        depart.dataset.boundFlightDates = "true";
        ret.dataset.boundFlightDates = "true";

        [depart, ret].forEach(function (field) {
            field.addEventListener("input", function () {
                var digits = field.value.replace(/\D/g, "").slice(0, 8);
                var pieces = [];
                if (digits.length > 0) {
                    pieces.push(digits.slice(0, 2));
                }
                if (digits.length > 2) {
                    pieces.push(digits.slice(2, 4));
                }
                if (digits.length > 4) {
                    pieces.push(digits.slice(4, 8));
                }

                field.value = pieces.join("/");
            });
        });

        depart.addEventListener("change", function () {
            var picked = parseDisplayDate(depart.value);
            var currentReturn = parseDisplayDate(ret.value);
            if (!picked) {
                return;
            }

            if (picked < todayDate()) {
                depart.value = "";
                emitInput(depart);
                return;
            }

            depart.value = formatDisplayDate(picked);
            if (currentReturn && picked > currentReturn) {
                ret.value = formatDisplayDate(picked);
                depart.value = "";
                emitInput(depart);
                emitInput(ret);
            }
        });

        ret.addEventListener("change", function () {
            var picked = parseDisplayDate(ret.value);
            var currentDepart = parseDisplayDate(depart.value);
            if (!picked) {
                return;
            }

            if (picked < todayDate()) {
                ret.value = "";
                emitInput(ret);
                return;
            }

            ret.value = formatDisplayDate(picked);
            if (currentDepart && picked < currentDepart) {
                depart.value = formatDisplayDate(picked);
                ret.value = "";
                emitInput(depart);
                emitInput(ret);
            }
        });
    }

    document.addEventListener("DOMContentLoaded", bindDatePair);
    document.addEventListener("enhancedload", bindDatePair);
})();
