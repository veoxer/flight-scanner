(function () {
    var searchStateKey = "flightscanner.search.filters";
    var searchResultsKey = "flightscanner.search.results";

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

    function parseTimeHourValue(value) {
        var match = /^(\d{2}):/.exec(value || "");
        return match ? Number(match[1]) : null;
    }

    function bindTimeRanges(root) {
        var ranges = {};
        root.querySelectorAll("[data-time-range][data-time-bound]").forEach(function (select) {
            var range = select.getAttribute("data-time-range");
            var bound = select.getAttribute("data-time-bound");
            ranges[range] = ranges[range] || {};
            ranges[range][bound] = select;
        });

        Object.keys(ranges).forEach(function (range) {
            var start = ranges[range].start;
            var end = ranges[range].end;
            if (!start || !end || start.dataset.boundTimeRange === "true") {
                return;
            }

            function sync() {
                var startHour = parseTimeHourValue(start.value);
                var endHour = parseTimeHourValue(end.value);

                Array.prototype.forEach.call(end.options, function (option) {
                    var hour = parseTimeHourValue(option.value);
                    option.disabled = hour !== null && startHour !== null && hour <= startHour;
                });

                Array.prototype.forEach.call(start.options, function (option) {
                    var hour = parseTimeHourValue(option.value);
                    option.disabled = hour !== null && endHour !== null && hour >= endHour;
                });

                if (end.value && end.selectedOptions.length && end.selectedOptions[0].disabled) {
                    end.value = "";
                }
                if (start.value && start.selectedOptions.length && start.selectedOptions[0].disabled) {
                    start.value = "";
                }

                start.dispatchEvent(new Event("customselectrefresh"));
                end.dispatchEvent(new Event("customselectrefresh"));
            }

            start.dataset.boundTimeRange = "true";
            end.dataset.boundTimeRange = "true";
            start.addEventListener("change", sync);
            end.addEventListener("change", sync);
            sync();
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
        var returnTimeField = root.querySelector("[data-return-time-field]");
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
            if (returnTimeField) {
                returnTimeField.hidden = isOneWay;
                returnTimeField.querySelectorAll("input").forEach(function (input) {
                    input.disabled = isOneWay;
                });
            }
            syncDateMode(root);
        }

        group.dataset.boundTripMode = "true";
        group.querySelectorAll("[data-trip-mode-option]").forEach(function (button) {
            button.addEventListener("click", function () {
                setTripType(button.getAttribute("data-trip-mode-option"));
            });
        });
        setTripType(hidden.value === "OneWay" ? "OneWay" : "Return");
    }

    function syncDateMode(root) {
        var hidden = root.querySelector("[data-date-mode-value]");
        if (!hidden) {
            return;
        }

        var isFlexible = hidden.value === "Flexible";
        var tripType = root.querySelector("[data-trip-type-value]");
        var isOneWay = tripType && tripType.value === "OneWay";
        root.querySelectorAll("[data-date-mode-option]").forEach(function (button) {
            button.classList.toggle("active", button.getAttribute("data-date-mode-option") === hidden.value);
        });

        root.querySelectorAll("[data-specific-date-field]").forEach(function (field) {
            field.hidden = isFlexible || (field.hasAttribute("data-return-field") && isOneWay);
            field.querySelectorAll("input, select, button").forEach(function (input) {
                if (input.hasAttribute("data-date-toggle")) {
                    input.disabled = isFlexible || (field.hasAttribute("data-return-field") && isOneWay);
                } else {
                    input.disabled = isFlexible || (input.getAttribute("data-flight-date") === "return" && isOneWay);
                }
            });
        });

        root.querySelectorAll("[data-flexible-date-field]").forEach(function (field) {
            var isStay = field.hasAttribute("data-flexible-stay-field");
            var isReturnOnly = field.hasAttribute("data-flexible-return-only-field");
            field.hidden = !isFlexible || ((isStay || isReturnOnly) && isOneWay);
            field.querySelectorAll("input, select, button").forEach(function (input) {
                input.disabled = !isFlexible || ((isStay || isReturnOnly) && isOneWay);
            });
        });

        refreshCustomSelects(root);
    }

    function bindDateMode(root) {
        var group = root.querySelector("[data-date-mode-group]");
        var hidden = root.querySelector("[data-date-mode-value]");
        if (!group || !hidden || group.dataset.boundDateMode === "true") {
            syncDateMode(root);
            return;
        }

        group.dataset.boundDateMode = "true";
        group.querySelectorAll("[data-date-mode-option]").forEach(function (button) {
            button.addEventListener("click", function () {
                hidden.value = button.getAttribute("data-date-mode-option") === "Flexible" ? "Flexible" : "Specific";
                syncDateMode(root);
            });
        });
        hidden.value = hidden.value === "Flexible" ? "Flexible" : "Specific";
        syncDateMode(root);
    }

    function refreshCustomSelects(root) {
        root.querySelectorAll("select[data-custom-select]").forEach(function (select) {
            select.dispatchEvent(new Event("customselectrefresh"));
        });
    }

    function closeCustomSelects(except) {
        document.querySelectorAll(".custom-select-shell.open").forEach(function (shell) {
            if (shell !== except) {
                shell.classList.remove("open");
                var menu = shell.querySelector(".custom-select-menu");
                if (menu) {
                    menu.hidden = true;
                }
            }
        });
    }

    function bindCustomSelects(root) {
        root.querySelectorAll("select[data-custom-select]").forEach(function (select) {
            if (select.dataset.boundCustomSelect === "true") {
                select.dispatchEvent(new Event("customselectrefresh"));
                return;
            }

            select.dataset.boundCustomSelect = "true";
            select.classList.add("native-select-hidden");

            var shell = document.createElement("div");
            shell.className = "custom-select-shell";
            var button = document.createElement("button");
            button.type = "button";
            button.className = "custom-select-button";
            button.innerHTML = "<span></span><i aria-hidden=\"true\">⌄</i>";
            var menu = document.createElement("div");
            menu.className = "custom-select-menu";
            menu.hidden = true;
            var search = null;
            var list = document.createElement("div");
            list.className = "custom-select-options";

            if (select.dataset.customSelectSearch === "true" || select.options.length > 6) {
                search = document.createElement("input");
                search.type = "search";
                search.className = "custom-select-search";
                search.placeholder = "Search";
                menu.appendChild(search);
            }

            menu.appendChild(list);
            shell.append(button, menu);
            select.insertAdjacentElement("afterend", shell);

            function selectedOption() {
                return select.selectedOptions && select.selectedOptions.length
                    ? select.selectedOptions[0]
                    : select.options[select.selectedIndex];
            }

            function updateButton() {
                var option = selectedOption();
                button.querySelector("span").textContent = option ? option.textContent : "";
                button.disabled = select.disabled;
                shell.classList.toggle("disabled", select.disabled);
            }

            function renderOptions(filter) {
                var query = (filter || "").trim().toLowerCase();
                list.replaceChildren();
                Array.prototype.forEach.call(select.options, function (option) {
                    if (query && !option.textContent.toLowerCase().includes(query) && !option.value.toLowerCase().includes(query)) {
                        return;
                    }

                    var item = document.createElement("button");
                    item.type = "button";
                    item.className = "custom-select-option";
                    item.disabled = option.disabled;
                    item.dataset.value = option.value;
                    item.innerHTML = "<span></span>";
                    item.querySelector("span").textContent = option.textContent;
                    item.classList.toggle("selected", option.value === select.value);
                    function chooseOption(event) {
                        event.preventDefault();
                        event.stopPropagation();
                        if (option.disabled) {
                            return;
                        }

                        option.selected = true;
                        select.value = option.value;
                        renderOptions(search ? search.value : "");
                        select.dispatchEvent(new Event("change", { bubbles: true }));
                        select.dispatchEvent(new Event("input", { bubbles: true }));
                        updateButton();
                        shell.classList.remove("open");
                        menu.hidden = true;
                    }

                    item.addEventListener("pointerdown", chooseOption);
                    item.addEventListener("click", function (event) {
                        if (event.detail !== 0) {
                            event.preventDefault();
                            event.stopPropagation();
                            return;
                        }

                        chooseOption(event);
                    });
                    list.appendChild(item);
                });
            }

            function openMenu() {
                if (select.disabled) {
                    return;
                }

                closeCustomSelects(shell);
                renderOptions(search ? search.value : "");
                shell.classList.add("open");
                menu.hidden = false;
                if (search) {
                    search.value = "";
                    renderOptions("");
                    search.focus();
                }
            }

            button.addEventListener("click", function (event) {
                event.preventDefault();
                event.stopPropagation();
                if (menu.hidden) {
                    openMenu();
                } else {
                    shell.classList.remove("open");
                    menu.hidden = true;
                }
            });

            if (search) {
                search.addEventListener("click", function (event) {
                    event.stopPropagation();
                });
                search.addEventListener("input", function () {
                    renderOptions(search.value);
                });
            }

            select.addEventListener("change", updateButton);
            select.addEventListener("customselectrefresh", function () {
                updateButton();
                if (!menu.hidden) {
                    renderOptions(search ? search.value : "");
                }
            });

            updateButton();
        });

        if (document.body.dataset.boundCustomSelectClose !== "true") {
            document.body.dataset.boundCustomSelectClose = "true";
            document.addEventListener("click", function (event) {
                if (!event.target.closest(".custom-select-shell")) {
                    closeCustomSelects();
                }
            });
        }
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
        var countInput = stack.querySelector("[data-target-filter-count]");
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
                select.dispatchEvent(new Event("customselectrefresh"));
            });

            filters.forEach(function (filter) {
                var input = filter.querySelector("input");
                if (input) {
                    input.setCustomValidity("");
                }
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
            if (countInput) {
                countInput.value = String(visible.length);
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
            input.addEventListener("input", function () {
                input.setCustomValidity("");
            });
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

    function clearAlertTargetValidity(root) {
        root.querySelectorAll("[data-target-filter] input").forEach(function (input) {
            input.setCustomValidity("");
        });
    }

    function validateAlertTargets(root) {
        var stack = root.querySelector("[data-alert-targets]");
        if (!stack) {
            return true;
        }

        var message = stack.getAttribute("data-alert-target-required") || "Enter a target price before saving an alert.";
        var invalid = null;
        Array.prototype.slice.call(stack.querySelectorAll("[data-target-filter]")).forEach(function (filter) {
            if (filter.hidden || invalid) {
                return;
            }

            var input = filter.querySelector("input");
            var value = input ? Number(input.value) : 0;
            if (!input || !input.value || !Number.isFinite(value) || value <= 0) {
                invalid = input;
            }
        });

        if (!invalid) {
            return true;
        }

        invalid.setCustomValidity(message);
        invalid.reportValidity();
        return false;
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

    function returnOptionsUrl(result) {
        var token = result.getAttribute("data-return-token") || "";
        var departureId = result.getAttribute("data-return-departure-id") || "";
        var arrivalId = result.getAttribute("data-return-arrival-id") || "";
        var outboundDate = result.getAttribute("data-return-outbound-date") || "";
        var returnDate = result.getAttribute("data-return-date") || "";
        return "/api/flights/return-options?departureToken=" + encodeURIComponent(token) +
            "&currency=" + encodeURIComponent(result.getAttribute("data-currency") || "MAD") +
            "&departureId=" + encodeURIComponent(departureId) +
            "&arrivalId=" + encodeURIComponent(arrivalId) +
            "&outboundDate=" + encodeURIComponent(outboundDate) +
            "&returnDate=" + encodeURIComponent(returnDate);
    }

    function renderReturnOptionCard(offer, result) {
        var leg = offer && offer.itineraryLegs && offer.itineraryLegs.length ? offer.itineraryLegs[0] : null;
        var currency = offer.currency || result.getAttribute("data-currency") || "MAD";
        var symbol = result.getAttribute("data-price-symbol") || "";
        var route = leg && leg.segments && leg.segments.length
            ? airportLabel(leg.segments[0].departureAirport) + " to " + airportLabel(leg.segments[leg.segments.length - 1].arrivalAirport)
            : (offer.origin || "") + " to " + (offer.destination || "");
        var html = '<details class="result-card itinerary-result return-option-card" data-return-option><summary class="result-summary">' +
            '<div class="airline-identity"><span class="airline-logo-fallback">' + escapeHtml((offer.airline || "A").slice(0, 2).toUpperCase()) +
            '</span><span><strong>' +
            escapeHtml(offer.airline || result.dataset.returnTitle || "Return flight") +
            ' ' + escapeHtml(offer.flightNumber || "") +
            '</strong><span>' + escapeHtml(offer.provider || "") + '</span></span></div><div><strong>' +
            escapeHtml(symbol) + ' ' + escapeHtml(offer.price || "") + ' ' + escapeHtml(currency) +
            '</strong><span>' + escapeHtml(result.dataset.returnTitle || "Return flight") + '</span></div><div><strong>' +
            escapeHtml(offer.departDate || "") + '</strong><span>' + escapeHtml(route) +
            '</span></div><div><strong>' + escapeHtml(offer.stops || 0) + '</strong><span>' +
            escapeHtml(formatDurationValue(offer.duration)) + '</span></div><span class="expand-chevron" aria-hidden="true">⌄</span></summary>';

        if (!leg) {
            return html + '<div class="itinerary-details"><p>' + escapeHtml(result.dataset.errorText || "Return flight details unavailable") + '</p></div></details>';
        }

        html += '<div class="itinerary-details"><section class="flight-leg"><div class="flight-leg-heading"><div><strong>' +
            escapeHtml(result.dataset.returnTitle || "Return flight") +
            '</strong><span>' + escapeHtml(leg.date || "") + '</span></div><span>' +
            escapeHtml(result.dataset.durationLabel || "Travel time") + ': ' +
            escapeHtml(formatDurationValue(leg.duration)) + '</span></div><div class="flight-timeline">';

        (leg.segments || []).forEach(function (segment, index) {
            html += '<div class="flight-segment"><div class="timeline-rail"><span></span><i></i><span></span></div><div class="segment-main">' +
                '<div class="segment-airports"><strong>' + formatSegmentTime(segment.departureAirport && segment.departureAirport.time) +
                '</strong><span>' + escapeHtml(airportLabel(segment.departureAirport)) +
                '</span><strong>' + formatSegmentTime(segment.arrivalAirport && segment.arrivalAirport.time) +
                '</strong><span>' + escapeHtml(airportLabel(segment.arrivalAirport)) + '</span></div>' +
                '<div class="segment-meta"><span>' + escapeHtml(formatDurationValue(segment.duration)) +
                '</span><span>' + escapeHtml(segment.airline || "") + '</span>';

            [segment.travelClass, segment.airplane, segment.flightNumber].forEach(function (value) {
                if (value) {
                    html += '<span>' + escapeHtml(value) + '</span>';
                }
            });
            html += '</div></div></div>';

            if (leg.layovers && leg.layovers[index]) {
                var layover = leg.layovers[index];
                html += '<div class="layover-row"><span>' +
                    escapeHtml(result.dataset.layoverLabel || "Layover") + ': ' +
                    escapeHtml(formatDurationValue(layover.duration)) + '</span><strong>' +
                    escapeHtml(layover.name || "") + escapeHtml(layover.code ? " (" + layover.code + ")" : "") +
                    '</strong></div>';
            }
        });

        return html + '</div></section></div></details>';
    }

    function renderDepartureSnapshot(result) {
        var summary = result.querySelector(".result-summary");
        if (!summary) {
            return "";
        }

        var cells = Array.prototype.slice.call(summary.children).filter(function (child) {
            return !child.classList.contains("expand-chevron");
        });
        return '<div class="return-departure-grid">' + cells.slice(0, 4).map(function (cell) {
            return '<div>' + cell.innerHTML + '</div>';
        }).join("") + '</div>';
    }

    function bindReturnPagination(modal) {
        var source = modal.querySelector("[data-return-results-source]");
        var list = modal.querySelector("[data-return-modal-list]");
        var controls = modal.querySelector("[data-return-pagination]");
        if (!source || !list || !controls) {
            return;
        }

        controls.dataset.boundReturnPagination = "true";
        var pageSize = controls.querySelector("[data-return-page-size]");
        var previous = controls.querySelector("[data-return-prev]");
        var next = controls.querySelector("[data-return-next]");
        var status = controls.querySelector("[data-return-page-status]");
        var page = 1;

        function cards() {
            return Array.prototype.slice.call(list.querySelectorAll("[data-return-option]"));
        }

        function selectedPageSize() {
            if (!pageSize || pageSize.value === "all") {
                return Number.MAX_SAFE_INTEGER;
            }

            return Math.max(1, Number(pageSize.value) || 5);
        }

        function render() {
            var items = cards();
            var size = selectedPageSize();
            var pageCount = Math.max(1, Math.ceil(items.length / size));
            page = Math.min(Math.max(1, page), pageCount);
            var start = (page - 1) * size;
            var end = start + size;
            items.forEach(function (item, index) {
                item.hidden = !(index >= start && index < end);
            });
            controls.hidden = items.length <= 1;
            if (previous) {
                previous.disabled = page <= 1;
            }
            if (next) {
                next.disabled = page >= pageCount;
            }
            if (status) {
                status.textContent = page + " / " + pageCount;
            }
        }

        if (pageSize) {
            pageSize.onchange = function () {
                page = 1;
                render();
            };
        }
        if (previous) {
            previous.onclick = function () {
                page--;
                render();
            };
        }
        if (next) {
            next.onclick = function () {
                page++;
                render();
            };
        }

        bindCustomSelects(modal);
        render();
    }

    function openReturnOptionsModal(result) {
        var modal = document.querySelector("[data-return-modal]");
        if (!modal) {
            return;
        }

        var list = modal.querySelector("[data-return-modal-list]");
        var status = modal.querySelector("[data-return-modal-status]");
        var subtitle = modal.querySelector("[data-return-modal-subtitle]");
        var departure = modal.querySelector("[data-return-modal-departure]");
        var source = modal.querySelector("[data-return-results-source]");
        var token = result.getAttribute("data-return-token") || "";
        var returnDate = result.getAttribute("data-return-date") || "";
        if (!token || !returnDate) {
            return;
        }

        if (subtitle) {
            subtitle.textContent = result.getAttribute("data-return-arrival-id") + " · " + (returnDate || "");
        }
        if (departure) {
            departure.innerHTML = renderDepartureSnapshot(result);
        }
        if (list) {
            list.replaceChildren();
        }
        if (source) {
            source.hidden = true;
        }
        if (status) {
            status.hidden = false;
            status.innerHTML = '<span class="search-loading-spinner" aria-hidden="true"></span><strong>' +
                escapeHtml(result.dataset.loadingText || "Loading return flight") + '</strong>';
        }
        modal.hidden = false;
        document.body.classList.add("modal-open");

        fetch(returnOptionsUrl(result), { headers: { "Accept": "application/json" } })
            .then(function (response) {
                if (!response.ok) {
                    throw new Error("Return options request failed");
                }
                return response.json();
            })
            .then(function (offers) {
                if (!Array.isArray(offers) || offers.length === 0) {
                    throw new Error("No return options returned");
                }

                if (status) {
                    status.hidden = true;
                    status.textContent = "";
                }
                if (list) {
                    list.innerHTML = offers.map(function (offer) {
                        return renderReturnOptionCard(offer, result);
                    }).join("");
                }
                if (source) {
                    source.hidden = false;
                }
                bindReturnPagination(modal);
            })
            .catch(function () {
                if (status) {
                    status.hidden = false;
                    status.textContent = result.dataset.errorText || "Return flight details unavailable";
                }
                if (source) {
                    source.hidden = true;
                }
            });
    }

    function closeReturnOptionsModal() {
        var modal = document.querySelector("[data-return-modal]");
        if (!modal) {
            return;
        }

        modal.hidden = true;
        document.body.classList.remove("modal-open");
    }

    function bindFlightResultDetails() {
        document.querySelectorAll("[data-flight-result]").forEach(function (result) {
            if (result.dataset.boundFlightResult === "true") {
                return;
            }

            result.dataset.boundFlightResult = "true";
            result.addEventListener("click", function (event) {
                var summary = event.target.closest(".result-summary");
                if (!summary || event.target.closest(".expand-chevron")) {
                    return;
                }

                if (!result.getAttribute("data-return-token") || !result.getAttribute("data-return-date")) {
                    return;
                }

                event.preventDefault();
                openReturnOptionsModal(result);
            });
        });

        document.querySelectorAll("[data-return-modal-close]").forEach(function (button) {
            if (button.dataset.boundReturnModalClose === "true") {
                return;
            }

            button.dataset.boundReturnModalClose = "true";
            button.addEventListener("click", closeReturnOptionsModal);
        });

        if (document.body.dataset.boundReturnModalEscape !== "true") {
            document.body.dataset.boundReturnModalEscape = "true";
            document.addEventListener("keydown", function (event) {
                if (event.key === "Escape") {
                    closeReturnOptionsModal();
                }
            });
        }
    }

    function isAlertSaveNavigation() {
        return new URLSearchParams(window.location.search).get("saveAlert") === "true";
    }

    function cacheRenderedResults() {
        var source = document.querySelector("[data-results-cache-source]");
        if (!source) {
            if (!isAlertSaveNavigation()) {
                sessionStorage.removeItem(searchResultsKey);
            }
            return;
        }

        sessionStorage.setItem(searchResultsKey, source.innerHTML);
    }

    function restoreResultsAfterAlertSave() {
        if (!isAlertSaveNavigation()) {
            return;
        }

        var target = document.querySelector("[data-results-cache-target]");
        if (!target || target.querySelector("[data-results-cache-source]")) {
            return;
        }

        var html = sessionStorage.getItem(searchResultsKey);
        if (!html) {
            return;
        }

        var wrapper = document.createElement("div");
        wrapper.setAttribute("data-results-cache-source", "");
        wrapper.innerHTML = html;
        target.replaceChildren(wrapper);
        bindFlightResultDetails();
    }

    function bindResultPagination() {
        var source = document.querySelector("[data-results-cache-source]");
        var grid = source && source.querySelector("[data-results-grid]");
        var controls = source && source.querySelector("[data-result-pagination]");
        if (!source || !grid || !controls || controls.dataset.boundResultPagination === "true") {
            return;
        }

        controls.dataset.boundResultPagination = "true";
        var pageSize = controls.querySelector("[data-results-page-size]");
        var previous = controls.querySelector("[data-results-prev]");
        var next = controls.querySelector("[data-results-next]");
        var status = controls.querySelector("[data-results-page-status]");
        var tabs = source.querySelector("[data-result-tabs]");
        var activeKind = "Departure";
        var page = 1;

        function cards() {
            return Array.prototype.slice.call(grid.querySelectorAll("[data-flight-result]"));
        }

        function activeCards() {
            var items = cards();
            if (!tabs) {
                return items;
            }

            return items.filter(function (item) {
                return (item.dataset.resultKind || "Departure") === activeKind;
            });
        }

        function selectedPageSize() {
            if (!pageSize || pageSize.value === "all") {
                return Number.MAX_SAFE_INTEGER;
            }

            return Math.max(1, Number(pageSize.value) || 10);
        }

        function render() {
            var allItems = cards();
            var items = activeCards();
            var size = selectedPageSize();
            var pageCount = Math.max(1, Math.ceil(items.length / size));
            page = Math.min(Math.max(1, page), pageCount);
            var start = (page - 1) * size;
            var end = start + size;

            allItems.forEach(function (item) {
                item.hidden = true;
            });
            items.forEach(function (item, index) {
                item.hidden = !(index >= start && index < end);
            });

            controls.hidden = items.length <= 1;
            if (previous) {
                previous.disabled = page <= 1;
            }
            if (next) {
                next.disabled = page >= pageCount;
            }
            if (status) {
                status.textContent = page + " / " + pageCount;
            }
        }

        if (pageSize) {
            pageSize.addEventListener("change", function () {
                page = 1;
                render();
            });
        }
        if (previous) {
            previous.addEventListener("click", function () {
                page--;
                render();
            });
        }
        if (next) {
            next.addEventListener("click", function () {
                page++;
                render();
            });
        }
        if (tabs) {
            tabs.querySelectorAll("[data-result-tab]").forEach(function (button) {
                button.addEventListener("click", function () {
                    activeKind = button.getAttribute("data-result-tab") || "Departure";
                    page = 1;
                    tabs.querySelectorAll("[data-result-tab]").forEach(function (item) {
                        item.classList.toggle("active", item === button);
                    });
                    render();
                });
            });
        }

        bindCustomSelects(source);
        render();
    }

    function formState(root) {
        var data = {};
        new FormData(root).forEach(function (value, key) {
            if (key === "run" || key === "saveAlert") {
                return;
            }

            if (data[key]) {
                data[key] = data[key] + "," + value;
            } else {
                data[key] = value;
            }
        });
        return data;
    }

    function restoreFormState(root) {
        if (window.location.search || root.dataset.restoredSearchState === "true") {
            return;
        }

        var navigation = performance.getEntriesByType && performance.getEntriesByType("navigation")[0];
        if (navigation && navigation.type === "reload") {
            sessionStorage.removeItem(searchStateKey);
            return;
        }

        var raw = sessionStorage.getItem(searchStateKey);
        if (!raw) {
            return;
        }

        try {
            var data = JSON.parse(raw);
            Object.keys(data).forEach(function (key) {
                root.querySelectorAll("[name='" + CSS.escape(key) + "']").forEach(function (field) {
                    if (field.type === "checkbox") {
                        field.checked = String(data[key]).split(",").indexOf(field.value) >= 0;
                    } else {
                        field.value = data[key];
                    }
                });
            });
            root.dataset.restoredSearchState = "true";
            bindTripMode(root);
            syncDateMode(root);
            bindCustomSelects(root);
            bindCurrencyCombobox(root);
        } catch {
            sessionStorage.removeItem(searchStateKey);
        }
    }

    function saveFormState(root) {
        sessionStorage.setItem(searchStateKey, JSON.stringify(formState(root)));
    }

    function bindSearchSubmit(root) {
        if (root.dataset.boundSearchSubmit === "true") {
            return;
        }

        root.dataset.boundSearchSubmit = "true";
        root.dataset.submitting = "false";
        root.addEventListener("input", function () {
            saveFormState(root);
        });
        root.addEventListener("change", function () {
            saveFormState(root);
        });
        root.addEventListener("click", function (event) {
            var submitter = event.target.closest("button[type='submit']");
            if (submitter) {
                clearAlertTargetValidity(root);
            }
        }, true);
        root.addEventListener("submit", function (event) {
            var isSaveAlert = event.submitter && event.submitter.name === "saveAlert";
            clearAlertTargetValidity(root);
            if (isSaveAlert && !validateAlertTargets(root)) {
                event.preventDefault();
                return;
            }

            if (!root.checkValidity()) {
                return;
            }

            if (root.dataset.submitting === "true") {
                event.preventDefault();
                return;
            }

            root.dataset.submitting = "true";

            root.querySelectorAll("[data-submit-intent]").forEach(function (field) {
                field.remove();
            });

            if (event.submitter && event.submitter.name && event.submitter.value) {
                var submitIntent = document.createElement("input");
                submitIntent.type = "hidden";
                submitIntent.name = event.submitter.name;
                submitIntent.value = event.submitter.value;
                submitIntent.setAttribute("data-submit-intent", event.submitter.name);
                root.appendChild(submitIntent);
            }

            saveFormState(root);
            var isSearch = event.submitter && event.submitter.name === "run";
            var overlay = document.querySelector("[data-search-loading]");
            var submitters = root.querySelectorAll("button[type='submit']");
            submitters.forEach(function (button) {
                button.disabled = true;
            });
            if (overlay && isSearch) {
                overlay.hidden = false;
            }
        });
    }

    function bindSearchForm() {
        var root = document.querySelector(".search-native-form");
        if (!root) {
            bindFlightResultDetails();
            return;
        }

        restoreFormState(root);
        bindRouteSwap(root);
        bindTripMode(root);
        bindDateMode(root);
        bindDatePair(root);
        bindLocationAutocomplete(root);
        bindCustomSelects(root);
        bindTimeRanges(root);
        bindTravellerPanel(root);
        bindCurrencyCombobox(root);
        bindAlertTargets(root);
        bindSearchSubmit(root);
        cacheRenderedResults();
        restoreResultsAfterAlertSave();
        bindFlightResultDetails();
        bindResultPagination();
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
