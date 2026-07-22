using System;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.WeTrakr.Configuration;
using Emby.Plugin.WeTrakr.Scrobbling;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Logging;

namespace Emby.Plugin.WeTrakr.Api
{
    /// <summary>
    /// POSTs scrobble payloads to {ApiBaseUrl}/webhooks/jellyfin/{WebhookToken}.
    /// Deliberately reuses the jellyfin webhook route — see the note on
    /// DeviceCodeClient for why. One retry on transient failure — scrobble must
    /// never throw into the event loop or Emby's playback pipeline.
    ///
    /// Uses System.Text.Json directly rather than Emby's IJsonSerializer — see the
    /// note on DeviceCodeClient: Emby's serializer does not honor
    /// [JsonPropertyName], which would have sent WeTrakr the wrong (PascalCase)
    /// field names instead of the snake_case wire format its webhook expects.
    /// </summary>
    public class WeTrakrClient
    {
        private static readonly string UserAgentValue =
            "WeTrakr-Emby/" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0");

        private readonly IHttpClient _httpClient;
        private readonly ILogger _logger;

        public WeTrakrClient(IHttpClient httpClient, ILogManager logManager)
        {
            _httpClient = httpClient;
            _logger = logManager.GetLogger("WeTrakr");
        }

        public async Task SendAsync(string apiBaseUrl, WeTrakrUserConfig userConfig, ScrobblePayload payload, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(userConfig.WebhookToken) || string.IsNullOrEmpty(apiBaseUrl))
            {
                return;
            }

            var url = $"{apiBaseUrl.TrimEnd('/')}/webhooks/jellyfin/{userConfig.WebhookToken}";
            var body = JsonSerializer.Serialize(payload).AsMemory();

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var options = new HttpRequestOptions
                    {
                        Url = url,
                        RequestContentType = "application/json",
                        RequestContent = body,
                        CancellationToken = ct,
                        UserAgent = UserAgentValue,
                        ThrowOnErrorResponse = false
                    };

                    using (var response = await _httpClient.Post(options).ConfigureAwait(false))
                    {
                        var status = (int)response.StatusCode;

                        if (status >= 200 && status < 300)
                        {
                            // Update local bookkeeping — best-effort, non-critical.
                            userConfig.LastScrobbleAt = DateTime.UtcNow;
                            userConfig.ScrobbleCount++;
                            Plugin.Instance?.SaveConfiguration();
                            return;
                        }

                        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                        {
                            // The server no longer recognises this token (revoked,
                            // rotated, or disconnected from the WeTrakr side). Without
                            // this, a dead token "works" forever from the plugin's
                            // point of view while every event is silently dropped
                            // server-side — same failure mode wetrakr-kodi's api.py
                            // guards against. Clearing it makes Status correctly
                            // report disconnected so the user knows to reconnect.
                            _logger.Warn("[WeTrakr] Token rejected ({0}) for event {1} — clearing connection, reconnect required", response.StatusCode, payload.Event);
                            userConfig.WebhookToken = string.Empty;
                            Plugin.Instance?.SaveConfiguration();
                            return;
                        }

                        _logger.Debug("[WeTrakr] POST attempt {0} returned {1} for event {2}", attempt, response.StatusCode, payload.Event);
                        if (attempt == 2)
                        {
                            _logger.Warn("[WeTrakr] POST failed for event {0} after retry: {1}", payload.Event, response.StatusCode);
                        }
                    }
                }
                catch (Exception ex) when (attempt == 1)
                {
                    _logger.Debug("[WeTrakr] POST attempt 1 failed for event {0}, retrying: {1}", payload.Event, ex.Message);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("[WeTrakr] POST failed for event {0}", ex, payload.Event);
                    return;
                }
            }
        }
    }
}
