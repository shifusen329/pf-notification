using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PfNotification.Ntfy;

/// <summary>One line of an ntfy JSON stream. <see cref="Time"/> is unix seconds; only <c>message</c> events carry text.</summary>
public sealed record NtfyStreamMessage(string Id, long Time, string Event, string Topic, string Message, string Title)
{
    public const string MessageEvent = "message";
    public const string OpenEvent = "open";
    public const string KeepaliveEvent = "keepalive";
}

public enum SubscriberState
{
    /// <summary>Not started yet, or stopped by the owner.</summary>
    Stopped,

    Connecting,

    Connected,

    /// <summary>Lost the stream; waiting out the backoff before the next attempt.</summary>
    Reconnecting,

    /// <summary>401: the token is wrong. Stays stopped until the owner makes a new subscriber.</summary>
    AuthRejected,

    /// <summary>403: the token can't read the topic. Stays stopped.</summary>
    Forbidden,

    /// <summary>Invalid settings or another 4xx. Stays stopped.</summary>
    Failed,
}

/// <summary>Where the stream is at. <see cref="Detail"/> is safe to show and log: it never contains the token.</summary>
public sealed record SubscriberStatus(SubscriberState State, DateTime Since, int Attempt, string Detail)
{
    /// <summary>The subscriber gave up; only new settings (a new subscriber) change that.</summary>
    public bool IsTerminal => State is SubscriberState.AuthRejected or SubscriberState.Forbidden or SubscriberState.Failed;
}

/// <summary>Test seams and tunables. The defaults are the production values.</summary>
public sealed record NtfySubscriberOptions
{
    public HttpMessageHandler? Handler { get; init; }

    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }

    public Func<long>? NowUnix { get; init; }

    /// <summary>Spreads reconnects out; the default is ±25 %. Tests pass the identity.</summary>
    public Func<TimeSpan, TimeSpan>? Jitter { get; init; }

    /// <summary>No line for this long (ntfy keeps alive every 45 s) means the connection is dead.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(90);
}

/// <summary>
/// Holds <c>GET {server}/{topic}/json</c> open and queues the messages published to the topic. Single use: Start once,
/// Stop once. Reconnects with backoff after network errors, end of stream and idle timeouts; stops for good on 401,
/// 403 and other 4xx until the owner makes a new subscriber. On reconnect it asks for messages since the last event
/// (at most <see cref="CatchUpWindowSeconds"/> back), so a short blip loses nothing; the consumer must still drop
/// stale and duplicate messages. Pure .NET: it never touches Dalamud, so it can be unit-tested.
/// </summary>
public sealed class NtfySubscriber
{
    public const int MaxQueue = 32;
    public const int CatchUpWindowSeconds = 60;

    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    private readonly NtfySettings settings;
    private readonly string userAgent;
    private readonly Action<string> log;
    private readonly NtfySubscriberOptions options;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Func<long> nowUnix;
    private readonly Func<TimeSpan, TimeSpan> jitter;
    private readonly ConcurrentQueue<NtfyStreamMessage> queue = new();
    private readonly CancellationTokenSource stop = new();

    private volatile SubscriberStatus status = new(SubscriberState.Stopped, DateTime.Now, 0, "not started");
    private long lastEventUnix;
    private int started;
    private Task loop = Task.CompletedTask;

    /// <param name="log">Receives diagnostic lines (never the token).</param>
    public NtfySubscriber(NtfySettings settings, string userAgent, Action<string> log, NtfySubscriberOptions? options = null)
    {
        this.settings = settings;
        this.userAgent = userAgent;
        this.log = log;
        this.options = options ?? new NtfySubscriberOptions();
        delay = this.options.Delay ?? Task.Delay;
        nowUnix = this.options.NowUnix ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        jitter = this.options.Jitter ?? DefaultJitter;
    }

    public SubscriberStatus Status => status;

    /// <summary>Server time of the last event of any kind, including keepalives; 0 before the first.</summary>
    public long LastEventUnix => Volatile.Read(ref lastEventUnix);

    public int QueuedCount => queue.Count;

    /// <summary>Completes once the loop has exited, after <see cref="Stop"/> or a terminal state.</summary>
    public Task Completion => loop;

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return;
        }

        loop = Task.Run(RunAsync);
    }

    /// <summary>Ends the loop. Safe to call more than once, from any thread, and before <see cref="Start"/>.</summary>
    public void Stop()
    {
        try
        {
            stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The loop already finished.
        }
    }

    public bool TryPeek(out NtfyStreamMessage message) => queue.TryPeek(out message!);

    public bool TryDequeue(out NtfyStreamMessage message) => queue.TryDequeue(out message!);

    /// <summary>Parses one stream line. Null for anything that isn't a JSON object with a string <c>event</c>.</summary>
    public static NtfyStreamMessage? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if ((root.ValueKind != JsonValueKind.Object)
                || !root.TryGetProperty("event", out var ev)
                || (ev.ValueKind != JsonValueKind.String))
            {
                return null;
            }

            var time = (root.TryGetProperty("time", out var t) && (t.ValueKind == JsonValueKind.Number) && t.TryGetInt64(out var value))
                ? value
                : 0;
            return new NtfyStreamMessage(
                Text(root, "id"),
                time,
                ev.GetString() ?? string.Empty,
                Text(root, "topic"),
                Text(root, "message"),
                Text(root, "title"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonElement root, string name) =>
        (root.TryGetProperty(name, out var p) && (p.ValueKind == JsonValueKind.String)) ? (p.GetString() ?? string.Empty) : string.Empty;

    private static TimeSpan DefaultJitter(TimeSpan wait) => wait * (0.75 + (Random.Shared.NextDouble() * 0.5));

    private void Set(SubscriberState state, int attempt, string detail) => status = new SubscriberStatus(state, DateTime.Now, attempt, detail);

    private async Task RunAsync()
    {
        var ct = stop.Token;
        var invalid = NtfyClient.Validate(settings);
        if (invalid is not null)
        {
            Set(SubscriberState.Failed, 0, invalid);
            return;
        }

        var handler = options.Handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        using var http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan, // a stream never completes; idle detection is per line
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

        var baseUrl = $"{settings.ServerUrl.TrimEnd('/')}/{settings.Topic}/json";
        var failures = 0; // consecutive connections without a keepalive or message
        var hadStream = false; // a connection succeeded before: catch up on reconnect
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Set(hadStream ? SubscriberState.Reconnecting : SubscriberState.Connecting, failures, hadStream ? "reconnecting" : "connecting");
                var url = hadStream ? $"{baseUrl}?since={Math.Max(LastEventUnix, nowUnix() - CatchUpWindowSeconds)}" : baseUrl;
                string detail;
                TimeSpan? wait = null;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
                    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    var code = (int)resp.StatusCode;
                    if (resp.IsSuccessStatusCode)
                    {
                        hadStream = true;
                        var (sawData, why) = await ReadStreamAsync(resp, ct).ConfigureAwait(false);
                        failures = sawData ? 1 : (failures + 1);
                        detail = why;
                    }
                    else
                    {
                        var error = await NtfyClient.ReadErrorAsync(resp, ct).ConfigureAwait(false);
                        switch (code)
                        {
                            case 401:
                                Set(SubscriberState.AuthRejected, failures, $"token rejected: {error}");
                                return;
                            case 403:
                                Set(SubscriberState.Forbidden, failures, $"no read access to the topic: {error}");
                                return;
                            case 429:
                                failures++;
                                detail = $"rate limited: {error}";
                                wait = NtfyClient.RetryAfter(resp) is { } retryAfter ? ((retryAfter > MaxRetryAfter) ? MaxRetryAfter : retryAfter) : null;
                                break;
                            case >= 500:
                                failures++;
                                detail = $"server error {code}: {error}";
                                break;
                            default:
                                Set(SubscriberState.Failed, failures, $"request refused ({code}): {error}");
                                return;
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    failures++;
                    detail = ex.Message;
                }

                if (ct.IsCancellationRequested)
                {
                    return;
                }

                var backoff = wait ?? jitter(Backoff[Math.Min(Math.Max(failures, 1) - 1, Backoff.Length - 1)]);
                Set(SubscriberState.Reconnecting, failures, $"{detail}; retrying in {backoff.TotalSeconds:0} s");
                log($"stream lost ({detail}); retrying in {backoff.TotalSeconds:0} s");
                try
                {
                    await delay(backoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        finally
        {
            if (ct.IsCancellationRequested)
            {
                Set(SubscriberState.Stopped, failures, "stopped");
            }

            stop.Dispose();
        }
    }

    /// <summary>Reads until the stream ends, goes idle or fails. Only throws when <paramref name="ct"/> is cancelled.</summary>
    private async Task<(bool SawData, string Detail)> ReadStreamAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var sawData = false;
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (true)
            {
                string? line;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    idle.CancelAfter(options.IdleTimeout);
                    try
                    {
                        line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        return (sawData, $"no data for {options.IdleTimeout.TotalSeconds:0} s");
                    }
                }

                if (line is null)
                {
                    return (sawData, "stream ended");
                }

                var message = ParseLine(line);
                if (message is null)
                {
                    log("ignored a line that isn't an ntfy event");
                    continue;
                }

                if (message.Time > 0)
                {
                    Volatile.Write(ref lastEventUnix, Math.Max(LastEventUnix, message.Time));
                }

                switch (message.Event)
                {
                    case NtfyStreamMessage.OpenEvent:
                        Set(SubscriberState.Connected, 0, "connected");
                        break;
                    case NtfyStreamMessage.KeepaliveEvent:
                        sawData = true;
                        break;
                    case NtfyStreamMessage.MessageEvent:
                        sawData = true;
                        if (queue.Count >= MaxQueue)
                        {
                            log($"queue full ({MaxQueue}); dropped message {message.Id}");
                        }
                        else
                        {
                            queue.Enqueue(message);
                        }

                        break;
                }
            }
        }
        catch (IOException ex)
        {
            return (sawData, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return (sawData, ex.Message);
        }
    }
}
