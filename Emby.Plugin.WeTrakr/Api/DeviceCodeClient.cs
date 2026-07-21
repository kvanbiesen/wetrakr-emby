using System;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Logging;

namespace Emby.Plugin.WeTrakr.Api
{
    /// <summary>
    /// Talks to the WeTrakr device-code OAuth endpoints:
    ///   POST /oauth/device/code?platform=jellyfin -> issues a user_code and device_code
    ///   POST /oauth/device/token                  -> exchanges a device_code for an access_token
    ///
    /// Deliberately identifies as platform=jellyfin: the wetrakr-api backend has no
    /// separate "emby" connection slot yet, and this plugin's payload shape is
    /// identical to wetrakr-jf's, so piggybacking on the existing jellyfin webhook
    /// route needs no backend changes. Caveat: an account cannot pair a Jellyfin
    /// server and an Emby server at the same time — the second connection
    /// overwrites auth.connections.jellyfin.webhook_token. Revisit once/if the
    /// backend grows a dedicated emby connection type.
    ///
    /// Uses System.Text.Json directly rather than Emby's IJsonSerializer: Emby's
    /// implementation does not honor [JsonPropertyName] in either direction, which
    /// silently produced empty fields when reading WeTrakr's snake_case responses.
    /// </summary>
    public class DeviceCodeClient
    {
        private readonly IHttpClient _httpClient;
        private readonly ILogger _logger;

        public DeviceCodeClient(IHttpClient httpClient, ILogManager logManager)
        {
            _httpClient = httpClient;
            _logger = logManager.GetLogger("WeTrakr");
        }

        public async Task<DeviceCodeResponse> RequestCodeAsync(string apiBaseUrl, CancellationToken ct)
        {
            var url = $"{apiBaseUrl.TrimEnd('/')}/oauth/device/code?platform=jellyfin";

            try
            {
                var options = new HttpRequestOptions
                {
                    Url = url,
                    CancellationToken = ct,
                    ThrowOnErrorResponse = false
                };

                using (var response = await _httpClient.Post(options).ConfigureAwait(false))
                {
                    if (!IsSuccess(response.StatusCode))
                    {
                        _logger.Warn("[WeTrakr] Device code request returned {0}", response.StatusCode);
                        return null;
                    }

                    return await JsonSerializer.DeserializeAsync<DeviceCodeResponse>(response.Content, cancellationToken: ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[WeTrakr] Device code request failed", ex);
                return null;
            }
        }

        public async Task<DeviceTokenResponse> PollTokenAsync(string apiBaseUrl, string deviceCode, CancellationToken ct)
        {
            var url = $"{apiBaseUrl.TrimEnd('/')}/oauth/device/token";

            try
            {
                var options = new HttpRequestOptions
                {
                    Url = url,
                    RequestContentType = "application/json",
                    RequestContent = JsonSerializer.Serialize(new DeviceTokenRequest { DeviceCode = deviceCode }).AsMemory(),
                    CancellationToken = ct,
                    ThrowOnErrorResponse = false
                };

                using (var response = await _httpClient.Post(options).ConfigureAwait(false))
                {
                    // Both success and 4xx carry a JSON body: {access_token,...} or {error: authorization_pending|expired_token|...}
                    var result = await JsonSerializer.DeserializeAsync<DeviceTokenResponse>(response.Content, cancellationToken: ct).ConfigureAwait(false);
                    return result ?? new DeviceTokenResponse { Error = "invalid_response" };
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[WeTrakr] Device token poll failed", ex);
                return new DeviceTokenResponse { Error = "network_error" };
            }
        }

        private static bool IsSuccess(HttpStatusCode statusCode)
        {
            var code = (int)statusCode;
            return code >= 200 && code < 300;
        }
    }

    public class DeviceTokenRequest
    {
        [JsonPropertyName("device_code")]
        public string DeviceCode { get; set; } = string.Empty;
    }

    public class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")]
        public string DeviceCode { get; set; } = string.Empty;

        [JsonPropertyName("user_code")]
        public string UserCode { get; set; } = string.Empty;

        [JsonPropertyName("verification_url")]
        public string VerificationUrl { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("interval")]
        public int Interval { get; set; }
    }

    public class DeviceTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; }

        [JsonPropertyName("token_type")]
        public string TokenType { get; set; }

        [JsonPropertyName("username")]
        public string Username { get; set; }

        [JsonPropertyName("error")]
        public string Error { get; set; }
    }
}
