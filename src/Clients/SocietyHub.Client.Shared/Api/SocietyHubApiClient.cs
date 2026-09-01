using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SocietyHub.Client.Shared.Models;

namespace SocietyHub.Client.Shared.Api;

/// <summary>
/// Where a client keeps its tokens.
///
/// Per platform, because the right storage differs by an order of magnitude in safety:
/// the Keychain or Keystore on mobile, and browser storage on the web, which cannot protect a
/// refresh token from a script running on the same origin. That difference is why the web
/// build uses a short-lived access token and never persists the refresh token to local
/// storage — see <c>BrowserTokenStore</c> in the admin app.
/// </summary>
public interface ITokenStore
{
    Task<string?> GetAccessTokenAsync();

    Task<string?> GetRefreshTokenAsync();

    Task SaveAsync(string accessToken, string refreshToken, DateTimeOffset expiresAtUtc);

    Task ClearAsync();
}

/// <summary>
/// Raised when the server refuses the request in a way the UI has to handle specifically
/// rather than as a generic failure.
/// </summary>
public sealed class ApiException : Exception
{
    public ApiException(HttpStatusCode statusCode, string? code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// The machine-readable code from the server's ProblemDetails, e.g.
    /// <c>feature.not_enabled</c>. Clients branch on this, never on the message — the message
    /// is English for developers and is not what a resident sees.
    /// </summary>
    public string? Code { get; }

    public bool IsUpgradeRequired => (int)StatusCode == 426;

    public bool IsFeatureDisabled => (int)StatusCode == 402;

    public bool IsUnauthorised => StatusCode == HttpStatusCode.Unauthorized;
}

/// <summary>
/// The single HTTP entry point every client uses.
///
/// Hand-written rather than generated from OpenAPI, and that is a decision worth stating
/// plainly. Generation would need the six services running to produce their documents, which
/// makes an offline build impossible and a CI build dependent on a working database — and the
/// generated surface would still need this layer wrapped around it for tokens, the client
/// version header, refresh-on-401 and the offline queue. The typed methods below are the part
/// generation would have saved, and there are about forty of them.
///
/// The generation path is kept viable rather than discarded: <c>scripts/generate-clients.sh</c>
/// produces the OpenAPI documents and an NSwag client when the stack is running, and the shape
/// of the DTOs here matches what it emits.
/// </summary>
public sealed class SocietyHubApiClient
{
    private readonly HttpClient _http;
    private readonly ITokenStore _tokens;
    private readonly ClientIdentity _identity;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public SocietyHubApiClient(HttpClient http, ITokenStore tokens, ClientIdentity identity)
    {
        _http = http;
        _tokens = tokens;
        _identity = identity;
    }

    /// <summary>Raised when the session is gone and the shell must return to sign-in.</summary>
    public event Func<Task>? SessionExpired;

    // ---- gate ----------------------------------------------------------

    public Task<IReadOnlyList<VisitorView>> GetExpectedVisitorsAsync(CancellationToken ct = default) =>
        GetListAsync<VisitorView>("api/v1/gate/visitors/expected", ct);

    public Task<GatePassView> PreApproveVisitorAsync(
        PreApproveVisitorRequest request, CancellationToken ct = default) =>
        PostAsync<PreApproveVisitorRequest, GatePassView>(
            "api/v1/gate/visitors/pre-approve", request, ct);

    public Task ApproveVisitorAsync(Guid visitorId, CancellationToken ct = default) =>
        PostAsync($"api/v1/gate/visitors/{visitorId}/approve", ct);

    public Task DenyVisitorAsync(Guid visitorId, CancellationToken ct = default) =>
        PostAsync($"api/v1/gate/visitors/{visitorId}/deny", ct);

    public Task<VisitorView> CheckInAsync(CheckInRequest request, CancellationToken ct = default) =>
        PostAsync<CheckInRequest, VisitorView>("api/v1/gate/visitors/check-in", request, ct);

    public Task CheckOutAsync(Guid visitorId, CancellationToken ct = default) =>
        PostAsync($"api/v1/gate/visitors/{visitorId}/check-out", ct);

    // ---- helpdesk ------------------------------------------------------

    public Task<IReadOnlyList<ComplaintView>> GetMyComplaintsAsync(CancellationToken ct = default) =>
        GetListAsync<ComplaintView>("api/v1/helpdesk/complaints/mine", ct);

    public Task<ComplaintView> RaiseComplaintAsync(
        RaiseComplaintRequest request, CancellationToken ct = default) =>
        PostAsync<RaiseComplaintRequest, ComplaintView>(
            "api/v1/helpdesk/complaints", request, ct);

    public Task ReopenComplaintAsync(
        Guid complaintId, string reason, CancellationToken ct = default) =>
        PostAsync<object, object>(
            $"api/v1/helpdesk/complaints/{complaintId}/reopen", new { reason }, ct);

    // ---- notices and polls ---------------------------------------------

    public Task<IReadOnlyList<NoticeView>> GetNoticesAsync(CancellationToken ct = default) =>
        GetListAsync<NoticeView>("api/v1/notice/notices", ct);

    public Task AcknowledgeNoticeAsync(Guid noticeId, CancellationToken ct = default) =>
        PostAsync($"api/v1/notice/notices/{noticeId}/acknowledge", ct);

    public Task<IReadOnlyList<PollView>> GetPollsAsync(CancellationToken ct = default) =>
        GetListAsync<PollView>("api/v1/notice/polls", ct);

    public Task CastVoteAsync(
        Guid pollId, Guid flatId, Guid optionId, CancellationToken ct = default) =>
        PostAsync<object, object>(
            $"api/v1/notice/polls/{pollId}/vote", new { flatId, optionId }, ct);

    // ---- features ------------------------------------------------------

    /// <summary>
    /// What this society has. Fetched once at start-up so the shell can hide what is
    /// unavailable — a resident on Basic should never see a bulk-drive tab that only refuses
    /// them. Shaping only: every gated endpoint checks again on the server.
    /// </summary>
    public Task<FeatureManifestView> GetFeaturesAsync(CancellationToken ct = default) =>
        GetAsync<FeatureManifestView>("api/v1/society/features", ct);

    // ---- plumbing ------------------------------------------------------

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, content: null, ct);

        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
               ?? throw new ApiException(
                   response.StatusCode, "response.empty", $"{path} returned an empty body.");
    }

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string path, CancellationToken ct) =>
        await GetAsync<List<T>>(path, ct);

    private async Task PostAsync(string path, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Post, path, content: null, ct);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path, TRequest request, CancellationToken ct)
    {
        using var content = JsonContent.Create(request, options: Json);
        using var response = await SendAsync(HttpMethod.Post, path, content, ct);

        return await response.Content.ReadFromJsonAsync<TResponse>(Json, ct)
               ?? throw new ApiException(
                   response.StatusCode, "response.empty", $"{path} returned an empty body.");
    }

    /// <summary>
    /// Sends, refreshing once on a 401 and retrying.
    ///
    /// Once, not in a loop. A refresh that returns a token the server then rejects means
    /// something is wrong that retrying cannot fix, and a client that keeps trying turns one
    /// broken session into sustained load against the identity service — which is the service
    /// least able to absorb it during an incident.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var response = await SendOnceAsync(method, path, content, ct);

        if (response.StatusCode is not HttpStatusCode.Unauthorized)
        {
            return await EnsureSuccessAsync(response, path, ct);
        }

        response.Dispose();

        if (!await TryRefreshAsync(ct))
        {
            await _tokens.ClearAsync();

            if (SessionExpired is not null)
            {
                await SessionExpired.Invoke();
            }

            throw new ApiException(
                HttpStatusCode.Unauthorized, "session.expired", "The session has expired.");
        }

        return await EnsureSuccessAsync(await SendOnceAsync(method, path, content, ct), path, ct);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };

        // Identifies the build to the server's deprecation gate. Without it an old app cannot
        // be told it is old, and the only signal a resident gets is things quietly breaking.
        request.Headers.TryAddWithoutValidation(
            ClientIdentity.HeaderName, _identity.HeaderValue);

        var token = await _tokens.GetAccessTokenAsync();

        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        // A non-GET is retried by the transport on a transient failure, so it needs a key the
        // server can deduplicate on — otherwise a flaky connection turns one pre-approved
        // visitor into three.
        if (method != HttpMethod.Get)
        {
            request.Headers.TryAddWithoutValidation(
                "Idempotency-Key", Guid.CreateVersion7().ToString());
        }

        return await _http.SendAsync(request, ct);
    }

    private async Task<bool> TryRefreshAsync(CancellationToken ct)
    {
        var refreshToken = await _tokens.GetRefreshTokenAsync();

        if (string.IsNullOrEmpty(refreshToken))
        {
            return false;
        }

        using var content = JsonContent.Create(new { refreshToken }, options: Json);
        using var response = await _http.PostAsync("api/v1/identity/auth/refresh", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var tokens = await response.Content.ReadFromJsonAsync<TokenPairView>(Json, ct);

        if (tokens is null)
        {
            return false;
        }

        await _tokens.SaveAsync(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAtUtc);
        return true;
    }

    /// <summary>
    /// Turns a failure response into an <see cref="ApiException"/> carrying the server's own
    /// error code, so the UI can localise it rather than showing English prose from the wire.
    /// </summary>
    private static async Task<HttpResponseMessage> EnsureSuccessAsync(
        HttpResponseMessage response, string path, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        string? code = null;
        var message = $"{path} failed with {(int)response.StatusCode}.";

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemView>(Json, ct);

            if (problem is not null)
            {
                code = problem.Code;
                message = problem.Detail ?? problem.Title ?? message;
            }
        }
        catch (JsonException)
        {
            // A gateway or proxy returned HTML. The status code is still meaningful and is
            // what the caller branches on, so this is not worth failing over.
        }

        var status = response.StatusCode;
        response.Dispose();

        throw new ApiException(status, code, message);
    }
}

/// <summary>
/// Which build is talking to the server. Sent on every request so the deprecation gate can
/// warn or refuse — see <c>docs/API-VERSIONING.md</c>.
/// </summary>
public sealed record ClientIdentity(string Platform, string Version)
{
    public const string HeaderName = "X-SocietyHub-Client";

    public string HeaderValue => $"{Platform}/{Version}";
}
