// E7 admin dashboard. Plain fetch() calls to the existing JSON API — no build step, no
// framework, matching the rest of this project's Razor + Bootstrap + vanilla-JS setup
// (see site.js). The JWT obtained from POST /api/auth/login lives only in
// sessionStorage for this tab; every admin-gated call below sends it as a Bearer token.
(function () {
    "use strict";

    const TOKEN_KEY = "jorideAdminToken";
    const NAME_KEY = "jorideAdminName";

    function getToken() {
        return sessionStorage.getItem(TOKEN_KEY);
    }

    function authHeaders(extra) {
        const headers = Object.assign({}, extra || {});
        const token = getToken();
        if (token) headers["Authorization"] = "Bearer " + token;
        return headers;
    }

    async function api(method, url, body) {
        const opts = { method, headers: authHeaders(body ? { "Content-Type": "application/json" } : {}) };
        if (body !== undefined) opts.body = JSON.stringify(body);
        const res = await fetch(url, opts);
        let data = null;
        const text = await res.text();
        if (text) {
            try { data = JSON.parse(text); } catch { data = text; }
        }
        if (!res.ok) {
            const message = (data && (data.error || data.message || data.detail)) || (typeof data === "string" ? data : res.statusText);
            const err = new Error(message || ("HTTP " + res.status));
            err.status = res.status;
            err.data = data;
            throw err;
        }
        return data;
    }

    function escapeHtml(value) {
        const div = document.createElement("div");
        div.textContent = value === null || value === undefined ? "" : String(value);
        return div.innerHTML;
    }

    // ── Login / session ──────────────────────────────────────────────────────
    const loginPanel = document.getElementById("login-panel");
    const dashboardSections = document.getElementById("dashboard-sections");
    const loginStatus = document.getElementById("login-status");
    const currentAdminName = document.getElementById("current-admin-name");

    function showLoggedIn(name) {
        loginPanel.style.display = "none";
        dashboardSections.style.display = "";
        currentAdminName.textContent = name;
        loadAll();
    }

    function showLoggedOut() {
        sessionStorage.removeItem(TOKEN_KEY);
        sessionStorage.removeItem(NAME_KEY);
        loginPanel.style.display = "";
        dashboardSections.style.display = "none";
    }

    document.getElementById("login-form").addEventListener("submit", async (e) => {
        e.preventDefault();
        loginStatus.textContent = "Signing in...";
        try {
            const email = document.getElementById("login-email").value;
            const password = document.getElementById("login-password").value;
            const result = await api("POST", "/api/auth/login", { email, password });
            if (!result.user || !result.user.isAdmin) {
                loginStatus.textContent = "This account is not an admin — logged in, but every admin action below will 403.";
            } else {
                loginStatus.textContent = "";
            }
            sessionStorage.setItem(TOKEN_KEY, result.token);
            sessionStorage.setItem(NAME_KEY, result.user.name || email);
            showLoggedIn(result.user.name || email);
        } catch (err) {
            loginStatus.textContent = "Login failed: " + err.message;
        }
    });

    document.getElementById("logout-btn").addEventListener("click", showLoggedOut);

    // ── KYC review ────────────────────────────────────────────────────────────
    async function loadKyc() {
        const status = document.getElementById("kyc-status");
        const body = document.getElementById("kyc-table-body");
        status.textContent = "Loading...";
        body.innerHTML = "";
        try {
            const users = await api("GET", "/api/admin/kyc/pending");
            status.textContent = users.length + " pending.";
            for (const u of users) {
                const tr = document.createElement("tr");
                tr.innerHTML =
                    "<td>" + escapeHtml(u.name) + " (#" + escapeHtml(u.id) + ")</td>" +
                    "<td>" + escapeHtml(u.email) + "</td>" +
                    "<td>" + escapeHtml(u.idNumber) + "</td>" +
                    "<td>" + escapeHtml(u.drivingLicenseNumber) + "</td>" +
                    "<td></td>";
                const actionCell = tr.lastElementChild;

                const reasonInput = document.createElement("input");
                reasonInput.type = "text";
                reasonInput.className = "form-control form-control-sm d-inline-block mb-1";
                reasonInput.placeholder = "reason";
                reasonInput.style.width = "180px";

                const approveBtn = document.createElement("button");
                approveBtn.className = "btn btn-sm btn-success ms-1";
                approveBtn.textContent = "Approve";
                approveBtn.addEventListener("click", () => reviewKyc(u.id, "approve", reasonInput.value, tr));

                const rejectBtn = document.createElement("button");
                rejectBtn.className = "btn btn-sm btn-outline-danger ms-1";
                rejectBtn.textContent = "Reject";
                rejectBtn.addEventListener("click", () => reviewKyc(u.id, "reject", reasonInput.value, tr));

                actionCell.appendChild(reasonInput);
                actionCell.appendChild(approveBtn);
                actionCell.appendChild(rejectBtn);
                body.appendChild(tr);
            }
        } catch (err) {
            status.textContent = "Failed to load: " + err.message;
        }
    }

    async function reviewKyc(userId, action, reason, row) {
        if (!reason || !reason.trim()) {
            alert("A reason is required.");
            return;
        }
        try {
            await api("POST", "/api/admin/kyc/" + userId + "/" + action, { reason });
            await loadKyc(); // re-fetch rather than just removing the row, so the "N pending" count stays accurate
        } catch (err) {
            alert("Failed: " + err.message);
        }
    }

    document.getElementById("kyc-refresh-btn").addEventListener("click", loadKyc);

    // ── Device health ─────────────────────────────────────────────────────────
    async function loadDeviceHealth() {
        const status = document.getElementById("device-status");
        const body = document.getElementById("device-table-body");
        status.textContent = "Loading...";
        body.innerHTML = "";
        try {
            const rows = await api("GET", "/api/admin/dashboard/device-health");
            status.textContent = rows.length + " vehicle(s).";
            for (const r of rows) {
                const tr = document.createElement("tr");
                const badge = r.online
                    ? '<span class="badge bg-success">Online</span>'
                    : '<span class="badge bg-secondary">Offline</span>';
                tr.innerHTML =
                    "<td>#" + escapeHtml(r.vehicleId) + " " + escapeHtml(r.model || "") + "</td>" +
                    "<td>" + escapeHtml(r.licensePlate) + "</td>" +
                    "<td>" + (r.lastPositionTime ? escapeHtml(r.lastPositionTime) : "never") + "</td>" +
                    "<td>" + (r.minutesSinceLastReport !== null && r.minutesSinceLastReport !== undefined ? escapeHtml(r.minutesSinceLastReport) : "-") + "</td>" +
                    "<td>" + badge + "</td>";
                body.appendChild(tr);
            }
        } catch (err) {
            status.textContent = "Failed to load: " + err.message;
        }
    }

    document.getElementById("device-refresh-btn").addEventListener("click", loadDeviceHealth);

    // ── Command console ───────────────────────────────────────────────────────
    async function loadVehicleOptions() {
        const select = document.getElementById("command-vehicle");
        select.innerHTML = "";
        try {
            const vehicles = await api("GET", "/api/vehicles");
            for (const v of vehicles) {
                const opt = document.createElement("option");
                opt.value = v.id;
                opt.textContent = "#" + v.id + " " + (v.licensePlate || "") + " (" + (v.status || "") + ")";
                select.appendChild(opt);
            }
        } catch (err) {
            const opt = document.createElement("option");
            opt.textContent = "Failed to load vehicles: " + err.message;
            select.appendChild(opt);
        }
    }

    document.getElementById("command-form").addEventListener("submit", async (e) => {
        e.preventDefault();
        const resultDiv = document.getElementById("command-result");
        const vehicleId = parseInt(document.getElementById("command-vehicle").value, 10);
        const commandType = document.getElementById("command-type").value;
        const reason = document.getElementById("command-reason").value;

        if (!reason || reason.trim().length < 3) {
            resultDiv.innerHTML = '<div class="alert alert-warning">A reason (3+ characters) is required before a command can be sent.</div>';
            return;
        }

        resultDiv.innerHTML = '<div class="text-muted">Recording reason, then dispatching command...</div>';
        try {
            // Supplementary audit note first (does not gate or influence the real command).
            await api("POST", "/api/admin/dashboard/command-notes", { vehicleId, commandType, reason });

            // The real, unmodified, safety-gated endpoint.
            const result = await api("POST", "/api/vehicles/" + vehicleId + "/commands/" + commandType);

            const badgeClass = result.state === "Confirmed" ? "bg-success"
                : (result.state === "SafetyBlocked" || result.state === "Unauthorized" || result.state === "Failed" ? "bg-danger" : "bg-warning");

            resultDiv.innerHTML =
                '<div class="alert alert-light border">' +
                "Command <strong>" + escapeHtml(commandType) + "</strong> on vehicle #" + escapeHtml(vehicleId) +
                ' &rarr; <span class="badge ' + badgeClass + '">' + escapeHtml(result.state) + "</span>" +
                "<br/><small class=\"text-muted\">command id " + escapeHtml(result.id) + ", requested " + escapeHtml(result.requestedAt) +
                (result.resolvedAt ? ", resolved " + escapeHtml(result.resolvedAt) : "") + "</small>" +
                "</div>";
        } catch (err) {
            resultDiv.innerHTML = '<div class="alert alert-danger">Failed: ' + escapeHtml(err.message) + "</div>";
        }
    });

    // ── Payments: partial capture ────────────────────────────────────────────
    document.getElementById("partial-capture-form").addEventListener("submit", async (e) => {
        e.preventDefault();
        const resultDiv = document.getElementById("capture-result");
        const id = document.getElementById("capture-intent-id").value.trim();
        const amount = parseFloat(document.getElementById("capture-amount").value);
        resultDiv.innerHTML = '<div class="text-muted">Capturing...</div>';
        try {
            const result = await api("POST", "/api/admin/payment-intents/" + encodeURIComponent(id) + "/partial-capture", { amount });
            resultDiv.innerHTML =
                '<div class="alert alert-success">Captured ' + escapeHtml(result.capturedAmount) +
                " — intent now " + escapeHtml(result.state) +
                (result.releasedAmount ? (", released " + escapeHtml(result.releasedAmount) + " of the remaining hold") : "") +
                "</div>";
        } catch (err) {
            resultDiv.innerHTML = '<div class="alert alert-danger">Failed: ' + escapeHtml(err.message) + "</div>";
        }
    });

    // ── Payments: pending top-ups ─────────────────────────────────────────────
    async function loadPendingTopUps() {
        const status = document.getElementById("topups-status");
        const body = document.getElementById("topups-table-body");
        status.textContent = "Loading...";
        body.innerHTML = "";
        try {
            const topups = await api("GET", "/api/admin/dashboard/topups/pending");
            status.textContent = topups.length + " pending.";
            for (const t of topups) {
                const tr = document.createElement("tr");
                tr.innerHTML =
                    "<td>#" + escapeHtml(t.userId) + "</td>" +
                    "<td>" + escapeHtml(t.amount) + "</td>" +
                    "<td>" + escapeHtml(t.paymentMethod) + "</td>" +
                    "<td>" + escapeHtml(t.reference) + "</td>" +
                    "<td>" + escapeHtml(t.createdAt) + "</td>" +
                    "<td></td>";
                const actionCell = tr.lastElementChild;

                const confirmBtn = document.createElement("button");
                confirmBtn.className = "btn btn-sm btn-success";
                confirmBtn.textContent = "Confirm";
                confirmBtn.addEventListener("click", async () => {
                    try {
                        await api("POST", "/api/admin/topups/" + t.id + "/confirm");
                        await loadPendingTopUps(); // re-fetch so the "N pending" count stays accurate
                    } catch (err) { alert("Failed: " + err.message); }
                });

                const rejectBtn = document.createElement("button");
                rejectBtn.className = "btn btn-sm btn-outline-danger ms-1";
                rejectBtn.textContent = "Reject";
                rejectBtn.addEventListener("click", async () => {
                    const reason = prompt("Reason for rejecting this top-up:");
                    if (!reason) return;
                    try {
                        await api("POST", "/api/admin/topups/" + t.id + "/reject", { reason });
                        await loadPendingTopUps();
                    } catch (err) { alert("Failed: " + err.message); }
                });

                actionCell.appendChild(confirmBtn);
                actionCell.appendChild(rejectBtn);
                body.appendChild(tr);
            }
        } catch (err) {
            status.textContent = "Failed to load: " + err.message;
        }
    }

    document.getElementById("topups-refresh-btn").addEventListener("click", loadPendingTopUps);

    // ── Payments: payout report download ─────────────────────────────────────
    document.getElementById("payout-form").addEventListener("submit", async (e) => {
        e.preventDefault();
        const status = document.getElementById("payout-status");
        const start = document.getElementById("payout-start").value;
        const end = document.getElementById("payout-end").value;
        status.textContent = "Requesting report...";
        try {
            // Sent as explicit UTC timestamps (not the bare "YYYY-MM-DD" the <input type=date>
            // gives us) so ASP.NET Core's query-string model binder produces a DateTime with
            // Kind=Utc — PaymentAdminService.GeneratePayoutReportAsync passes these straight
            // into an EF Core/Npgsql query, which rejects Kind=Unspecified. This is a
            // frontend-only adjustment to how the existing endpoint is called, not a change
            // to that endpoint or service.
            const startUtc = start + "T00:00:00Z";
            const endUtc = end + "T23:59:59Z";
            const url = "/api/admin/payouts/report?periodStart=" + encodeURIComponent(startUtc) + "&periodEnd=" + encodeURIComponent(endUtc);
            const res = await fetch(url, { headers: authHeaders() });
            if (!res.ok) {
                const text = await res.text();
                throw new Error(text || ("HTTP " + res.status));
            }
            const blob = await res.blob();
            const objectUrl = URL.createObjectURL(blob);
            const a = document.createElement("a");
            a.href = objectUrl;
            a.download = "payout-report-" + start + "-" + end + ".csv";
            document.body.appendChild(a);
            a.click();
            a.remove();
            URL.revokeObjectURL(objectUrl);
            status.textContent = "Downloaded.";
        } catch (err) {
            status.textContent = "Failed: " + err.message;
        }
    });

    // ── Overdue trips ─────────────────────────────────────────────────────────
    async function loadOverdue() {
        const status = document.getElementById("overdue-status");
        const body = document.getElementById("overdue-table-body");
        status.textContent = "Loading...";
        body.innerHTML = "";
        try {
            const trips = await api("GET", "/api/trips/overdue");
            status.textContent = trips.length + " overdue.";
            for (const t of trips) {
                const tr = document.createElement("tr");
                tr.innerHTML =
                    "<td>#" + escapeHtml(t.id) + "</td>" +
                    "<td>#" + escapeHtml(t.userId) + "</td>" +
                    "<td>#" + escapeHtml(t.vehicleId) + "</td>" +
                    "<td>" + escapeHtml(t.scheduledEndTime) + "</td>" +
                    "<td>" + escapeHtml(t.minutesOverdue) + "</td>" +
                    "<td>" + (t.notified ? '<span class="badge bg-info">yes</span>' : '<span class="badge bg-secondary">not yet</span>') + "</td>";
                body.appendChild(tr);
            }
        } catch (err) {
            status.textContent = "Failed to load: " + err.message;
        }
    }

    document.getElementById("overdue-refresh-btn").addEventListener("click", loadOverdue);

    // ── Bootstrap on load ─────────────────────────────────────────────────────
    function loadAll() {
        loadKyc();
        loadDeviceHealth();
        loadVehicleOptions();
        loadPendingTopUps();
        loadOverdue();
    }

    const existingToken = getToken();
    if (existingToken) {
        showLoggedIn(sessionStorage.getItem(NAME_KEY) || "admin");
    }
})();
