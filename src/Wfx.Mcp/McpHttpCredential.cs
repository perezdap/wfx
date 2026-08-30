namespace Wfx.Mcp;

/// <summary>
/// The bridge between the HTTP transport and the per-user token store: hands the transport a
/// valid access token for each request, refreshing preemptively on expiry, and retries a
/// refresh once after a 401. Every token it touches joins the host's <see cref="McpSecretSet"/>
/// so redaction covers credentials minted mid-run; an unrecoverable grant is dropped so the
/// next failure carries the sign-in remediation.
/// </summary>
internal sealed class McpHttpCredential(McpTokenStore store, string serverName, McpSecretSet? secrets = null)
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

    private readonly McpTokenStore _store = store;
    private readonly string _serverName = serverName;
    private readonly McpSecretSet _secrets = secrets ?? new McpSecretSet();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public async Task<string?> AcquireAccessTokenAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var record = ReadRegistering();
        if (record is null)
        {
            return null;
        }

        if (record.ExpiresAtUtc is { } expiry && expiry - ExpirySkew <= DateTimeOffset.UtcNow)
        {
            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Re-read under the lock: a concurrent request may already have refreshed.
                record = ReadRegistering();
                if (record is not null &&
                    record.ExpiresAtUtc is { } currentExpiry &&
                    currentExpiry - ExpirySkew <= DateTimeOffset.UtcNow)
                {
                    if (!await new McpOAuthFlow(http, _store).RefreshAsync(_serverName, cancellationToken).ConfigureAwait(false))
                    {
                        return null;
                    }

                    record = ReadRegistering();
                }
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        return record?.AccessToken;
    }

    /// <summary>
    /// Refreshes after the server rejected <paramref name="failedAccessToken"/> with a 401.
    /// Returns true when a refresh happened (or a concurrent request already replaced the
    /// failed token), false when no usable grant remains.
    /// </summary>
    public async Task<bool> RefreshAccessTokenAsync(
        HttpClient http,
        string? failedAccessToken,
        CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = ReadRegistering();
            if (record is null || record.RefreshToken is null)
            {
                return false;
            }

            if (failedAccessToken is not null &&
                !string.Equals(record.AccessToken, failedAccessToken, StringComparison.Ordinal))
            {
                return true;
            }

            var refreshed = await new McpOAuthFlow(http, _store).RefreshAsync(_serverName, cancellationToken)
                .ConfigureAwait(false);
            if (refreshed)
            {
                ReadRegistering();
            }

            return refreshed;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Reads the stored credential and registers its token material for redaction.</summary>
    private McpTokenRecord? ReadRegistering()
    {
        var record = _store.Get(_serverName);
        _secrets.Add(record?.AccessToken);
        _secrets.Add(record?.RefreshToken);
        return record;
    }
}
