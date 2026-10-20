using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using PfNotification.Ntfy;

namespace PfNotification.Tests;

public sealed class NtfySubscriberTests
{
    private const string Token = "tk_testtokentesttokentesttoken";
    private const long Now = 1_000_000;

    private static readonly NtfySettings Settings = new("https://ntfy.example.com", "pf-alerts-chat", Token);

    [Fact]
    public void ParsesAMessageLine()
    {
        var line = """{"id":"abc","time":1700000000,"event":"message","topic":"pf-alerts-chat","message":"hello","title":"t"}""";

        var message = NtfySubscriber.ParseLine(line);

        Assert.NotNull(message);
        Assert.Equal("abc", message.Id);
        Assert.Equal(1700000000, message.Time);
        Assert.Equal("message", message.Event);
        Assert.Equal("pf-alerts-chat", message.Topic);
        Assert.Equal("hello", message.Message);
        Assert.Equal("t", message.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[1, 2]")]
    [InlineData("""{"id":"x","time":1}""")]
    [InlineData("""{"event":5}""")]
    public void UnparseableLinesAreNull(string line)
    {
        Assert.Null(NtfySubscriber.ParseLine(line));
    }

    [Fact]
    public void MissingFieldsDefaultToEmpty()
    {
        var message = NtfySubscriber.ParseLine("""{"event":"keepalive"}""");

        Assert.NotNull(message);
        Assert.Equal(string.Empty, message.Id);
        Assert.Equal(0, message.Time);
        Assert.Equal(string.Empty, message.Message);
    }

    [Fact]
    public async Task OnlyMessageEventsAreQueuedAndEveryEventUpdatesTheClock()
    {
        var feed = new Feed();
        var (subscriber, handler, _, _) = Start(feed);

        await feed.WriteLine("""{"id":"o","time":999990,"event":"open","topic":"pf-alerts-chat"}""");
        await WaitFor(() => subscriber.Status.State == SubscriberState.Connected);
        await feed.WriteLine("""{"id":"k","time":999995,"event":"keepalive","topic":"pf-alerts-chat"}""");
        await feed.WriteLine("""{"id":"m","time":999996,"event":"message","topic":"pf-alerts-chat","message":"hi"}""");
        await WaitFor(() => subscriber.QueuedCount == 1);

        Assert.Equal(999996, subscriber.LastEventUnix);
        Assert.True(subscriber.TryPeek(out var peeked));
        Assert.Equal("hi", peeked.Message);
        Assert.Equal(1, subscriber.QueuedCount);
        Assert.True(subscriber.TryDequeue(out var dequeued));
        Assert.Equal("m", dequeued.Id);
        Assert.False(subscriber.TryPeek(out _));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://ntfy.example.com/pf-alerts-chat/json", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, request.Headers.Authorization.Parameter);

        await StopAsync(subscriber);
    }

    [Fact]
    public async Task ReconnectsAfterEndOfStreamWithABoundedSince()
    {
        var first = new Feed();
        var second = new Feed();
        var (subscriber, handler, delays, _) = Start(first, second);

        await first.WriteLine("""{"id":"o","time":999990,"event":"open","topic":"pf-alerts-chat"}""");
        await WaitFor(() => subscriber.Status.State == SubscriberState.Connected);
        first.Complete();
        await WaitFor(() => handler.Requests.Count == 2);

        Assert.Equal("?since=999990", handler.Requests[1].RequestUri!.Query);
        Assert.Equal([TimeSpan.FromSeconds(1)], delays);

        await StopAsync(subscriber);
    }

    [Fact]
    public async Task SinceNeverReachesFurtherBackThanTheCatchUpWindow()
    {
        var first = new Feed();
        var (subscriber, handler, _, _) = Start(first, new Feed());

        await first.WriteLine("""{"id":"o","time":900000,"event":"open","topic":"pf-alerts-chat"}""");
        await WaitFor(() => subscriber.Status.State == SubscriberState.Connected);
        first.Complete();
        await WaitFor(() => handler.Requests.Count == 2);

        Assert.Equal($"?since={Now - NtfySubscriber.CatchUpWindowSeconds}", handler.Requests[1].RequestUri!.Query);

        await StopAsync(subscriber);
    }

    [Fact]
    public async Task BackoffGrowsWithoutDataAndResetsAfterAKeepalive()
    {
        var feeds = Enumerable.Range(0, 8).Select(_ => new Feed()).ToArray();
        var (subscriber, handler, delays, _) = Start(feeds.Append(new Feed()).ToArray());

        // Seven streams that end without any data, then one with a keepalive, then one that ends again.
        for (var i = 0; i < 7; i++)
        {
            await WaitFor(() => handler.Requests.Count == i + 1);
            feeds[i].Complete();
        }

        await WaitFor(() => handler.Requests.Count == 8);
        await feeds[7].WriteLine("""{"id":"k","time":999999,"event":"keepalive","topic":"pf-alerts-chat"}""");
        await WaitFor(() => subscriber.LastEventUnix == 999999);
        feeds[7].Complete();
        await WaitFor(() => handler.Requests.Count == 9);

        var seconds = delays.Select(d => (int)d.TotalSeconds).ToArray();
        Assert.Equal([1, 2, 5, 10, 30, 60, 60, 1], seconds);
        Assert.Equal(SubscriberState.Reconnecting, subscriber.Status.State);

        await StopAsync(subscriber);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, SubscriberState.AuthRejected)]
    [InlineData(HttpStatusCode.Forbidden, SubscriberState.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest, SubscriberState.Failed)]
    [InlineData(HttpStatusCode.NotFound, SubscriberState.Failed)]
    public async Task ClientErrorsStopForGood(HttpStatusCode status, SubscriberState expected)
    {
        var (subscriber, handler, delays, _) = Start(Respond(status, """{"code":40101,"http":401,"error":"unauthorized"}"""));

        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(expected, subscriber.Status.State);
        Assert.True(subscriber.Status.IsTerminal);
        Assert.Contains("unauthorized", subscriber.Status.Detail);
        Assert.DoesNotContain(Token, subscriber.Status.Detail);
        Assert.Single(handler.Requests);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task RateLimitHonoursRetryAfterAndServerErrorsBackOff()
    {
        var limited = Respond(HttpStatusCode.TooManyRequests);
        var (subscriber, handler, delays, _) = Start(
            r =>
            {
                var resp = limited(r);
                resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return resp;
            },
            Respond(HttpStatusCode.ServiceUnavailable),
            new Feed().Respond);

        await WaitFor(() => handler.Requests.Count == 3);

        Assert.Equal([TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(2)], delays);

        await StopAsync(subscriber);
    }

    [Fact]
    public async Task NetworkErrorsAreRetriedAndNeverLeakTheToken()
    {
        var (subscriber, handler, delays, _) = Start(_ => throw new HttpRequestException("connection refused"), new Feed().Respond);

        await WaitFor(() => handler.Requests.Count == 2);

        Assert.Equal([TimeSpan.FromSeconds(1)], delays);
        Assert.DoesNotContain(Token, subscriber.Status.Detail);

        await StopAsync(subscriber);
    }

    [Fact]
    public async Task InvalidSettingsFailWithoutARequest()
    {
        var handler = new ScriptedHandler([]);
        var subscriber = new NtfySubscriber(Settings with { ServerUrl = "http://ntfy.example.com" }, "PfNotification.Tests/1.0", _ => { }, new NtfySubscriberOptions { Handler = handler });
        subscriber.Start();

        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SubscriberState.Failed, subscriber.Status.State);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StopEndsAPendingRead()
    {
        var feed = new Feed();
        var (subscriber, handler, _, _) = Start(feed);
        await feed.WriteLine("""{"id":"o","time":999990,"event":"open","topic":"pf-alerts-chat"}""");
        await WaitFor(() => subscriber.Status.State == SubscriberState.Connected);

        subscriber.Stop();
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SubscriberState.Stopped, subscriber.Status.State);
        Assert.Single(handler.Requests);
        subscriber.Stop(); // idempotent after the loop disposed its token source
    }

    [Fact]
    public async Task IdleStreamIsReconnected()
    {
        var (subscriber, handler, delays, logged) = Start([new Feed(), new Feed()], new NtfySubscriberOptions { IdleTimeout = TimeSpan.FromMilliseconds(50) });

        await WaitFor(() => handler.Requests.Count >= 2);
        await WaitFor(() => Logged(logged, "no data"));

        // The silent streams keep timing out, so only the first backoff is deterministic here.
        lock (delays)
        {
            Assert.Equal(TimeSpan.FromSeconds(1), delays[0]);
        }

        await StopAsync(subscriber);
    }

    [Fact]
    public async Task QueueIsCappedAndDropsAreLogged()
    {
        var feed = new Feed();
        var (subscriber, _, _, logged) = Start(feed);

        for (var i = 0; i <= NtfySubscriber.MaxQueue; i++)
        {
            await feed.WriteLine($$$"""{"id":"m{{{i}}}","time":999990,"event":"message","topic":"pf-alerts-chat","message":"{{{i}}}"}""");
        }

        await WaitFor(() => Logged(logged, "queue full"));

        Assert.Equal(NtfySubscriber.MaxQueue, subscriber.QueuedCount);
        Assert.True(subscriber.TryDequeue(out var first));
        Assert.Equal("m0", first.Id);

        await StopAsync(subscriber);
    }

    private static (NtfySubscriber Subscriber, ScriptedHandler Handler, List<TimeSpan> Delays, List<string> Logged) Start(params Feed[] feeds) =>
        Start(feeds, null);

    private static (NtfySubscriber Subscriber, ScriptedHandler Handler, List<TimeSpan> Delays, List<string> Logged) Start(Feed[] feeds, NtfySubscriberOptions? options) =>
        Start(options, feeds.Select(f => (Func<HttpRequestMessage, HttpResponseMessage>)f.Respond).ToArray());

    private static (NtfySubscriber Subscriber, ScriptedHandler Handler, List<TimeSpan> Delays, List<string> Logged) Start(
        params Func<HttpRequestMessage, HttpResponseMessage>[] responses) =>
        Start(null, responses);

    private static (NtfySubscriber Subscriber, ScriptedHandler Handler, List<TimeSpan> Delays, List<string> Logged) Start(
        NtfySubscriberOptions? options,
        params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        var handler = new ScriptedHandler(responses);
        var delays = new List<TimeSpan>();
        var logged = new List<string>();
        var subscriber = new NtfySubscriber(
            Settings,
            "PfNotification.Tests/1.0",
            line =>
            {
                lock (logged)
                {
                    logged.Add(line);
                }
            },
            (options ?? new NtfySubscriberOptions()) with
            {
                Handler = handler,
                Delay = (delay, _) =>
                {
                    lock (delays)
                    {
                        delays.Add(delay);
                    }

                    return Task.CompletedTask;
                },
                NowUnix = () => Now,
                Jitter = wait => wait,
            });
        subscriber.Start();
        return (subscriber, handler, delays, logged);
    }

    private static async Task StopAsync(NtfySubscriber subscriber)
    {
        subscriber.Stop();
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SubscriberState.Stopped, subscriber.Status.State);
    }

    private static bool Logged(List<string> logged, string fragment)
    {
        lock (logged)
        {
            return logged.Any(line => line.Contains(fragment));
        }
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 5_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "timed out waiting for the subscriber");
            await Task.Delay(10);
        }
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Respond(HttpStatusCode status, string? body = null) =>
        _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body ?? string.Empty, Encoding.UTF8, (body is null) ? "text/plain" : "application/json"),
        };

    /// <summary>A streaming response body the test writes to line by line.</summary>
    private sealed class Feed
    {
        private readonly Pipe pipe = new();

        public HttpResponseMessage Respond(HttpRequestMessage request) =>
            new(HttpStatusCode.OK) { Content = new StreamContent(pipe.Reader.AsStream()) };

        public async Task WriteLine(string line)
        {
            await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        }

        public void Complete() => pipe.Writer.Complete();
    }

    /// <summary>Scripted responses in order; once they run out, every request gets a stream that stays open and silent.</summary>
    private sealed class ScriptedHandler(IEnumerable<Func<HttpRequestMessage, HttpResponseMessage>> responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> responses = new(responses);
        private readonly List<HttpRequestMessage> requests = [];

        public IReadOnlyList<HttpRequestMessage> Requests
        {
            get
            {
                lock (requests)
                {
                    return requests.ToList();
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Func<HttpRequestMessage, HttpResponseMessage> next;
            lock (requests)
            {
                requests.Add(request);
                next = (responses.Count > 0) ? responses.Dequeue() : new Feed().Respond;
            }

            return Task.FromResult(next(request));
        }
    }
}
