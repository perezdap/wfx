using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Wfx.Core;
using Wfx.Providers;

namespace Wfx.Providers.Tests;

public sealed class ProviderRetryTests
{
    private static readonly QueuedResponse Success = new()
    {
        StatusCode = HttpStatusCode.OK,
        Content = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n"
    };

    [Fact]
    public async Task RetriesRateLimitsThenStreams()
    {
        var handler = new QueueStubHandler([
            QueuedResponse.Failure((HttpStatusCode)429),
            QueuedResponse.Failure((HttpStatusCode)429),
            Success
        ]);
        var delays = new List<TimeSpan>();
        var provider = CreateProvider(handler, delays.Add);

        var events = await CollectAsync(provider.StreamAsync(
            new ModelRequest("model", [new ModelMessage(ModelRole.User, "hi")], []),
            TestContext.Current.CancellationToken));

        Assert.Equal("ok", Assert.Single(events.OfType<ModelCompleted>()).Message.Content);
        Assert.Equal(3, handler.Attempts);
        Assert.Equal(2, delays.Count);
        Assert.InRange(delays[0], TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.InRange(delays[1], TimeSpan.Zero, TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task RetriesServerErrorsThenStreams()
    {
        var handler = new QueueStubHandler([
            QueuedResponse.Failure(HttpStatusCode.ServiceUnavailable),
            Success
        ]);
        var delays = new List<TimeSpan>();
        var provider = CreateProvider(handler, delays.Add);

        var events = await CollectAsync(provider.StreamAsync(
            new ModelRequest("model", [new ModelMessage(ModelRole.User, "hi")], []),
            TestContext.Current.CancellationToken));

        Assert.Single(events.OfType<ModelCompleted>());
        Assert.Equal(2, handler.Attempts);
        Assert.Single(delays);
    }

    [Fact]
    public async Task HonorsRetryAfterHeader()
    {
        var handler = new QueueStubHandler([
            QueuedResponse.Failure((HttpStatusCode)429, retryAfter: TimeSpan.FromSeconds(2)),
            Success
        ]);
        var delays = new List<TimeSpan>();
        var provider = CreateProvider(handler, delays.Add);

        await CollectAsync(provider.StreamAsync(
            new ModelRequest("model", [new ModelMessage(ModelRole.User, "hi")], []),
            TestContext.Current.CancellationToken));

        Assert.Equal([TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task ExhaustsRetriesAndThrows()
    {
        var handler = new QueueStubHandler([
            QueuedResponse.Failure((HttpStatusCode)429),
            QueuedResponse.Failure((HttpStatusCode)429),
            QueuedResponse.Failure((HttpStatusCode)429),
            QueuedResponse.Failure((HttpStatusCode)429)
        ]);
        var delays = new List<TimeSpan>();
        var provider = CreateProvider(handler, delays.Add);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await CollectAsync(provider.StreamAsync(
                new ModelRequest("model", [new ModelMessage(ModelRole.User, "x")], []),
                TestContext.Current.CancellationToken)));

        Assert.Equal((HttpStatusCode)429, exception.StatusCode);
        Assert.Equal(4, handler.Attempts);
        Assert.Equal(3, delays.Count);
    }

    [Fact]
    public async Task DoesNotRetryStatusesBeyond599()
    {
        var handler = new QueueStubHandler([
            QueuedResponse.Failure((HttpStatusCode)600),
            Success
        ]);
        var provider = CreateProvider(handler, _ => throw new InvalidOperationException("A non-5xx status must not wait."));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await CollectAsync(provider.StreamAsync(
                new ModelRequest("model", [new ModelMessage(ModelRole.User, "x")], []),
                TestContext.Current.CancellationToken)));

        Assert.Equal((HttpStatusCode)600, exception.StatusCode);
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task FailsNonTransientClientErrorsImmediately()
    {
        var handler = new QueueStubHandler([QueuedResponse.Failure(HttpStatusCode.BadRequest)]);
        var provider = CreateProvider(handler, _ => throw new InvalidOperationException("A client error must not wait."));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await CollectAsync(provider.StreamAsync(
                new ModelRequest("model", [new ModelMessage(ModelRole.User, "x")], []),
                TestContext.Current.CancellationToken)));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task HonorsCallerCancellationDuringBackoff()
    {
        var handler = new QueueStubHandler([
            QueuedResponse.Failure((HttpStatusCode)429),
            QueuedResponse.Failure((HttpStatusCode)429),
            Success
        ]);
        using var cancellation = new CancellationTokenSource();
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), new OpenAiProviderOptions
        {
            BaseUri = new Uri("https://example.test/v1"),
            Timeout = TimeSpan.FromMinutes(5)
        }, (_, token) =>
        {
            cancellation.Cancel();
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await CollectAsync(provider.StreamAsync(
                new ModelRequest("model", [new ModelMessage(ModelRole.User, "x")], []),
                cancellation.Token));
        });

        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task BoundsBackoffByOverallTimeout()
    {
        var handler = new QueueStubHandler([
            QueuedResponse.Failure((HttpStatusCode)429, retryAfter: TimeSpan.FromMinutes(5)),
            Success
        ]);
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), new OpenAiProviderOptions
        {
            BaseUri = new Uri("https://example.test/v1"),
            Timeout = TimeSpan.FromMilliseconds(150)
        });

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await CollectAsync(provider.StreamAsync(
                new ModelRequest("model", [new ModelMessage(ModelRole.User, "x")], []),
                TestContext.Current.CancellationToken)));

        Assert.Equal(1, handler.Attempts);
    }

    private static OpenAiCompatibleProvider CreateProvider(QueueStubHandler handler, Action<TimeSpan> recordDelay) =>
        new(new HttpClient(handler), new OpenAiProviderOptions
        {
            BaseUri = new Uri("https://example.test/v1"),
            Timeout = TimeSpan.FromMinutes(5)
        }, (delay, _) =>
        {
            recordDelay(delay);
            return Task.CompletedTask;
        });

    private static async Task<List<ModelStreamEvent>> CollectAsync(IAsyncEnumerable<ModelStreamEvent> source)
    {
        var result = new List<ModelStreamEvent>();
        await foreach (var item in source)
        {
            result.Add(item);
        }

        return result;
    }

    internal sealed class QueuedResponse
    {
        public required HttpStatusCode StatusCode { get; init; }

        public string Content { get; init; } = string.Empty;

        public TimeSpan? RetryAfter { get; init; }

        public static QueuedResponse Failure(HttpStatusCode statusCode, TimeSpan? retryAfter = null) => new()
        {
            StatusCode = statusCode,
            Content = "endpoint busy",
            RetryAfter = retryAfter
        };
    }

    internal sealed class QueueStubHandler : HttpMessageHandler
    {
        private readonly Queue<QueuedResponse> _responses;

        public QueueStubHandler(IEnumerable<QueuedResponse> responses)
        {
            _responses = new Queue<QueuedResponse>(responses);
        }

        public int Attempts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            if (request.Content is not null)
            {
                _ = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("The queue ran out of responses.");
            }

            var next = _responses.Dequeue();
            var response = new HttpResponseMessage(next.StatusCode)
            {
                Content = new StringContent(next.Content, Encoding.UTF8, "text/event-stream")
            };
            if (next.RetryAfter is { } retryAfter)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
            }

            return response;
        }
    }
}
