using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PfNotification.Ntfy;

/// <summary>Where to publish. <see cref="Token"/> is a bearer token and must never be logged or echoed.</summary>
public sealed record NtfySettings(string ServerUrl, string Topic, string Token);

/// <summary>One notification. <see cref="Priority"/> is ntfy's 1 (min) to 5 (max); tags map to emoji on the phone.</summary>
public sealed record NtfyMessage(string Title, string Message, int Priority, IReadOnlyList<string> Tags);

public enum NtfyOutcome
{
    Sent,
    InvalidConfig,
    AuthRejected,
    Forbidden,
    RateLimited,
    ClientError,
    ServerError,
    NetworkError,
    Cancelled,
}

/// <summary>Result of a publish. <see cref="Detail"/> is safe to show and log: it never contains the token.</summary>
public sealed record NtfyResult(NtfyOutcome Outcome, int? Status, int Attempts, string Detail)
{
    public bool Ok => Outcome == NtfyOutcome.Sent;
}

/// <summary>
/// Publishes to a self-hosted ntfy server as JSON (POST to the server root, topic in the body), which avoids the
/// header-encoding limits of ntfy's header-based publishing. Retries network errors, 5xx and 429; never retries
/// other 4xx. Never throws: every failure comes back as an <see cref="NtfyResult"/>.
/// </summary>
public sealed class NtfyClient : IDisposable
{
    public const int MaxAttempts = 3;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];
    private static readonly Regex TopicPattern = new("^[-_A-Za-z0-9]{1,64}$", RegexOptions.CultureInvariant);

    private readonly HttpClient http;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    /// <param name="userAgent">Sent with every request, e.g. "PfNotification/0.1.0.0".</param>
    /// <param name="handler">Test seam. Defaults to a pooled <see cref="SocketsHttpHandler"/>.</param>
    /// <param name="delay">Test seam for retry waits. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public NtfyClient(string userAgent, HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        // A bounded connection lifetime makes a change of home IP show up through DNS without a plugin reload.
        handler ??= new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan, // per-request timeouts via linked CTS
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        this.delay = delay ?? Task.Delay;
    }

    /// <summary>Why <paramref name="settings"/> can't be used, or null when it can.</summary>
    public static string? Validate(NtfySettings settings)
    {
        if (!Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return "server URL is not a valid absolute URL";
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return "server URL must use https://";
        }

        if (!TopicPattern.IsMatch(settings.Topic))
        {
            return "topic must be 1-64 letters, digits, '-' or '_'";
        }

        if (string.IsNullOrEmpty(settings.Token))
        {
            return "access token is empty";
        }

        // A pasted newline or space would make AuthenticationHeaderValue throw FormatException.
        if (settings.Token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || (c > '~')))
        {
            return "access token contains spaces or invalid characters";
        }

        return null;
    }

    public async Task<NtfyResult> PublishAsync(NtfySettings settings, NtfyMessage message, CancellationToken ct)
    {
        var invalid = Validate(settings);
        if (invalid is not null)
        {
            return new NtfyResult(NtfyOutcome.InvalidConfig, null, 0, invalid);
        }

        var url = new Uri(settings.ServerUrl.TrimEnd('/') + "/");
        var body = JsonSerializer.Serialize(new
        {
            topic = settings.Topic,
            title = message.Title,
            message = message.Message,
            priority = Math.Clamp(message.Priority, 1, 5),
            tags = message.Tags,
        });

        var last = new NtfyResult(NtfyOutcome.NetworkError, null, 0, "not sent");
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            TimeSpan? retryAfter = null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(RequestTimeout);

                using var resp = await http.SendAsync(req, timeout.Token).ConfigureAwait(false);
                var status = (int)resp.StatusCode;
                if (resp.IsSuccessStatusCode)
                {
                    return new NtfyResult(NtfyOutcome.Sent, status, attempt, "sent");
                }

                var detail = await ReadErrorAsync(resp, timeout.Token).ConfigureAwait(false);
                switch (status)
                {
                    case 401:
                        return new NtfyResult(NtfyOutcome.AuthRejected, status, attempt, $"token rejected: {detail}");
                    case 403:
                        return new NtfyResult(NtfyOutcome.Forbidden, status, attempt, $"no write access to the topic: {detail}");
                    case 429:
                        last = new NtfyResult(NtfyOutcome.RateLimited, status, attempt, $"rate limited: {detail}");
                        retryAfter = RetryAfter(resp);
                        break;
                    case >= 500:
                        last = new NtfyResult(NtfyOutcome.ServerError, status, attempt, $"server error {status}: {detail}");
                        break;
                    default:
                        return new NtfyResult(NtfyOutcome.ClientError, status, attempt, $"request refused ({status}): {detail}");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new NtfyResult(NtfyOutcome.Cancelled, null, attempt, "cancelled");
            }
            catch (OperationCanceledException)
            {
                last = new NtfyResult(NtfyOutcome.NetworkError, null, attempt, $"timed out after {RequestTimeout.TotalSeconds:0} s");
            }
            catch (HttpRequestException ex)
            {
                last = new NtfyResult(NtfyOutcome.NetworkError, null, attempt, ex.Message);
            }
            catch (Exception ex)
            {
                return new NtfyResult(NtfyOutcome.NetworkError, null, attempt, ex.Message);
            }

            if (attempt == MaxAttempts)
            {
                break;
            }

            try
            {
                await delay(retryAfter ?? Backoff[Math.Min(attempt - 1, Backoff.Length - 1)], ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new NtfyResult(NtfyOutcome.Cancelled, null, attempt, "cancelled");
            }
        }

        return last;
    }

    public void Dispose()
    {
        http.Dispose();
    }

    /// <summary>ntfy errors are JSON like <c>{"code":40101,"http":401,"error":"unauthorized"}</c>.</summary>
    private static async Task<string> ReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var error) && (error.ValueKind == JsonValueKind.String))
            {
                return error.GetString() ?? resp.ReasonPhrase ?? "error";
            }
        }
        catch (Exception)
        {
            // Not JSON (e.g. an nginx error page); fall back to the status line.
        }

        return resp.ReasonPhrase ?? "error";
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage resp)
    {
        var header = resp.Headers.RetryAfter;
        var wait = header?.Delta ?? ((header?.Date is { } date) ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
        if (wait is not { } w)
        {
            return null;
        }

        return (w < TimeSpan.Zero) ? TimeSpan.Zero : ((w > MaxRetryAfter) ? MaxRetryAfter : w);
    }
}
