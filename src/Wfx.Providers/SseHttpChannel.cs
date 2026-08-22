using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Wfx.Providers;

/// <summary>
/// The HTTP half of an OpenAI-style streaming call: endpoint composition,
/// authorization, request timeout, secret redaction, and server-sent-event
/// framing. Protocol-specific JSON stays in the transports that own it.
/// </summary>
internal sealed class SseHttpChannel
{
    private readonly HttpClient _httpClient;
    private readonly OpenAiProviderOptions _options;

    public SseHttpChannel(HttpClient httpClient, OpenAiProviderOptions options)
    {
        _httpClient = httpClient;
        _options = options;
        if (!_options.BaseUri.IsAbsoluteUri)
        {
            throw new ArgumentException("Provider base URI must be absolute.", nameof(options));
        }
    }

    public HttpRequestMessage CreateRequest(string relativePath, byte[] body)
    {
        var endpoint = new Uri(_options.BaseUri.ToString().TrimEnd('/') + relativePath, UriKind.Absolute);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.UserAgent.ParseAdd("wfx/0.1");
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        foreach (var header in _options.Headers)
        {
            if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Use ApiKey rather than an Authorization custom header.");
            }

            httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        httpRequest.Content = new ByteArrayContent(body);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        return httpRequest;
    }

    /// <summary>
    /// Yields the payload of every <c>data:</c> line in the response stream,
    /// stopping at <c>[DONE]</c>. Throws when the stream carries no data event.
    /// </summary>
    public async IAsyncEnumerable<string> ReadDataEventsAsync(
        HttpRequestMessage httpRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(_options.Timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException($"Model request exceeded the {_options.Timeout} timeout.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadBoundedAsync(response.Content, 64 * 1024, linkedSource.Token).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"Model endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}: {Redact(error)}",
                    null,
                    response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(linkedSource.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var sawData = false;
            while (await reader.ReadLineAsync(linkedSource.Token).ConfigureAwait(false) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var data = line[5..].TrimStart();
                if (data.Equals("[DONE]", StringComparison.Ordinal))
                {
                    break;
                }

                if (data.Length == 0)
                {
                    continue;
                }

                sawData = true;
                yield return data;
            }

            if (!sawData)
            {
                throw new ProviderProtocolException("The provider stream ended without any data events.");
            }
        }
    }

    public string Redact(string value)
    {
        var secrets = _options.Headers.Values
            .Append(_options.ApiKey)
            .Where(static secret => !string.IsNullOrEmpty(secret))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static secret => secret!.Length);
        foreach (var secret in secrets)
        {
            value = value.Replace(secret!, "[REDACTED]", StringComparison.Ordinal);
        }

        return value;
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, int maxCharacters, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var buffer = new char[maxCharacters];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        return new string(buffer, 0, count);
    }
}
