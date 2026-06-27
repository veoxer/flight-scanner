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
    var currencies = [
        { code: "MAD", symbol: "د.م.", name: "Moroccan Dirham" },
        { code: "USD", symbol: "$", name: "US Dollar" },
        { code: "EUR", symbol: "€", name: "Euro" },
        { code: "GBP", symbol: "£", name: "British Pound" },
        { code: "CAD", symbol: "C$", name: "Canadian Dollar" },
        { code: "AUD", symbol: "A$", name: "Australian Dollar" },
        { code: "JPY", symbol: "¥", name: "Japanese Yen" },
        { code: "CNY", symbol: "¥", name: "Chinese Yuan" },
        { code: "AED", symbol: "د.إ", name: "UAE Dirham" },
        { code: "SAR", symbol: "﷼", name: "Saudi Riyal" },
        { code: "CHF", symbol: "CHF", name: "Swiss Franc" },
        { code: "BRL", symbol: "R$", name: "Brazilian Real" },
        { code: "ZAR", symbol: "R", name: "South African Rand" },
        { code: "INR", symbol: "₹", name: "Indian Rupee" }
    ];

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

        if (changedField === depart && departDate) {
            depart.value = formatDisplayDate(departDate);
            if (returnDate && departDate > returnDate) {
                ret.value = formatDisplayDate(departDate);
                depart.value = "";
            }
        }

        if (changedField === ret && returnDate) {
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
        calendar.addEventListener("mousedown", function (event) {
            event.stopPropagation();
        });
        calendar.addEventListener("click", function (event) {
            event.stopPropagation();
        });

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
            if (selected && date.getTime() === selected.getTime()) {
                button.classList.add("selected");
            }
            if (date < today) {
                button.classList.add("past");
                button.disabled = true;
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

        previous.addEventListener("click", function (event) {
            event.preventDefault();
            event.stopPropagation();
            renderCalendar(field, depart, ret, new Date(month.getFullYear(), month.getMonth() - 1, 1));
        });
        next.addEventListener("click", function (event) {
            event.preventDefault();
            event.stopPropagation();
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

    function bindRouteSwap(root) {
        var button = root.querySelector("[data-route-swap]");
        if (!button || button.dataset.boundSwap === "true") {
            return;
        }

        button.dataset.boundSwap = "true";
        button.addEventListener("click", function () {
            var origin = root.querySelector("[data-location-input='origin']");
            var destination = root.querySelector("[data-location-input='destination']");
            var originType = root.querySelector("[data-location-type='origin']");
            var destinationType = root.querySelector("[data-location-type='destination']");
            var originCode = root.querySelector("[data-location-code='origin']");
            var destinationCode = root.querySelector("[data-location-code='destination']");
            var originValue = origin.value;
            var originTypeValue = originType.value;
            var originCodeValue = originCode.value;
            origin.value = destination.value;
            destination.value = originValue;
            originType.value = destinationType.value;
            destinationType.value = originTypeValue;
            originCode.value = destinationCode.value;
            destinationCode.value = originCodeValue;
        });
    }

    function bindTripMode(root) {
        var group = document.querySelector("[data-trip-mode-group]");
        var hidden = root.querySelector("[data-trip-type-value]");
        var returnField = root.querySelector("[data-return-field]");
        var returnInput = root.querySelector("[data-flight-date='return']");
        if (!group || !hidden || group.dataset.boundTripMode === "true") {
            return;
        }

        function setTripType(value) {
            var isOneWay = value === "OneWay";
            hidden.value = isOneWay ? "OneWay" : "Return";
            group.querySelectorAll("[data-trip-mode-option]").forEach(function (button) {
                button.classList.toggle("active", button.getAttribute("data-trip-mode-option") === hidden.value);
            });
            if (returnField) {
                returnField.hidden = isOneWay;
            }
            if (returnInput) {
                returnInput.disabled = isOneWay;
            }
        }

        group.dataset.boundTripMode = "true";
        group.querySelectorAll("[data-trip-mode-option]").forEach(function (button) {
            button.addEventListener("click", function () {
                setTripType(button.getAttribute("data-trip-mode-option"));
            });
        });
        setTripType(hidden.value === "OneWay" ? "OneWay" : "Return");
    }

    function bindCurrencyCombobox(root) {
        var box = root.querySelector("[data-currency-combobox]");
        if (!box || box.dataset.boundCurrency === "true") {
            return;
        }

        box.dataset.boundCurrency = "true";
        var hidden = box.querySelector("[data-currency-value]");
        var toggle = box.querySelector("[data-currency-toggle]");
        var menu = box.querySelector("[data-currency-menu]");
        var search = box.querySelector("[data-currency-search]");
        var options = box.querySelector("[data-currency-options]");
        var symbol = box.querySelector("[data-currency-symbol]");
        var code = box.querySelector("[data-currency-code]");

        function selectCurrency(currency) {
            hidden.value = currency.code;
            symbol.textContent = currency.symbol;
            code.textContent = currency.code;
            menu.hidden = true;
        }

        function render(filter) {
            var query = (filter || "").trim().toLowerCase();
            options.replaceChildren();
            if (query.length === 0) {
                return;
            }

            currencies
                .filter(function (currency) {
                    return currency.code.toLowerCase().includes(query) ||
                        currency.name.toLowerCase().includes(query) ||
                        currency.symbol.toLowerCase().includes(query);
                })
                .slice(0, 12)
                .forEach(function (currency) {
                    var item = document.createElement("button");
                    item.type = "button";
                    item.className = "currency-option";
                    item.innerHTML = "<span></span><strong></strong><small></small>";
                    item.querySelector("span").textContent = currency.symbol;
                    item.querySelector("strong").textContent = currency.code;
                    item.querySelector("small").textContent = currency.name;
                    item.addEventListener("click", function () {
                        selectCurrency(currency);
                    });
                    options.appendChild(item);
                });
        }

        toggle.addEventListener("click", function () {
            menu.hidden = !menu.hidden;
            search.value = "";
            options.replaceChildren();
            if (!menu.hidden) {
                search.focus();
            }
        });

        search.addEventListener("input", function () {
            render(search.value);
        });

        document.addEventListener("click", function (event) {
            if (!box.contains(event.target)) {
                menu.hidden = true;
            }
        });
    }

    function bindAlertTargets(root) {
        var stack = root.querySelector("[data-alert-targets]");
        if (!stack || stack.dataset.boundTargets === "true") {
            return;
        }

        stack.dataset.boundTargets = "true";
        var addButton = stack.querySelector("[data-target-add]");
        var filters = Array.prototype.slice.call(stack.querySelectorAll("[data-target-filter]"));

        function visibleFilters() {
            return filters.filter(function (filter) {
                return !filter.hidden;
            });
        }

        function syncOptions() {
            var visible = visibleFilters();
            var selected = visible.map(function (filter) {
                return filter.querySelector("select").value;
            });

            visible.forEach(function (filter) {
                var select = filter.querySelector("select");
                Array.prototype.forEach.call(select.options, function (option) {
                    option.disabled = option.value !== select.value && selected.indexOf(option.value) >= 0;
                });
            });

            filters.forEach(function (filter) {
                var remove = filter.querySelector("[data-target-remove]");
                if (remove) {
                    remove.hidden = visible.length <= 1 || filter.hidden;
                }
            });

            if (addButton) {
                addButton.hidden = visible.length >= filters.length;
            }
        }

        function activateFilter(filter) {
            filter.hidden = false;
            var select = filter.querySelector("select");
            var used = visibleFilters().map(function (item) {
                return item === filter ? "" : item.querySelector("select").value;
            });
            Array.prototype.some.call(select.options, function (option) {
                if (used.indexOf(option.value) < 0) {
                    select.value = option.value;
                    return true;
                }

                return false;
            });
            filter.querySelector("input").focus();
            syncOptions();
        }

        if (addButton) {
            addButton.addEventListener("click", function () {
                var hidden = filters.find(function (filter) {
                    return filter.hidden;
                });
                if (hidden) {
                    activateFilter(hidden);
                }
            });
        }

        filters.forEach(function (filter) {
            var select = filter.querySelector("select");
            var input = filter.querySelector("input");
            var remove = filter.querySelector("[data-target-remove]");

            select.addEventListener("change", syncOptions);
            if (remove) {
                remove.addEventListener("click", function () {
                    if (visibleFilters().length <= 1) {
                        return;
                    }

                    input.value = "";
                    filter.hidden = true;
                    syncOptions();
                });
            }
        });

        syncOptions();
    }

    function createOption(item, input, typeInput, codeInput, menu) {
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
            codeInput.value = item.code || "";
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
            var codeInput = root.querySelector("[data-location-code='" + name + "']");
            var controller = null;

            function close() {
                window.setTimeout(function () {
                    menu.hidden = true;
                }, 120);
            }

            input.addEventListener("blur", close);
            input.addEventListener("input", function () {
                var query = input.value.trim();
                codeInput.value = "";
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
                            menu.appendChild(createOption(item, input, typeInput, codeInput, menu));
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

    function formatDurationValue(value) {
        if (!value) {
            return "";
        }

        if (typeof value === "number") {
            var totalMinutes = Math.round(value / 600000000);
            return Math.floor(totalMinutes / 60) + " h " + (totalMinutes % 60) + " m";
        }

        var text = String(value);
        var days = 0;
        var dayMatch = /^(\d+)\.(.+)$/.exec(text);
        if (dayMatch) {
            days = Number(dayMatch[1]) || 0;
            text = dayMatch[2];
        }

        var pieces = text.split(":").map(function (piece) { return Number(piece) || 0; });
        var hours = (days * 24) + (pieces[0] || 0);
        var minutes = pieces[1] || 0;
        return hours + " h " + minutes + " m";
    }

    function formatSegmentTime(value) {
        if (!value) {
            return "--:--";
        }

        var date = new Date(value);
        if (Number.isNaN(date.getTime())) {
            return "--:--";
        }

        return date.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
    }

    function airportLabel(airport) {
        if (!airport) {
            return "";
        }

        return airport.code ? airport.name + " (" + airport.code + ")" : airport.name;
    }

    function kilogramsFromGrams(grams) {
        return Math.max(0, Math.round(Number(grams || 0) / 1000)).toLocaleString();
    }

    function escapeHtml(value) {
        return String(value || "")
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#39;");
    }

    function renderReturnLeg(details, offer) {
        var leg = offer && offer.itineraryLegs && offer.itineraryLegs.length ? offer.itineraryLegs[0] : null;
        if (!leg) {
            details.innerHTML = '<div class="flight-leg-heading"><div><strong>' +
                escapeHtml(details.closest("[data-flight-result]").dataset.returnTitle) +
                '</strong><span>' + escapeHtml(details.closest("[data-flight-result]").dataset.errorText) + '</span></div></div>';
            return;
        }

        var result = details.closest("[data-flight-result]");
        var durationLabel = result.dataset.durationLabel || "Travel time";
        var layoverLabel = result.dataset.layoverLabel || "Layover";
        var co2Label = result.dataset.co2Label || "kg CO2e";
        var html = '<div class="flight-leg-heading"><div><strong>' +
            escapeHtml(result.dataset.returnTitle || "Return flight") +
            '</strong><span>' + escapeHtml(leg.date || "") + '</span></div><span>' +
            escapeHtml(durationLabel) + ': ' + escapeHtml(formatDurationValue(leg.duration)) + '</span></div><div class="flight-timeline">';

        (leg.segments || []).forEach(function (segment, index) {
            html += '<div class="flight-segment"><div class="timeline-rail"><span></span><i></i><span></span></div><div class="segment-main">' +
                '<div class="segment-airports"><strong>' + formatSegmentTime(segment.departureAirport && segment.departureAirport.time) +
                '</strong><span>' + escapeHtml(airportLabel(segment.departureAirport)) +
                '</span><strong>' + formatSegmentTime(segment.arrivalAirport && segment.arrivalAirport.time) +
                '</strong><span>' + escapeHtml(airportLabel(segment.arrivalAirport)) + '</span></div>' +
                '<div class="segment-meta"><span>' + escapeHtml(formatDurationValue(segment.duration)) + '</span><span>' + escapeHtml(segment.airline || "") + '</span>';

            [segment.travelClass, segment.airplane, segment.flightNumber].forEach(function (value) {
                if (value) {
                    html += '<span>' + escapeHtml(value) + '</span>';
                }
            });
            html += '</div>';

            if (segment.extensions && segment.extensions.length) {
                html += '<div class="flight-tags">';
                segment.extensions.slice(0, 4).forEach(function (item) {
                    html += '<span>' + escapeHtml(item) + '</span>';
                });
                html += '</div>';
            }

            html += '</div></div>';
            if (leg.layovers && leg.layovers[index]) {
                var layover = leg.layovers[index];
                html += '<div class="layover-row"><span>' + escapeHtml(layoverLabel) + ': ' + escapeHtml(formatDurationValue(layover.duration)) +
                    '</span><strong>' + escapeHtml(layover.name || "") + escapeHtml(layover.code ? " (" + layover.code + ")" : "") + '</strong></div>';
            }
        });

        html += '</div>';
        if (offer.carbonEmissionsGrams) {
            html += '<div class="flight-detail-side"><span>' +
                escapeHtml(result.dataset.emissionsLabel || "Emissions estimate") + ': ' +
                escapeHtml(kilogramsFromGrams(offer.carbonEmissionsGrams)) + ' ' + escapeHtml(co2Label) + '</span></div>';
        }

        details.innerHTML = html;
    }

    function bindFlightResultDetails() {
        document.querySelectorAll("[data-flight-result]").forEach(function (result) {
            if (result.dataset.boundFlightResult === "true") {
                return;
            }

            result.dataset.boundFlightResult = "true";
            result.addEventListener("toggle", function () {
                var token = result.getAttribute("data-return-token");
                var target = result.querySelector("[data-return-details]");
                if (!result.open || !target || !token || target.dataset.loaded === "true" || target.dataset.loading === "true") {
                    return;
                }

                target.dataset.loading = "true";
                target.querySelector(".flight-leg-heading span").textContent = result.dataset.loadingText || "Loading return flight";
                fetch("/api/flights/return-details?departureToken=" + encodeURIComponent(token) +
                    "&currency=" + encodeURIComponent(result.getAttribute("data-currency") || "MAD"), {
                    headers: { "Accept": "application/json" }
                })
                    .then(function (response) {
                        if (!response.ok) {
                            throw new Error("Return details request failed");
                        }
                        return response.json();
                    })
                    .then(function (offer) {
                        target.dataset.loaded = "true";
                        renderReturnLeg(target, offer);
                    })
                    .catch(function () {
                        target.innerHTML = '<div class="flight-leg-heading"><div><strong>' +
                            escapeHtml(result.dataset.returnTitle || "Return flight") + '</strong><span>' +
                            escapeHtml(result.dataset.errorText || "Return flight details unavailable") + '</span></div></div>';
                    })
                    .finally(function () {
                        target.dataset.loading = "false";
                    });
            });
        });
    }

    function bindSearchForm() {
        var root = document.querySelector(".search-native-form");
        if (!root) {
            bindFlightResultDetails();
            return;
        }

        bindRouteSwap(root);
        bindTripMode(root);
        bindDatePair(root);
        bindLocationAutocomplete(root);
        bindTravellerPanel(root);
        bindCurrencyCombobox(root);
        bindAlertTargets(root);
        bindFlightResultDetails();
    }

    function scheduleSearchFormBinding() {
        bindSearchForm();

        var attempts = 0;
        var retry = window.setInterval(function () {
            attempts++;
            bindSearchForm();

            var depart = document.querySelector("[data-flight-date='depart']");
            if (attempts >= 30 || (depart && depart.dataset.boundFlightDates === "true")) {
                window.clearInterval(retry);
            }
        }, 150);
    }

    window.flightScannerBindSearchForm = scheduleSearchFormBinding;

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", scheduleSearchFormBinding);
    } else {
        scheduleSearchFormBinding();
    }

    window.addEventListener("load", scheduleSearchFormBinding);
    window.addEventListener("pageshow", scheduleSearchFormBinding);
    document.addEventListener("enhancedload", scheduleSearchFormBinding);

    if (document.body && "MutationObserver" in window) {
        var observerTimeout = null;
        var observer = new MutationObserver(function () {
            window.clearTimeout(observerTimeout);
            observerTimeout = window.setTimeout(bindSearchForm, 50);
        });
        observer.observe(document.body, { childList: true, subtree: true });
    }
})();
