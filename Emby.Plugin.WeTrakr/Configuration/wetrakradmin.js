define([
    "baseView",
    "appRouter",
    "emby-button",
    "emby-linkbutton"

], function (BaseView, appRouter) {

    "use strict";

    var USER_PAGE = "configurationpage?name=WeTrakr&userId=";

    function escapeHtml(text) {
        return String(text || "")
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;");
    }

    function getJson(path) {
        return ApiClient.getJSON(ApiClient.getUrl(path));
    }

    function renderAdmin(view, info) {
        view.querySelector(".adminVersions").textContent =
            "Plugin " + info.pluginVersion + " on Emby Server " + info.embyVersion + ".";

        var status = view.querySelector(".adminKeyStatus");
        status.innerHTML = info.apiKeyConfigured
            ? ""
            : "WeTrakr isn't fully set up on this server yet, so users can't connect. See "
                + '<a is="emby-linkbutton" class="button-link" href="https://github.com/kvanbiesen/wetrakr-emby" target="_blank" rel="noopener">github.com/kvanbiesen/wetrakr-emby</a> for setup help.';
    }

    function renderUser(user) {
        var status = user.connected
            ? "Connected" + (user.username ? " as " + user.username : "") + (user.autoSync ? ", syncing automatically" : "")
            : "Not connected";
        var last = user.lastRunAt ? " - last sync " + new Date(user.lastRunAt).toLocaleString() : "";
        return '<div class="listItem listItem-border">' +
            '<div class="listItemBody two-line">' +
            '<h3 class="listItemBodyText">' + escapeHtml(user.name) + '</h3>' +
            '<div class="listItemBodyText secondary">' + escapeHtml(status + last) + '</div>' +
            '</div>' +
            '<button is="emby-button" type="button" class="raised raised-mini btnUserWeTrakr" data-id="' + escapeHtml(user.id) + '"><span>WeTrakr settings</span></button>' +
            '</div>';
    }

    function load(view) {
        getJson("WeTrakr/admin").then(function (info) { renderAdmin(view, info); });

        var container = view.querySelector(".wetrakrUserList");
        getJson("WeTrakr/admin/users").then(function (users) {
            container.innerHTML = users.map(renderUser).join("") || "<p>No users found.</p>";
        }, function () {
            container.innerHTML = "<p>Could not load users.</p>";
        });
    }

    function View(view, params) {
        BaseView.apply(this, arguments);

        view.addEventListener("click", function (e) {
            var btn = e.target.closest(".btnUserWeTrakr");
            if (btn) appRouter.show(USER_PAGE + encodeURIComponent(btn.getAttribute("data-id")));
        });
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        load(this.view);
    };

    return View;
});
