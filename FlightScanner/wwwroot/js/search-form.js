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
    var routeLocationFallbacks = {
        "AF": { label: "Africa", lat: 1.65, lng: 17.8 },
        "AFRICA": { label: "Africa", lat: 1.65, lng: 17.8 },
        "AN": { label: "Antarctica", lat: -82.8, lng: 0 },
        "ANTARCTICA": { label: "Antarctica", lat: -82.8, lng: 0 },
        "AS": { label: "Asia", lat: 34, lng: 100 },
        "ASIA": { label: "Asia", lat: 34, lng: 100 },
        "EU": { label: "Europe", lat: 54.5, lng: 15 },
        "EUROPE": { label: "Europe", lat: 54.5, lng: 15 },
        "NA": { label: "North America", lat: 48.2, lng: -100 },
        "NORTH AMERICA": { label: "North America", lat: 48.2, lng: -100 },
        "OC": { label: "Oceania", lat: -25, lng: 134 },
        "OCEANIA": { label: "Oceania", lat: -25, lng: 134 },
        "SA": { label: "South America", lat: -14.6, lng: -58.4 },
        "SOUTH AMERICA": { label: "South America", lat: -14.6, lng: -58.4 },
        "JFK": { label: "JFK", lat: 40.6413, lng: -73.7781 },
        "LAX": { label: "LAX", lat: 33.9416, lng: -118.4085 },
        "YYZ": { label: "YYZ", lat: 43.6777, lng: -79.6248 },
        "LHR": { label: "LHR", lat: 51.47, lng: -0.4543 },
        "CDG": { label: "CDG", lat: 49.0097, lng: 2.5479 },
        "AMS": { label: "AMS", lat: 52.3105, lng: 4.7683 },
        "CMN": { label: "CMN", lat: 33.3675, lng: -7.5898 },
        "CAI": { label: "CAI", lat: 30.112, lng: 31.4 },
        "JNB": { label: "JNB", lat: -26.1337, lng: 28.242 },
        "DXB": { label: "DXB", lat: 25.2532, lng: 55.3657 },
        "SIN": { label: "SIN", lat: 1.3644, lng: 103.9915 },
        "HND": { label: "HND", lat: 35.5494, lng: 139.7798 },
        "SYD": { label: "SYD", lat: -33.9399, lng: 151.1753 },
        "AKL": { label: "AKL", lat: -37.0082, lng: 174.785 },
        "GRU": { label: "GRU", lat: -23.4356, lng: -46.4731 },
        "EZE": { label: "EZE", lat: -34.8222, lng: -58.5358 },
        "NEW YORK": { label: "New York", lat: 40.7128, lng: -74.006 },
        "LONDON": { label: "London", lat: 51.5072, lng: -0.1276 },
        "PARIS": { label: "Paris", lat: 48.8566, lng: 2.3522 },
        "CASABLANCA": { label: "Casablanca", lat: 33.5731, lng: -7.5898 },
        "MOROCCO": { label: "Morocco", lat: 31.8, lng: -7.1 },
        "FRANCE": { label: "France", lat: 46.2, lng: 2.2 },
        "UNITED KINGDOM": { label: "United Kingdom", lat: 54.2, lng: -2.7 },
        "UNITED STATES": { label: "United States", lat: 39.8, lng: -98.6 },
        "CANADA": { label: "Canada", lat: 56.1, lng: -106.3 },
        "EGYPT": { label: "Egypt", lat: 26.8, lng: 30.8 },
        "SOUTH AFRICA": { label: "South Africa", lat: -30.6, lng: 22.9 }
    };

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
            var originValue = origin.value;
            var originTypeValue = originType.value;
            var originLat = origin.dataset.locationLat || "";
            var originLng = origin.dataset.locationLng || "";
            var originLabel = origin.dataset.locationLabel || "";
            origin.value = destination.value;
            destination.value = originValue;
            originType.value = destinationType.value;
            destinationType.value = originTypeValue;
            origin.dataset.locationLat = destination.dataset.locationLat || "";
            origin.dataset.locationLng = destination.dataset.locationLng || "";
            origin.dataset.locationLabel = destination.dataset.locationLabel || "";
            destination.dataset.locationLat = originLat;
            destination.dataset.locationLng = originLng;
            destination.dataset.locationLabel = originLabel;
            updateRouteMap(root);
        });
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

    function createOption(item, input, typeInput, menu, root) {
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
            input.dataset.locationLat = item.latitude == null ? "" : String(item.latitude);
            input.dataset.locationLng = item.longitude == null ? "" : String(item.longitude);
            input.dataset.locationLabel = item.primary || item.value;
            menu.hidden = true;
            updateRouteMap(root);
        });
        return button;
    }

    function normalizeRouteKey(value) {
        return (value || "")
            .split(" - ")[0]
            .split("·")[0]
            .trim()
            .normalize("NFD")
            .replace(/[\u0300-\u036f]/g, "")
            .toUpperCase();
    }

    function readDatasetCoordinate(input) {
        var lat = Number(input.dataset.locationLat);
        var lng = Number(input.dataset.locationLng);
        if (Number.isFinite(lat) && Number.isFinite(lng)) {
            return {
                label: input.dataset.locationLabel || input.value.trim(),
                lat: lat,
                lng: lng
            };
        }

        return null;
    }

    function resolveRouteLocation(input) {
        if (!input || !input.value.trim()) {
            return null;
        }

        var dataset = readDatasetCoordinate(input);
        if (dataset) {
            return dataset;
        }

        var key = normalizeRouteKey(input.value);
        var fallback = routeLocationFallbacks[key];
        if (fallback) {
            return fallback;
        }

        if (key.length > 3) {
            fallback = routeLocationFallbacks[key.slice(0, 3)];
            if (fallback) {
                return fallback;
            }
        }

        return null;
    }

    function projectRoutePoint(location) {
        var lng = Math.max(-180, Math.min(180, location.lng));
        var lat = Math.max(-85, Math.min(85, location.lat));
        return {
            x: 40 + ((lng + 180) / 360) * 880,
            y: 28 + ((85 - lat) / 170) * 364
        };
    }

    function setPin(pin, label, point, value) {
        pin.hidden = false;
        pin.setAttribute("transform", "translate(" + point.x.toFixed(1) + " " + point.y.toFixed(1) + ")");
        label.textContent = (value || "").slice(0, 22);
    }

    function midpointOnCurve(start, control, end, t) {
        var inverse = 1 - t;
        return {
            x: inverse * inverse * start.x + 2 * inverse * t * control.x + t * t * end.x,
            y: inverse * inverse * start.y + 2 * inverse * t * control.y + t * t * end.y
        };
    }

    function updateRouteMap(root) {
        var panel = root.querySelector("[data-route-map]");
        if (!panel) {
            return;
        }

        var originInput = root.querySelector("[data-location-input='origin']");
        var destinationInput = root.querySelector("[data-location-input='destination']");
        var title = panel.querySelector("[data-route-map-title]");
        var line = panel.querySelector("[data-route-line]");
        var plane = panel.querySelector("[data-route-plane]");
        var originPin = panel.querySelector("[data-route-pin='origin']");
        var destinationPin = panel.querySelector("[data-route-pin='destination']");
        var originLabel = panel.querySelector("[data-route-label='origin']");
        var destinationLabel = panel.querySelector("[data-route-label='destination']");
        var origin = resolveRouteLocation(originInput);
        var destination = resolveRouteLocation(destinationInput);

        originPin.hidden = true;
        destinationPin.hidden = true;
        line.hidden = true;
        plane.hidden = true;

        if (origin) {
            setPin(originPin, originLabel, projectRoutePoint(origin), origin.label || originInput.value);
        }

        if (destination) {
            setPin(destinationPin, destinationLabel, projectRoutePoint(destination), destination.label || destinationInput.value);
        }

        if (origin && destination) {
            var start = projectRoutePoint(origin);
            var end = projectRoutePoint(destination);
            var distance = Math.hypot(end.x - start.x, end.y - start.y);
            var lift = Math.max(46, Math.min(132, distance * 0.22));
            var control = {
                x: (start.x + end.x) / 2,
                y: Math.min(start.y, end.y) - lift
            };
            var middle = midpointOnCurve(start, control, end, 0.5);
            var angle = Math.atan2(end.y - start.y, end.x - start.x) * 180 / Math.PI;
            line.setAttribute("d", "M" + start.x.toFixed(1) + " " + start.y.toFixed(1) + " Q" + control.x.toFixed(1) + " " + control.y.toFixed(1) + " " + end.x.toFixed(1) + " " + end.y.toFixed(1));
            line.hidden = false;
            plane.setAttribute("transform", "translate(" + middle.x.toFixed(1) + " " + middle.y.toFixed(1) + ") rotate(" + angle.toFixed(1) + ")");
            plane.hidden = false;
            title.textContent = (origin.label || originInput.value) + " → " + (destination.label || destinationInput.value);
        } else if (origin) {
            title.textContent = origin.label || originInput.value;
        } else if (destination) {
            title.textContent = destination.label || destinationInput.value;
        } else {
            title.textContent = title.getAttribute("data-default-title") || title.textContent;
            if (!title.getAttribute("data-default-title")) {
                title.setAttribute("data-default-title", title.textContent);
            }
        }
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
                input.dataset.locationLat = "";
                input.dataset.locationLng = "";
                input.dataset.locationLabel = "";
                updateRouteMap(root);
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
                            menu.appendChild(createOption(item, input, typeInput, menu, root));
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

    function bindRouteMap(root) {
        var title = root.querySelector("[data-route-map-title]");
        if (title && !title.getAttribute("data-default-title")) {
            title.setAttribute("data-default-title", title.textContent);
        }

        if (root.dataset.boundRouteMap === "true") {
            updateRouteMap(root);
            return;
        }

        root.dataset.boundRouteMap = "true";
        root.querySelectorAll("[data-location-input]").forEach(function (input) {
            input.addEventListener("change", function () {
                updateRouteMap(root);
            });
            input.addEventListener("blur", function () {
                updateRouteMap(root);
            });
        });
        updateRouteMap(root);
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

        bindRouteSwap(root);
        bindDatePair(root);
        bindLocationAutocomplete(root);
        bindRouteMap(root);
        bindTravellerPanel(root);
        bindCurrencyCombobox(root);
        bindAlertTargets(root);
    }

    document.addEventListener("DOMContentLoaded", bindSearchForm);
    document.addEventListener("enhancedload", bindSearchForm);
})();
