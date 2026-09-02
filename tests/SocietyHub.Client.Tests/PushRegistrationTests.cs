using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SocietyHub.Client.Shared.Api;

namespace SocietyHub.Client.Tests;

/// <summary>
/// The push registration lifecycle.
///
/// Push carries almost everything, because SMS is reserved for emergencies on cost grounds. A
/// device whose token is stale receives nothing and reports no error — the server sends
/// happily to an address that no longer exists, and a resident simply stops hearing about
/// visitors at their gate without ever filing a bug.
///
/// The platform token itself needs a device. Everything around it — when to send, when not to,
/// what to do when the send fails — does not, and that is where the failures are.
/// </summary>
public sealed class PushRegistrationTests
{
    private sealed class FakeTokenProvider : IDeviceTokenProvider
    {
        public string? Token { get; set; }

        public event Action<string>? TokenRefreshed;

        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Token);

        public void Rotate(string token)
        {
            Token = token;
            TokenRefreshed?.Invoke(token);
        }

        public bool HasSubscribers => TokenRefreshed is not null;
    }

    private sealed class InMemoryCache : IPushTokenCache
    {
        private string? _value;

        public Task<string?> GetAsync() => Task.FromResult(_value);

        public Task SetAsync(string? token)
        {
            _value = token;
            return Task.CompletedTask;
        }
    }

    /// <summary>Records what was sent, and can be told to fail.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Registered { get; } = [];

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (StatusCode == HttpStatusCode.OK)
            {
                Registered.Add(body);
            }

            return new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubTokenStore : ITokenStore
    {
        public Task<string?> GetAccessTokenAsync() => Task.FromResult<string?>("access");

        public Task<string?> GetRefreshTokenAsync() => Task.FromResult<string?>("refresh");

        public Task SaveAsync(string a, string r, DateTimeOffset e) => Task.CompletedTask;

        public Task ClearAsync() => Task.CompletedTask;
    }

    private static (PushRegistrationService Service, RecordingHandler Handler,
        FakeTokenProvider Provider, InMemoryCache Cache) Build(string? token = "device-token-1")
    {
        var handler = new RecordingHandler();

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") };
        var api = new SocietyHubApiClient(http, new StubTokenStore(), new ClientIdentity("android", "1.0.0"));

        var provider = new FakeTokenProvider { Token = token };
        var cache = new InMemoryCache();

        return (
            new PushRegistrationService(api, provider, cache, NullLogger<PushRegistrationService>.Instance),
            handler,
            provider,
            cache);
    }

    [Fact]
    public async Task A_new_token_is_registered()
    {
        var (service, handler, _, _) = Build();

        await service.StartAsync();

        Assert.Single(handler.Registered);
        Assert.Contains("device-token-1", handler.Registered[0]);
    }

    [Fact]
    public async Task An_unchanged_token_is_not_sent_again()
    {
        // Not merely an optimisation. Every launch of every app across 42,000 flats would
        // otherwise be a database write for data that did not change.
        var (service, handler, _, _) = Build();

        await service.StartAsync();
        await service.StartAsync();
        await service.StartAsync();

        Assert.Single(handler.Registered);
    }

    [Fact]
    public async Task A_rotated_token_is_registered_without_a_restart()
    {
        // The platform rotates on reinstall, on restore from backup, and sometimes for its own
        // reasons. An app that registers once at install and never listens goes silently
        // unreachable months later, and nobody connects the two events.
        var (service, handler, provider, _) = Build();

        await service.StartAsync();
        provider.Rotate("device-token-2");

        // The refresh handler is deliberately fire-and-forget, so give it a moment.
        await Task.Delay(150);

        Assert.Equal(2, handler.Registered.Count);
        Assert.Contains("device-token-2", handler.Registered[1]);
    }

    [Fact]
    public async Task No_token_is_not_an_error()
    {
        // Normal on a device where notification permission was refused, or an emulator with no
        // Play Services. The app must work without push, just less well.
        var (service, handler, _, _) = Build(token: null);

        await service.StartAsync();

        Assert.Empty(handler.Registered);
    }

    [Fact]
    public async Task A_failed_registration_is_not_cached_so_it_retries()
    {
        // The most important test here. Caching before the server confirms means one failed
        // call leaves the device permanently unreachable while appearing registered — and
        // nothing ever retries, because the cache says it is already done.
        var (service, handler, _, cache) = Build();
        handler.StatusCode = HttpStatusCode.InternalServerError;

        await service.StartAsync();

        Assert.Null(await cache.GetAsync());

        handler.StatusCode = HttpStatusCode.OK;
        await service.StartAsync();

        Assert.Single(handler.Registered);
        Assert.Equal("device-token-1", await cache.GetAsync());
    }

    [Fact]
    public async Task Registration_failing_never_throws()
    {
        // Push failing to register must not prevent an app from starting. A resident with no
        // notifications still needs to open the gate screen.
        var (service, handler, _, _) = Build();
        handler.StatusCode = HttpStatusCode.ServiceUnavailable;

        var exception = await Record.ExceptionAsync(() => service.StartAsync());

        Assert.Null(exception);
    }

    [Fact]
    public async Task Signing_out_clears_the_cache_so_the_next_user_registers()
    {
        // The token belongs to the device, but the registration belongs to the user. Leaving
        // the cache populated would make the next person's sign-in skip registration and
        // silently inherit nothing.
        var (service, handler, _, cache) = Build();

        await service.StartAsync();
        await service.StopAsync();

        Assert.Null(await cache.GetAsync());

        await service.StartAsync();

        Assert.Equal(2, handler.Registered.Count);
    }

    [Fact]
    public async Task Stopping_unsubscribes_from_rotations()
    {
        // A signed-out app that still reacts to a token refresh would call a society-scoped
        // endpoint with no session, once per rotation, forever.
        var (service, handler, provider, _) = Build();

        await service.StartAsync();
        await service.StopAsync();

        provider.Rotate("device-token-3");
        await Task.Delay(150);

        Assert.Single(handler.Registered);
    }
}
