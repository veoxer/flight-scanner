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

    var monthNames = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    var dayNames = ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"];

    function maskDateField(field) {
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
    }

    function enforceDatePair(depart, ret, changedField) {
        var departDate = parseDisplayDate(depart.value);
        var returnDate = parseDisplayDate(ret.value);
        var min = todayDate();

        if (changedField === depart && departDate) {
            if (departDate < min) {
                depart.value = "";
                return;
            }

            depart.value = formatDisplayDate(departDate);
            if (returnDate && departDate > returnDate) {
                ret.value = formatDisplayDate(departDate);
                depart.value = "";
            }
        }

        if (changedField === ret && returnDate) {
            if (returnDate < min) {
                ret.value = "";
                return;
            }

            ret.value = formatDisplayDate(returnDate);
            if (departDate && returnDate < departDate) {
                depart.value = formatDisplayDate(returnDate);
                ret.value = "";
            }
        }
    }

    function renderCalendar(field, depart, ret, visibleDate) {
        var wrapper = field.closest(".date-picker-field");
        var existing = wrapper.querySelector(".date-calendar");
        if (existing) {
            existing.remove();
        }

        var selected = parseDisplayDate(field.value);
        var today = todayDate();
        var month = new Date(visibleDate.getFullYear(), visibleDate.getMonth(), 1);
        var firstDay = (month.getDay() + 6) % 7;
        var daysInMonth = new Date(month.getFullYear(), month.getMonth() + 1, 0).getDate();
        var calendar = document.createElement("div");
        calendar.className = "date-calendar";

        var header = document.createElement("div");
        header.className = "date-calendar-header";
        var previous = document.createElement("button");
        previous.type = "button";
        previous.textContent = "‹";
        var title = document.createElement("strong");
        title.textContent = monthNames[month.getMonth()] + " " + month.getFullYear();
        var next = document.createElement("button");
        next.type = "button";
        next.textContent = "›";
        header.append(previous, title, next);
        calendar.appendChild(header);

        var grid = document.createElement("div");
        grid.className = "date-calendar-grid";
        dayNames.forEach(function (day) {
            var label = document.createElement("span");
            label.className = "date-calendar-day-label";
            label.textContent = day;
            grid.appendChild(label);
        });

        for (var blank = 0; blank < firstDay; blank++) {
            grid.appendChild(document.createElement("span"));
        }

        for (var day = 1; day <= daysInMonth; day++) {
            var date = new Date(month.getFullYear(), month.getMonth(), day);
            var button = document.createElement("button");
            button.type = "button";
            button.textContent = String(day);
            if (date < today) {
                button.disabled = true;
            }
            if (selected && date.getTime() === selected.getTime()) {
                button.classList.add("selected");
            }
            if (date.getTime() === today.getTime()) {
                button.classList.add("today");
            }
            button.addEventListener("click", function (event) {
                var picked = new Date(Number(event.currentTarget.dataset.year), Number(event.currentTarget.dataset.month), Number(event.currentTarget.dataset.day));
                field.value = formatDisplayDate(picked);
                enforceDatePair(depart, ret, field);
                calendar.remove();
            });
            button.dataset.year = String(date.getFullYear());
            button.dataset.month = String(date.getMonth());
            button.dataset.day = String(date.getDate());
            grid.appendChild(button);
        }

        previous.addEventListener("click", function () {
            renderCalendar(field, depart, ret, new Date(month.getFullYear(), month.getMonth() - 1, 1));
        });
        next.addEventListener("click", function () {
            renderCalendar(field, depart, ret, new Date(month.getFullYear(), month.getMonth() + 1, 1));
        });

        calendar.appendChild(grid);
        wrapper.appendChild(calendar);
    }

    function showCalendar(field, depart, ret) {
        document.querySelectorAll(".date-calendar").forEach(function (calendar) {
            calendar.remove();
        });
        var selected = parseDisplayDate(field.value) || todayDate();
        renderCalendar(field, depart, ret, selected);
    }

    function bindDatePair(root) {
        var depart = root.querySelector("[data-flight-date='depart']");
        var ret = root.querySelector("[data-flight-date='return']");
        if (!depart || !ret || depart.dataset.boundFlightDates === "true") {
            return;
        }

        depart.dataset.boundFlightDates = "true";
        ret.dataset.boundFlightDates = "true";

        [depart, ret].forEach(function (field) {
            field.addEventListener("input", function () {
                maskDateField(field);
            });
            field.addEventListener("focus", function () {
                showCalendar(field, depart, ret);
            });
        });

        depart.addEventListener("change", function () {
            enforceDatePair(depart, ret, depart);
        });

        ret.addEventListener("change", function () {
            enforceDatePair(depart, ret, ret);
        });

        root.querySelectorAll("[data-date-toggle]").forEach(function (button) {
            button.addEventListener("click", function () {
                var target = root.querySelector("[data-flight-date='" + button.getAttribute("data-date-toggle") + "']");
                if (target) {
                    showCalendar(target, depart, ret);
                    target.focus();
                }
            });
        });

        document.addEventListener("click", function (event) {
            if (!event.target.closest(".date-picker-field")) {
                document.querySelectorAll(".date-calendar").forEach(function (calendar) {
                    calendar.remove();
                });
            }
        });
    }

    function createOption(item, input, typeInput, menu) {
        var button = document.createElement("button");
        button.type = "button";
        button.className = "location-option";
        button.innerHTML =
            '<span class="location-kind"></span><span><strong></strong><small></small></span>';
        button.querySelector(".location-kind").textContent = item.type;
        button.querySelector("strong").textContent = item.primary;
        button.querySelector("small").textContent = item.secondary;
        button.addEventListener("mousedown", function (event) {
            event.preventDefault();
            input.value = item.value;
            typeInput.value = item.type;
            menu.hidden = true;
        });
        return button;
    }

    function bindLocationAutocomplete(root) {
        root.querySelectorAll("[data-location-input]").forEach(function (input) {
            if (input.dataset.boundLocation === "true") {
                return;
            }

            input.dataset.boundLocation = "true";
            var name = input.getAttribute("data-location-input");
            var menu = root.querySelector("[data-location-menu='" + name + "']");
            var typeInput = root.querySelector("[data-location-type='" + name + "']");
            var controller = null;

            function close() {
                window.setTimeout(function () {
                    menu.hidden = true;
                }, 120);
            }

            input.addEventListener("blur", close);
            input.addEventListener("input", function () {
                var query = input.value.trim();
                if (query.length < 2) {
                    menu.hidden = true;
                    menu.replaceChildren();
                    return;
                }

                if (controller) {
                    controller.abort();
                }

                controller = new AbortController();
                fetch("/api/locations/suggest?q=" + encodeURIComponent(query), {
                    headers: { "Accept": "application/json" },
                    signal: controller.signal
                })
                    .then(function (response) {
                        if (!response.ok) {
                            throw new Error("Suggestion request failed");
                        }
                        return response.json();
                    })
                    .then(function (items) {
                        menu.replaceChildren();
                        if (!items.length) {
                            menu.hidden = true;
                            return;
                        }

                        items.forEach(function (item) {
                            menu.appendChild(createOption(item, input, typeInput, menu));
                        });
                        menu.hidden = false;
                    })
                    .catch(function (error) {
                        if (error.name !== "AbortError") {
                            menu.hidden = true;
                        }
                    });
            });
        });
    }

    function cabinText(value) {
        switch (value) {
            case "PremiumEconomy":
                return "Premium economy";
            case "Business":
                return "Business";
            case "First":
                return "First";
            default:
                return "Economy";
        }
    }

    function bindTravellerPanel(root) {
        var toggle = root.querySelector("[data-traveller-toggle]");
        var panel = root.querySelector("[data-traveller-panel]");
        if (!toggle || !panel || toggle.dataset.boundTravellers === "true") {
            return;
        }

        toggle.dataset.boundTravellers = "true";
        var summary = root.querySelector("[data-traveller-summary]");
        var cabin = root.querySelector("[data-cabin-select]");

        function count(name) {
            return Number(root.querySelector("[data-hidden-count='" + name + "']").value || "0");
        }

        function setCount(name, value) {
            var min = name === "adults" ? 1 : 0;
            var next = Math.max(min, Math.min(9, value));
            root.querySelector("[data-hidden-count='" + name + "']").value = String(next);
            root.querySelector("[data-count='" + name + "']").textContent = String(next);
        }

        function updateSummary() {
            var total = count("adults") + count("children");
            var noun = total === 1 ? "traveller" : "travellers";
            summary.textContent = total + " " + noun + ", " + cabinText(cabin.value);
        }

        toggle.addEventListener("click", function () {
            panel.hidden = !panel.hidden;
        });

        root.querySelectorAll("[data-step]").forEach(function (button) {
            button.addEventListener("click", function () {
                var name = button.getAttribute("data-step");
                var delta = Number(button.getAttribute("data-delta"));
                setCount(name, count(name) + delta);
                updateSummary();
            });
        });

        cabin.addEventListener("change", updateSummary);
        var apply = root.querySelector("[data-traveller-apply]");
        if (apply) {
            apply.addEventListener("click", function () {
                panel.hidden = true;
            });
        }

        document.addEventListener("click", function (event) {
            if (!panel.hidden && !panel.contains(event.target) && !toggle.contains(event.target)) {
                panel.hidden = true;
            }
        });
    }

    function bindSearchForm() {
        var root = document.querySelector(".search-native-form");
        if (!root) {
            return;
        }

        bindDatePair(root);
        bindLocationAutocomplete(root);
        bindTravellerPanel(root);
    }

    document.addEventListener("DOMContentLoaded", bindSearchForm);
    document.addEventListener("enhancedload", bindSearchForm);
})();
