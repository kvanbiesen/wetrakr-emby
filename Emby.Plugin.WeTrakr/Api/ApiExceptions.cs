using System;

namespace Emby.Plugin.WeTrakr.Api
{
    /// <summary>WeTrakr answered with an error, or could not be reached (Status is null then).</summary>
    public class WeTrakrApiException : Exception
    {
        public WeTrakrApiException(int? status, string code, string message, Exception inner = null)
            : base(message, inner)
        {
            Status = status;
            Code = code;
        }

        public int? Status { get; }
        public string Code { get; }
    }

    /// <summary>The user has no login, or WeTrakr revoked it; they have to connect again.</summary>
    public class NotConnectedException : WeTrakrApiException
    {
        public NotConnectedException(string message) : base(401, "not_connected", message) { }
    }

    /// <summary>The user's daily request quota is used up. Retrying today only makes it worse.</summary>
    public class QuotaExceededException : WeTrakrApiException
    {
        public QuotaExceededException(string message) : base(429, "QUOTA_EXCEEDED", message) { }
    }

    /// <summary>No app key is configured, so nothing can be sent.</summary>
    public class ApiKeyMissingException : WeTrakrApiException
    {
        public ApiKeyMissingException() : base(null, "api_key_missing", "No WeTrakr API key is configured. An admin can paste one on the WeTrakr plugin page.") { }
    }
}
