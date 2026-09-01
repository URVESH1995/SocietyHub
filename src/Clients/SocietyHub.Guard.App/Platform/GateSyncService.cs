using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SocietyHub.Client.Shared.Api;

namespace SocietyHub.Guard.App.Platform;

/// <summary>
/// Drains the offline queue whenever the tablet has a connection again.
///
/// A background service rather than something the guard triggers. Nobody at a gate is going to
/// remember to press Sync, and an entry that sits unsent because a button was not pressed is
/// indistinguishable from one that was never recorded.
///
/// Two triggers, because neither alone is enough. Connectivity changes catch the common case —
/// the router comes back — but Android's connectivity events are unreliable enough that a
/// timer is needed as a backstop, and a tablet that came up before the network did would
/// otherwise never fire one.
/// </summary>
public sealed class GateSyncService : IDisposable
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(2);

    private readonly OfflineQueue _queue;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GateSyncService> _logger;

    private readonly CancellationTokenSource _stopping = new();
    private Timer? _timer;

    public GateSyncService(
        OfflineQueue queue, IHttpClientFactory httpFactory, ILogger<GateSyncService> logger)
    {
        _queue = queue;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>Fires after every drain so the shell can refresh its badge.</summary>
    public event Action<SyncResult>? Synced;

    public bool IsOnline { get; private set; } = true;

    public void Start()
    {
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;

        IsOnline = Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

        _timer = new Timer(
            _ => _ = SyncAsync(), state: null, dueTime: TimeSpan.Zero, period: SweepInterval);
    }

    public void Dispose()
    {
        Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;

        _stopping.Cancel();
        _timer?.Dispose();
        _stopping.Dispose();
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        IsOnline = e.NetworkAccess == NetworkAccess.Internet;

        if (IsOnline)
        {
            _ = SyncAsync();
        }
    }

    /// <summary>
    /// Sends whatever is queued.
    ///
    /// Attempted even when the platform says there is no connection. Android reports
    /// NetworkAccess.Internet for a captive portal and sometimes reports none while a
    /// perfectly good connection exists — a real request is the only honest test, and the
    /// cost of a failed one is a caught exception.
    /// </summary>
    public async Task SyncAsync()
    {
        if (_stopping.IsCancellationRequested || _queue.Depth == 0)
        {
            return;
        }

        try
        {
            var http = _httpFactory.CreateClient(nameof(SocietyHubApiClient));

            var result = await _queue.DrainAsync(
                (action, ct) => SendAsync(http, action, ct), _stopping.Token);

            if (result.Sent > 0 || result.Parked.Count > 0)
            {
                _logger.LogInformation(
                    "Synced {Sent} queued gate actions, {Remaining} remaining, {Parked} parked.",
                    result.Sent,
                    result.Remaining,
                    result.Parked.Count);
            }

            Synced?.Invoke(result);
        }
        catch (OperationCanceledException)
        {
            // The app is shutting down. The queue is already on disk.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Queue drain failed. It will be retried.");
        }
    }

    private static async Task SendAsync(
        HttpClient http, QueuedAction action, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, action.Path)
        {
            Content = new StringContent(action.JsonBody, Encoding.UTF8, "application/json"),
        };

        // The key minted when the guard performed the action, not now. This is what makes a
        // retry after a lost response safe: the server recognises the repeat and does not
        // check the same visitor in twice.
        request.Headers.TryAddWithoutValidation(
            "Idempotency-Key", action.IdempotencyKey.ToString());

        // The server records when it happened, not when it arrived. A gate log that timestamps
        // a 7am entry as 11am — because that is when the network returned — is not a log
        // anyone can use to answer "who came in before the theft".
        request.Headers.TryAddWithoutValidation(
            "X-SocietyHub-Occurred-At", action.OccurredAtUtc.ToString("O"));

        using var response = await http.SendAsync(request, ct);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? code = null;

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(ct);
            code = problem?.Code;
        }
        catch (JsonException)
        {
            // A proxy returned HTML. The status code still tells the queue what to do.
        }

        throw new ApiException(
            response.StatusCode, code, $"{action.Description} was rejected.");
    }

    private sealed record ProblemPayload(string? Code);
}
