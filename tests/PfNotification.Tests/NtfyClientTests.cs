using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PfNotification.Ntfy;

namespace PfNotification.Tests;

public sealed class NtfyClientTests
{
    private const string Token = "tk_testtokentesttokentesttoken";

    private static readonly NtfySettings Settings = new("https://ntfy.example.com/", "pf-alerts", Token);
    private static readonly NtfyMessage Message = new("Party full", "8/8: 2 tanks, 2 healers, 4 DPS", 4, ["busts_in_silhouette"]);

    [Fact]
    public async Task PublishesJsonToTheServerRootWithBearerAuth()
    {
        var (client, handler, _) = Create(Respond(HttpStatusCode.OK));

        var result = await client.PublishAsync(Settings, Message, CancellationToken.None);

        Assert.Equal(NtfyOutcome.Sent, result.Outcome);
        Assert.Equal(1, result.Attempts);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://ntfy.example.com/", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, request.Headers.Authorization.Parameter);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("pf-alerts", root.GetProperty("topic").GetString());
        Assert.Equal("Party full", root.GetProperty("title").GetString());
        Assert.Equal("8/8: 2 tanks, 2 healers, 4 DPS", root.GetProperty("message").GetString());
        Assert.Equal(4, root.GetProperty("priority").GetInt32());
        Assert.Equal("busts_in_silhouette", Assert.Single(root.GetProperty("tags").EnumerateArray().ToList()).GetString());
    }

    [Fact]
    public async Task ServerUrlWithoutTrailingSlashStillPostsToTheRoot()
    {
        var (client, handler, _) = Create(Respond(HttpStatusCode.OK));

        await client.PublishAsync(Settings with { ServerUrl = "https://ntfy.example.com" }, Message, CancellationToken.None);

        Assert.Equal("https://ntfy.example.com/", Assert.Single(handler.Requests).Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task PriorityIsClampedToNtfyRange()
    {
        var (client, handler, _) = Create(Respond(HttpStatusCode.OK));

        await client.PublishAsync(Settings, Message with { Priority = 9 }, CancellationToken.None);

        using var json = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal(5, json.RootElement.GetProperty("priority").GetInt32());
    }

    [Fact]
    public async Task RetriesServerErrorsWithBackoffThenSucceeds()
    {
        var (client, handler, delays) = Create(
            Respond(HttpStatusCode.ServiceUnavailable),
            Respond(HttpStatusCode.BadGateway),
            Respond(HttpStatusCode.OK));

        var result = await client.PublishAsync(Settings, Message, CancellationToken.None);

        Assert.Equal(NtfyOutcome.Sent, result.Outcome);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)], delays);
    }

    [Fact]
    public async Task GivesUpAfterMaxAttempts()
    {
        var (client, handler, _) = Create(
            Respond(HttpStatusCode.InternalServerError),
            Respond(HttpStatusCode.InternalServerError),
            Respond(HttpStatusCode.InternalServerError),
            Respond(HttpStatusCode.OK));

        var result = await client.PublishAsync(Settings, Message, CancellationToken.None);

        Assert.Equal(NtfyOutcome.ServerError, result.Outcome);
        Assert.Equal(NtfyClient.MaxAttempts, result.Attempts);
        Assert.Equal(NtfyClient.MaxAttempts, handler.Requests.Count);
    }

    [Fact]
    public async Task RetriesNetworkErrors()
    {
        var (client, _, _) = Create(_ => throw new HttpRequestException("connection refused"), Respond(HttpStatusCode.OK));

        var result = await client.PublishAsync(Settings, Message, CancellationToken.None);

        Assert.Equal(NtfyOutcome.Sent, result.Outcome);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task RateLimitHonoursRetryAfter()
    {
        var limited = Respond(HttpStatusCode.TooManyRequests);
        var (client, _, delays) = Create(
            r =>
            {
                var resp = limited(r);
                resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return resp;
            },
            Respond(HttpStatusCode.OK));

        var result = await client.PublishAsync(Settings, Message, CancellationToken.None);

        Assert.Equal(NtfyOutcome.Sent, result.Outcome);
        Assert.Equal([TimeSpan.FromSeconds(7)], delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, NtfyOutcome.AuthRejected)]
    [InlineData(HttpStatusCode.Forbidden, NtfyOutcome.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest, NtfyOutcome.ClientError)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, NtfyOutcome.ClientError)]
    public async Task DoesNotRetryClientErrors(HttpStatusCode status, NtfyOutcome expected)
    {
        var (client, handler, _) = Create(Respond(status), Respond(HttpStatusCode.OK));

        var result = await client.PublishAsync(Settings, Message, CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal((int)status, result.Status);
        Assert.Equal(1, result.Attempts);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ReportsTheNtfyErrorMessage()
    {
        var (client, _, _) = Create(Respond(HttpStatusCode.Forbidden, """{"code":40301,"http":403,"error":"forbidden"}"""));

        var result = await client.PublishAsync(Settings, Message, CancellationToken.None);

        Assert.Equal(NtfyOutcome.Forbidden, result.Outcome);
        Assert.Contains("forbidden", result.Detail);
    }

    [Fact]
    public async Task NonJsonErrorBodyFallsBackToTheStatusLine()
    {
        var (client, _, _) = Create(Respond(HttpStatusCode.BadRequest, "<html>nginx</html>"));

        var result = await client.PublishAsync(Settings, Message, CancellationToken.None);

        Assert.Equal(NtfyOutcome.ClientError, result.Outcome);
        Assert.Contains("Bad Request", result.Detail);
    }

    [Fact]
    public async Task CancellationReturnsCancelled()
    {
        var (client, _, _) = Create(Respond(HttpStatusCode.OK));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await client.PublishAsync(Settings, Message, cts.Token);

        Assert.Equal(NtfyOutcome.Cancelled, result.Outcome);
    }

    [Theory]
    [InlineData("http://ntfy.example.com", "pf-alerts", Token)]
    [InlineData("not a url", "pf-alerts", Token)]
    [InlineData("https://ntfy.example.com", "", Token)]
    [InlineData("https://ntfy.example.com", "pf alerts", Token)]
    [InlineData("https://ntfy.example.com", "pf-alerts", "")]
    [InlineData("https://ntfy.example.com", "pf-alerts", Token + "\n")]
    [InlineData("https://ntfy.example.com", "pf-alerts", "tk_with space")]
    public async Task InvalidSettingsAreRejectedWithoutSending(string url, string topic, string token)
    {
        var (client, handler, _) = Create(Respond(HttpStatusCode.OK));

        var result = await client.PublishAsync(new NtfySettings(url, topic, token), Message, CancellationToken.None);

        Assert.Equal(NtfyOutcome.InvalidConfig, result.Outcome);
        Assert.Empty(handler.Requests);
        if (token.Length > 0)
        {
            Assert.DoesNotContain(token.Trim(), result.Detail);
        }
    }

    [Fact]
    public async Task TokenNeverAppearsInFailureDetails()
    {
        var failures = new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            Respond(HttpStatusCode.Unauthorized),
            Respond(HttpStatusCode.Forbidden),
            Respond(HttpStatusCode.BadRequest),
            Respond(HttpStatusCode.InternalServerError),
            _ => throw new HttpRequestException("boom"),
        };

        foreach (var failure in failures)
        {
            var (client, _, _) = Create(failure, failure, failure);
            var result = await client.PublishAsync(Settings, Message, CancellationToken.None);

            Assert.False(result.Ok);
            Assert.DoesNotContain(Token, result.Detail);
        }
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Respond(HttpStatusCode status, string? body = null) =>
        _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body ?? string.Empty, Encoding.UTF8, (body is null) ? "text/plain" : "application/json"),
        };

    private static (NtfyClient Client, FakeHandler Handler, List<TimeSpan> Delays) Create(
        params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        var handler = new FakeHandler(responses);
        var delays = new List<TimeSpan>();
        var client = new NtfyClient("PfNotification.Tests/1.0", handler, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });
        return (client, handler, delays);
    }

    private sealed class FakeHandler(IEnumerable<Func<HttpRequestMessage, HttpResponseMessage>> responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> responses = new(responses);

        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = (request.Content is null) ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            var next = (responses.Count > 0) ? responses.Dequeue() : Respond(HttpStatusCode.OK);
            return next(request);
        }
    }
}
