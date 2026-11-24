using System.Net;

namespace CloudOrc.Backup.Agent.Transport;

public enum AuthorizationHealth
{
    Ok,

    /// <summary>401s: credential expired/invalid - renewal or re-activation needed.</summary>
    Expired,

    /// <summary>403s: endpoint/credential revoked - never retried indefinitely (§8).</summary>
    Revoked
}

public sealed record ReconnectDecision(TimeSpan? Delay, bool StopReconnecting, bool AttemptSessionRenewal, AuthorizationHealth Authorization, string RetryStatus);

/// <summary>
/// Pure reconnect policy for the agent WebSocket (§8): "Use bounded exponential full jitter for
/// reconnects; respect valid Retry-After; distinguish 429 overload, 503 unavailable, 401
/// renewal, 403 revoked ... Never retry revoked authorization indefinitely."
/// Returns null Delay when the caller's normal exponential full-jitter backoff applies.
/// </summary>
public sealed class ConnectionRetryPolicy
{
    public const int UnauthorizedBeforeExpired = 5;
    public const int ForbiddenBeforeStop = 3;
    /// <summary>
    /// Slow retries once the credential looks expired: 1 → 2 → 5 minutes (then every 5). A backend that
    /// briefly does not know the device (restart, wrong database, failover) must not leave a working
    /// computer "offline" for a quarter of an hour; a really expired credential costs one call per 5 min.
    /// </summary>
    public static readonly TimeSpan[] ExpiredRetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    /// <summary>Longest wait between retries with an expired credential.</summary>
    public static readonly TimeSpan ExpiredRetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>After a 403 (revoked) - checked twice more, 10 minutes apart, then reconnecting stops (§8).</summary>
    public static readonly TimeSpan RevokedRetryDelay = TimeSpan.FromMinutes(10);

    private int _consecutiveUnauthorized;
    private int _consecutiveForbidden;

    public AuthorizationHealth Authorization { get; private set; } = AuthorizationHealth.Ok;

    public void OnConnected()
    {
        _consecutiveUnauthorized = 0;
        _consecutiveForbidden = 0;
        Authorization = AuthorizationHealth.Ok;
    }

    public ReconnectDecision OnFailure(HttpStatusCode? status, TimeSpan? retryAfter, bool renewalAvailable)
    {
        switch (status)
        {
            case HttpStatusCode.Unauthorized:
                _consecutiveForbidden = 0;
                _consecutiveUnauthorized++;
                if (_consecutiveUnauthorized == 1 && renewalAvailable)
                {
                    return new ReconnectDecision(TimeSpan.Zero, false, true, Authorization, "Renewing session credential");
                }

                if (_consecutiveUnauthorized >= UnauthorizedBeforeExpired)
                {
                    Authorization = AuthorizationHealth.Expired;
                    var step = Math.Min(_consecutiveUnauthorized - UnauthorizedBeforeExpired, ExpiredRetryDelays.Length - 1);
                    return new ReconnectDecision(ExpiredRetryDelays[step], false, renewalAvailable, Authorization,
                        "Authorization expired - reconnect this device from the CloudOrc portal");
                }

                return new ReconnectDecision(null, false, false, Authorization, "Credential rejected - retrying");

            case HttpStatusCode.Forbidden:
                _consecutiveUnauthorized = 0;
                _consecutiveForbidden++;
                Authorization = AuthorizationHealth.Revoked;
                return _consecutiveForbidden >= ForbiddenBeforeStop
                    ? new ReconnectDecision(null, true, false, Authorization, "Device access was revoked - re-activation required")
                    : new ReconnectDecision(RevokedRetryDelay, false, false, Authorization, "Device access was revoked");

            case HttpStatusCode.TooManyRequests:
            case HttpStatusCode.ServiceUnavailable:
                return new ReconnectDecision(retryAfter, false, false, Authorization,
                    status == HttpStatusCode.TooManyRequests ? "CloudOrc is busy - waiting as instructed" : "CloudOrc is temporarily unavailable");

            default:
                return new ReconnectDecision(null, false, false, Authorization, "Connection failed - retrying");
        }
    }
}
