define(['loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select'], function (loading) {
  'use strict';

  var PLUGIN_PATH = 'Plugins/WeTrakr/Users';
  var POLL_INTERVAL_MS = 5000;
  var pollTimer = null;

  function currentUserId(view) {
    return view.querySelector('#wtSelectUser').value;
  }

  function apiUrl(view, path) {
    return ApiClient.getUrl(PLUGIN_PATH + '/' + currentUserId(view) + '/' + path);
  }

  function stopPolling() {
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
  }

  function showState(view, state) {
    ['Disconnected', 'Pairing', 'Connected'].forEach(function (s) {
      var el = view.querySelector('#wtState' + s);
      if (el) el.style.display = (s === state) ? 'block' : 'none';
    });
  }

  function describeError(err) {
    if (!err) return 'Unknown error';
    if (err.status !== undefined) {
      return 'HTTP ' + err.status + (err.statusText ? ' ' + err.statusText : '');
    }
    if (err.message) return err.message;
    return String(err);
  }

  function alertError(prefix, err) {
    loading.hide();
    console.error('[WeTrakr]', prefix, err);
    var base = prefix + ': ' + describeError(err);
    if (err && typeof err.text === 'function') {
      err.text().then(function (body) {
        var extra = body ? ('\n\n' + body.substring(0, 300)) : '';
        Dashboard.alert(base + extra);
      }).catch(function () {
        Dashboard.alert(base);
      });
    } else {
      Dashboard.alert(base);
    }
  }

  function populateUsers(view, users) {
    var select = view.querySelector('#wtSelectUser');
    var previous = select.value;
    select.innerHTML = '';
    users.forEach(function (user) {
      var opt = document.createElement('option');
      opt.value = user.Id;
      opt.text = user.Name;
      select.add(opt);
    });
    if (previous && users.some(function (u) { return u.Id === previous; })) {
      select.value = previous;
    }
  }

  function refreshStatus(view) {
    return ApiClient.getJSON(apiUrl(view, 'Status')).then(function (s) {
      if (s.Connected) {
        view.querySelector('#wtUsername').textContent = s.Username || '(unknown user)';
        var last = s.LastScrobbleAt ? new Date(s.LastScrobbleAt).toLocaleString() : 'never';
        view.querySelector('#wtStats').textContent = s.ScrobbleCount + ' events sent — last at ' + last;
        view.querySelector('#wtTogglePlaying').checked = !!s.ScrobblePlaying;
        view.querySelector('#wtToggleWatched').checked = !!s.ScrobbleWatched;
        view.querySelector('#wtToggleRatings').checked = !!s.ScrobbleRatings;
        view.querySelector('#wtToggleSyncHistory').checked = !!s.SyncWatchedHistory;
        loadFolders(view, s.LocationsExcluded || []);
        showState(view, 'Connected');
      } else {
        showState(view, 'Disconnected');
      }
    }).catch(function (err) {
      console.error('[WeTrakr] Status error:', err, 'status:', err && err.status);
      showState(view, 'Disconnected');
    });
  }

  function loadFolders(view, excludedLocations) {
    var container = view.querySelector('#wtLocations');
    return ApiClient.getVirtualFolders(currentUserId(view)).then(function (virtualFolders) {
      container.innerHTML = '';
      (virtualFolders || []).forEach(function (folder) {
        (folder.Locations || []).forEach(function (location) {
          var label = document.createElement('label');
          label.className = 'emby-checkbox-label';

          var input = document.createElement('input', { is: 'emby-checkbox' });
          input.type = 'checkbox';
          input.className = 'wtLocationCheckbox';
          input.value = location;
          input.checked = excludedLocations.indexOf(location) !== -1;
          input.addEventListener('change', function () { saveExcludedLocations(view); });

          var span = document.createElement('span');
          span.textContent = folder.Name + ' — ' + location;

          label.appendChild(input);
          label.appendChild(span);
          container.appendChild(label);
        });
      });
    }).catch(function (err) {
      console.error('[WeTrakr] Failed to load folders', err);
    });
  }

  function saveExcludedLocations(view) {
    var container = view.querySelector('#wtLocations');
    var checked = Array.prototype.filter.call(
      container.querySelectorAll('.wtLocationCheckbox'),
      function (cb) { return cb.checked; }
    ).map(function (cb) { return cb.value; });

    return ApiClient.ajax({
      type: 'POST',
      url: apiUrl(view, 'Settings'),
      data: JSON.stringify({ LocationsExcluded: checked }),
      contentType: 'application/json'
    });
  }

  function startPairing(view) {
    stopPolling();
    loading.show();
    ApiClient.ajax({ type: 'POST', url: apiUrl(view, 'ConnectStart') })
      .then(function (res) { return res.json(); })
      .then(function (code) {
        loading.hide();
        view.querySelector('#wtUserCode').textContent = code.UserCode;
        var link = view.querySelector('#wtVerificationLink');
        if (code.VerificationUrl) {
          link.href = code.VerificationUrl;
          link.textContent = code.VerificationUrl;
        }
        view.querySelector('#wtPairingStatus').textContent = 'Waiting for confirmation…';
        showState(view, 'Pairing');
        pollTimer = setInterval(function () { pollForToken(view); }, POLL_INTERVAL_MS);
      })
      .catch(function (err) { alertError('Could not start pairing', err); });
  }

  function pollForToken(view) {
    ApiClient.ajax({ type: 'POST', url: apiUrl(view, 'Poll') })
      .then(function (res) { return res.json(); })
      .then(function (result) {
        var statusEl = view.querySelector('#wtPairingStatus');
        if (result.Status === 'connected') {
          stopPolling();
          refreshStatus(view);
        } else if (result.Status === 'expired_token') {
          stopPolling();
          statusEl.textContent = 'Code expired. Please start again.';
          setTimeout(function () { showState(view, 'Disconnected'); }, 2500);
        } else if (result.Status === 'authorization_pending') {
          statusEl.textContent = 'Waiting for confirmation…';
        } else if (result.Status === 'no_pending_code') {
          stopPolling();
          showState(view, 'Disconnected');
        } else {
          statusEl.textContent = 'Status: ' + result.Status;
        }
      })
      .catch(function () {
        var statusEl = view.querySelector('#wtPairingStatus');
        if (statusEl) statusEl.textContent = 'Network error. Retrying…';
      });
  }

  function cancelPairing(view) {
    stopPolling();
    showState(view, 'Disconnected');
  }

  function disconnect(view) {
    if (!confirm('Disconnect WeTrakr for this user? Playback will stop scrobbling until reconnected.')) return;
    loading.show();
    ApiClient.ajax({ type: 'POST', url: apiUrl(view, 'Disconnect') })
      .then(function () {
        loading.hide();
        refreshStatus(view);
      });
  }

  function updateSetting(view, key, value) {
    var body = {};
    body[key] = value;
    return ApiClient.ajax({
      type: 'POST',
      url: apiUrl(view, 'Settings'),
      data: JSON.stringify(body),
      contentType: 'application/json'
    });
  }

  function loadForSelectedUser(view) {
    stopPolling();
    showState(view, 'Disconnected');
    loading.show();
    refreshStatus(view).then(function () { loading.hide(); });
  }

  return function (view) {
    view.querySelector('#wtConnectBtn').addEventListener('click', function () { startPairing(view); });
    view.querySelector('#wtCancelPairingBtn').addEventListener('click', function () { cancelPairing(view); });
    view.querySelector('#wtDisconnectBtn').addEventListener('click', function () { disconnect(view); });
    view.querySelector('#wtTogglePlaying').addEventListener('change', function (e) { updateSetting(view, 'ScrobblePlaying', e.target.checked); });
    view.querySelector('#wtToggleWatched').addEventListener('change', function (e) { updateSetting(view, 'ScrobbleWatched', e.target.checked); });
    view.querySelector('#wtToggleRatings').addEventListener('change', function (e) { updateSetting(view, 'ScrobbleRatings', e.target.checked); });
    view.querySelector('#wtToggleSyncHistory').addEventListener('change', function (e) { updateSetting(view, 'SyncWatchedHistory', e.target.checked); });
    view.querySelector('#wtSelectUser').addEventListener('change', function () { loadForSelectedUser(view); });

    view.addEventListener('viewshow', function () {
      loading.show();
      ApiClient.getUsers().then(function (users) {
        populateUsers(view, users);
        loading.hide();
        loadForSelectedUser(view);
      });
    });

    view.addEventListener('viewhide', stopPolling);
    view.addEventListener('viewdestroy', stopPolling);
  };
});
