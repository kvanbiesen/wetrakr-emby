define([
    "loading",
    "baseView",
    "alert",
    "confirm",
    "globalize",
    "emby-button",
    "emby-checkbox",
    "emby-toggle",
    "emby-linkbutton",
    "emby-select",
    "emby-input"

], function (loading, BaseView, alert, confirm, globalize) {

    "use strict";

    var OPTIONS_KEY = "wetrakr";
    var STATUS_POLL_MS = 2000;
    var STATUS_POLL_RETRIES = 5;

    // "toast" is loaded on demand rather than as a hard dependency above: Emby's own
    // define([...]) waits for every listed module before this page's factory runs at
    // all, so one flaky shared asset (seen in practice: modules/toast/toast.js
    // occasionally 502ing) would otherwise take the whole settings page down with it,
    // not just the small "Saved"/"Connected" confirmations that actually use it.
    // A failed load here just skips that one confirmation; nothing else on the page
    // depends on it.
    function toast(message) {
        try {
            require(["toast"], function (t) { t(message); }, function (err) {
                console.error("[WeTrakr] toast module unavailable, skipping notification:", err);
            });
        } catch (err) {
            console.error("[WeTrakr] toast module unavailable, skipping notification:", err);
        }
    }

    // ---- server calls ------------------------------------------------------------

    function getJson(path) {
        return ApiClient.getJSON(ApiClient.getUrl(path));
    }

    // ApiClient.ajax resolves with parsed JSON when dataType is "json"
    function postJson(path, query) {
        return ApiClient.ajax({ type: "POST", url: ApiClient.getUrl(path, query), dataType: "json" });
    }

    function postOnly(path) {
        return ApiClient.ajax({ type: "POST", url: ApiClient.getUrl(path) });
    }

    function describeError(err, what) {
        var status = err && err.status;
        console.error(what + " request failed", err);
        if (!status || status === 404 || status === 502 || status === 504) {
            return "The Emby server could not be reached at " + ApiClient.serverAddress() +
                ". If you use a custom domain or reverse proxy, open Emby by its IP address and try again.";
        }
        return what + " failed (HTTP " + status + "). See the browser console for details.";
    }

    // ---- options (the user's settings, stored by Emby per user) -----------------------

    function fetchOptions(userId) {
        return ApiClient.getTypedUserSettings(userId, OPTIONS_KEY);
    }

    // Re-read, patch, write: fields this page does not show survive. Saves are chained per view, because each one
    // writes the stored settings back whole and one started while another is in flight would undo it.
    function saveOptions(instance, patch) {
        var userId = instance.params.userId;
        var run = function () {
            loading.show();
            return fetchOptions(userId).then(function (options) {
                options = options || {};
                patch(options);
                return ApiClient.updateTypedUserSettings(userId, OPTIONS_KEY, options);
            }).then(function () {
                loading.hide();
                toast(globalize.translate("SettingsSaved"));
                return true;
            }, function (err) {
                loading.hide();
                alert(describeError(err, "Saving settings"));
                return false;
            });
        };
        var saved = (instance._saveChain || Promise.resolve()).then(run);
        instance._saveChain = saved;
        return saved;
    }

    // read at the click, so a save queued behind another keeps what was on screen when it was requested
    function readForm(view) {
        var minLength = parseInt(view.querySelector(".minLength").value, 10);
        var excluded = [];
        Array.prototype.forEach.call(view.querySelectorAll(".libraryCheck"), function (box) {
            if (box.checked) excluded.push(box.getAttribute("data-id"));
        });
        return {
            scrobbleMovies: view.querySelector(".scrobbleMovies").checked,
            scrobbleShows: view.querySelector(".scrobbleShows").checked,
            minLength: isNaN(minLength) ? 5 : Math.max(0, minLength),
            syncPull: view.querySelector(".syncPull").checked,
            syncPush: view.querySelector(".syncPush").checked,
            syncFavorites: view.querySelector(".syncFavorites").checked,
            excludedLibraries: excluded,
            autoSync: view.querySelector(".autoSync").checked,
            autoSyncHours: parseInt(view.querySelector(".autoSyncHours").value, 10) || 24
        };
    }

    function applyForm(options, form) {
        Object.keys(form).forEach(function (key) { options[key] = form[key]; });
    }

    function writeForm(view, options) {
        view.querySelector(".scrobbleMovies").checked = options.scrobbleMovies !== false;
        view.querySelector(".scrobbleShows").checked = options.scrobbleShows !== false;
        view.querySelector(".minLength").value = options.minLength === undefined || options.minLength === null ? 5 : options.minLength;
        view.querySelector(".syncPull").checked = options.syncPull !== false;
        view.querySelector(".syncPush").checked = options.syncPush !== false;
        view.querySelector(".syncFavorites").checked = options.syncFavorites === true;
        view.querySelector(".autoSync").checked = options.autoSync === true;
        view.querySelector(".autoSyncHours").value = String(options.autoSyncHours || 24);
        view.querySelector(".autoSyncInterval").classList.toggle("hide", options.autoSync !== true);
    }

    // ---- sections -----------------------------------------------------------------

    var SECTIONS = [".notConnected", ".pairing", ".connected"];

    function showSection(view, selector) {
        SECTIONS.forEach(function (s) {
            view.querySelector(s).classList.toggle("hide", s !== selector);
        });
    }

    function renderLibraries(view, libraries, excluded) {
        var list = view.querySelector(".libraryList");
        list.innerHTML = "";
        if (!libraries.length) {
            list.textContent = "No libraries found.";
            return;
        }
        libraries.forEach(function (library) {
            var label = document.createElement("label");
            label.className = "emby-checkbox-label";
            var box = document.createElement("input", { is: "emby-checkbox" });
            box.type = "checkbox";
            box.className = "libraryCheck";
            box.setAttribute("data-id", library.id);
            box.checked = excluded.indexOf(library.id) !== -1;
            var text = document.createElement("span");
            text.textContent = library.name;
            label.appendChild(box);
            label.appendChild(text);
            list.appendChild(label);
        });
    }

    // ---- login (device code) --------------------------------------------------------

    function stopPairing(instance) {
        if (instance._pairTimer) { clearTimeout(instance._pairTimer); instance._pairTimer = null; }
        if (instance._secondsTimer) { clearInterval(instance._secondsTimer); instance._secondsTimer = null; }
        instance._pairing = null;
    }

    function abortPairing(instance, message) {
        loading.hide();
        stopPairing(instance);
        if (instance.view) load(instance);
        if (message) alert(message);
    }

    function schedulePoll(instance, pairing) {
        instance._pairTimer = setTimeout(function () { pollPairing(instance, pairing); }, pairing.interval * 1000);
    }

    // _pairing ties every poll to the attempt that started it: an answer landing after Cancel or a restarted login is dropped
    function pollPairing(instance, pairing) {
        postJson("WeTrakr/oauth/" + instance.params.userId + "/poll").then(function (result) {
            if (instance._pairing !== pairing) return;

            if (Date.now() > pairing.finish || result.status === "expired") {
                abortPairing(instance, "The code expired. Connect again to get a new one.");
            } else if (result.status === "connected") {
                stopPairing(instance);
                toast("Connected to WeTrakr" + (result.username ? " as " + result.username : ""));
                load(instance);
            } else if (result.status === "denied") {
                abortPairing(instance, "WeTrakr access was denied.");
            } else if (result.status === "error") {
                abortPairing(instance, result.error || "WeTrakr could not confirm the login.");
            } else if (result.status === "none") {
                abortPairing(instance, null);
            } else {
                schedulePoll(instance, pairing);
            }
        }, function (err) {
            if (instance._pairing !== pairing) return;
            abortPairing(instance, describeError(err, "Connecting to WeTrakr"));
        });
    }

    function startPairing(instance) {
        var view = instance.view;
        loading.show();
        postJson("WeTrakr/oauth/" + instance.params.userId + "/start").then(function (code) {
            loading.hide();
            if (!instance.view) return;
            if (code.error) { alert(code.error); return; }

            var pairing = { interval: code.interval || 5, finish: Date.now() + (code.expiresIn || 600) * 1000 };
            instance._pairing = pairing;

            var text = view.querySelector(".pairingText");
            text.innerHTML = "";
            text.appendChild(document.createTextNode("Open "));
            var link = document.createElement("a");
            link.href = code.activateUrl;
            link.target = "_blank";
            link.rel = "noopener";
            link.textContent = code.verificationUrl;
            text.appendChild(link);
            text.appendChild(document.createTextNode(" on your phone or computer, sign in to WeTrakr and enter this code:"));

            view.querySelector(".pairingCode").textContent = code.userCode;
            view.querySelector(".pairingStatus").textContent = "Waiting for you to approve it on WeTrakr...";

            var secondsEl = view.querySelector(".pairingSeconds");
            var renderSeconds = function () {
                secondsEl.textContent = Math.max(0, Math.round((pairing.finish - Date.now()) / 1000));
            };
            renderSeconds();
            instance._secondsTimer = setInterval(renderSeconds, 1000);

            schedulePoll(instance, pairing);
            showSection(view, ".pairing");
        }, function (err) {
            loading.hide();
            if (instance.view) alert(describeError(err, "Connecting to WeTrakr"));
        });
    }

    function cancelPairing(instance) {
        stopPairing(instance);
        postOnly("WeTrakr/oauth/" + instance.params.userId + "/cancel");
        load(instance);
    }

    function logOut(instance) {
        confirm({
            title: "Disconnect from WeTrakr",
            text: "Nothing more is sent to WeTrakr and nothing is imported from it until you connect again. Your history on both sides stays as it is.",
            confirmText: "Disconnect"
        }).then(function () {
            loading.show();
            postOnly("WeTrakr/oauth/" + instance.params.userId + "/logout").then(function () {
                loading.hide();
                stopStatusPolling(instance);
                load(instance);
            }, function (err) {
                loading.hide();
                alert(describeError(err, "Disconnecting"));
            });
        }, function () { /* cancelled */ });
    }

    // ---- sync -----------------------------------------------------------------------

    function formatDate(iso) {
        var date = new Date(iso);
        return isNaN(date.getTime()) ? iso : date.toLocaleString();
    }

    function describeStatus(status) {
        if (status.running) return status.message || "Syncing...";
        var text = status.message || "";
        if (status.lastRunAt) text = "Last sync " + formatDate(status.lastRunAt) + ". " + text;
        return text || "No sync has run yet.";
    }

    function stopStatusPolling(instance) {
        if (instance._statusTimer) { clearTimeout(instance._statusTimer); instance._statusTimer = null; }
    }

    function scheduleStatusPoll(instance) {
        stopStatusPolling(instance);
        instance._statusTimer = setTimeout(function () {
            instance._statusTimer = null;
            refreshStatus(instance);
        }, STATUS_POLL_MS);
    }

    function setRunning(instance, running) {
        instance._syncRunning = running;
        instance.view.querySelector(".btnSync").disabled = running;
        instance.view.querySelector(".btnReset").disabled = running;
    }

    // _paused guards a response that lands after the page was left
    function applyStatus(instance, status) {
        if (instance._paused) { instance._loaded = false; return; }
        instance._pollFailures = 0;

        var wasRunning = instance._syncRunning;
        setRunning(instance, !!status.running);
        instance.view.querySelector(".syncStatus").textContent = describeStatus(status);
        instance.view.querySelector(".autoSyncStatus").textContent = status.lastAutoRunAt
            ? "Last automatic sync " + formatDate(status.lastAutoRunAt) + "."
            : "No automatic sync has run yet.";

        if (instance._syncRunning) {
            scheduleStatusPoll(instance);
        } else if (wasRunning) {
            if (status.error) alert(status.error); else toast("Sync finished");
            load(instance);
        }
    }

    function refreshStatus(instance) {
        getJson("WeTrakr/sync/" + instance.params.userId + "/status").then(function (status) {
            applyStatus(instance, status);
        }, function (err) {
            if (instance._paused) return;
            // the run keeps going on the server, so a failed poll is retried before giving up
            if (instance._syncRunning) {
                instance._pollFailures = (instance._pollFailures || 0) + 1;
                if (instance._pollFailures < STATUS_POLL_RETRIES) {
                    instance.view.querySelector(".syncStatus").textContent = "Waiting for the Emby server (retry " + instance._pollFailures + " of " + STATUS_POLL_RETRIES + ")...";
                    scheduleStatusPoll(instance);
                    return;
                }
                setRunning(instance, false);
                instance._loaded = false;
                instance.view.querySelector(".syncStatus").textContent = "Lost the connection to Emby while syncing. It continues on the server; reopen this page to see the result.";
                return;
            }
            setRunning(instance, false);
            alert(describeError(err, "Reading the sync status"));
        });
    }

    function startSync(instance, reset, automatic) {
        var userId = instance.params.userId;
        loading.show();
        postJson("WeTrakr/sync/" + userId, { reset: reset ? "true" : "false", automatic: automatic ? "true" : "false" }).then(function (status) {
            loading.hide();
            if (instance._paused) { instance._loaded = false; return; }
            if (status.running) {
                toast(reset ? "Sync started, reading everything again" : "Sync started");
                applyStatus(instance, status);
            } else if (status.error) {
                alert(status.error);
            }
        }, function (err) {
            loading.hide();
            alert(describeError(err, "Sync"));
        });
    }

    function confirmSync(instance, reset) {
        var form = readForm(instance.view);
        if (!form.syncPull && !form.syncPush && !form.syncFavorites) {
            alert("Turn on importing from WeTrakr, sending new Emby plays, or syncing favorites first.");
            return;
        }
        var parts = [];
        if (form.syncPull) parts.push("marks what you watched on WeTrakr as played in Emby");
        if (form.syncPush) parts.push("sends new plays from Emby that WeTrakr is missing");
        if (form.syncFavorites) parts.push("brings favorite movies in from WeTrakr");
        confirm({
            title: reset ? "Reset and sync everything again" : "Sync now",
            text: "This " + parts.join(" and ") + ". " + (reset
                ? "It reads your whole WeTrakr history again; nothing is unmarked, and Emby history from before is still not sent."
                : "The first sync goes through your whole history, later ones only what changed."),
            confirmText: "Sync"
        }).then(function () {
            // saved first, so the run uses what is on screen
            saveOptions(instance, function (options) { applyForm(options, form); }).then(function (saved) {
                if (saved) startSync(instance, reset, false);
            });
        }, function () { /* cancelled */ });
    }

    // ---- loading ------------------------------------------------------------------------

    function load(instance) {
        var view = instance.view;
        var userId = instance.params.userId;
        loading.show();

        getJson("WeTrakr/account/" + userId).then(function (account) {
            if (!instance.view) { loading.hide(); return null; }

            view.querySelector(".noApiKey").classList.toggle("hide", account.apiKeyConfigured);
            view.querySelector(".btnConnect").disabled = !account.apiKeyConfigured;

            if (!account.connected) {
                stopStatusPolling(instance);
                instance._syncRunning = false;
                showSection(view, ".notConnected");
                instance._loaded = true;
                loading.hide();
                return null;
            }

            view.querySelector(".accountLine").textContent = account.username
                ? "You are connected to WeTrakr as " + account.username + "."
                : "You are connected to WeTrakr.";
            if (account.username) view.querySelector(".accountLinks a").href = "https://wetrakr.com/" + encodeURIComponent(account.username);

            return Promise.all([fetchOptions(userId), getJson("WeTrakr/libraries/" + userId)]).then(function (results) {
                if (!instance.view) { loading.hide(); return; }
                var options = results[0] || {};
                writeForm(view, options);
                renderLibraries(view, results[1] || [], options.excludedLibraries || []);
                showSection(view, ".connected");
                refreshStatus(instance);
                instance._loaded = true;
                loading.hide();
            });
        }).catch(function (err) {
            loading.hide();
            if (instance.view) alert(describeError(err, "Loading WeTrakr settings"));
        });
    }

    function onSubmit(ev) {
        ev.preventDefault();
        var form = readForm(this.view);
        saveOptions(this, function (options) { applyForm(options, form); });
        return false;
    }

    function bindEvents(instance) {
        var view = instance.view;
        view.querySelector("form").addEventListener("submit", onSubmit.bind(instance));
        view.querySelector(".btnConnect").addEventListener("click", function () { startPairing(instance); });
        view.querySelector(".btnCancelPairing").addEventListener("click", function () { cancelPairing(instance); });
        view.querySelector(".btnLogout").addEventListener("click", function () { logOut(instance); });
        view.querySelector(".btnSync").addEventListener("click", function () { confirmSync(instance, false); });
        view.querySelector(".btnReset").addEventListener("click", function () { confirmSync(instance, true); });

        view.querySelector(".autoSync").addEventListener("change", function () {
            var toggle = this, checked = toggle.checked, form = readForm(view);
            view.querySelector(".autoSyncInterval").classList.toggle("hide", !checked);
            // the on-screen edits are carried along with the switch
            saveOptions(instance, function (options) { applyForm(options, form); }).then(function (saved) {
                if (!saved) { toggle.checked = !checked; view.querySelector(".autoSyncInterval").classList.toggle("hide", checked); }
                else if (checked && (form.syncPull || form.syncPush || form.syncFavorites)) startSync(instance, false, true);
            });
        });

        view.querySelector(".autoSyncHours").addEventListener("change", function () {
            var form = readForm(view);
            saveOptions(instance, function (options) { applyForm(options, form); });
        });
    }

    function View(view, params) {
        BaseView.apply(this, arguments);
        bindEvents(this);
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        this._paused = false;
        if (options.refresh || !this._loaded) {
            load(this);
        } else if (this._syncRunning) {
            refreshStatus(this);
        }
    };

    View.prototype.onPause = function () {
        this._paused = true;
        stopStatusPolling(this);
        BaseView.prototype.onPause.apply(this, arguments);
    };

    View.prototype.destroy = function () {
        this._paused = true;
        stopStatusPolling(this);
        stopPairing(this);
        BaseView.prototype.destroy.apply(this, arguments);
    };

    return View;
});
