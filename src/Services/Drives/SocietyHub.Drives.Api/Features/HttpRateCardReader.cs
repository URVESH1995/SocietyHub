using System.Net;
using System.Net.Http.Json;
using SocietyHub.Caching;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Drives.Api.Features;

/// <summary>
/// Reads a vendor's rate card over HTTP, through service discovery.
///
/// <para>
/// The one synchronous cross-service call in the drives flow, and it is synchronous for a
/// reason: a resident tapping Join has to be told a price, now. Everything else between these
/// two services goes through events.
/// </para>
///
/// <para>
/// Cached aggressively, because a rate card is pinned at drive open and cannot legitimately
/// change for the life of the drive — the drive stores the card id precisely so a vendor
/// editing their prices mid-drive cannot alter what residents already agreed to. That makes a
/// stale read impossible rather than merely unlikely.
/// </para>
/// </summary>
public sealed class HttpRateCardReader : IRateCardReader
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);

    private readonly HttpClient _http;
    private readonly ICacheStore _cache;
    private readonly ILogger<HttpRateCardReader> _logger;

    public HttpRateCardReader(
        HttpClient http, ICacheStore cache, ILogger<HttpRateCardReader> logger)
    {
        _http = http;
        _cache = cache;
        _logger = logger;
    }

    public async Task<Result<long>> UnitPriceForAsync(
        Guid rateCardId, int units, CancellationToken cancellationToken = default)
    {
        var slabs = await GetSlabsAsync(rateCardId, cancellationToken);

        if (slabs is null)
        {
            // Deliberately a failure rather than a fallback price. Guessing here would charge
            // somebody a number nobody agreed to, and the caller — enrolment, or the lifecycle
            // worker at cut-off — is built to retry rather than proceed.
            return Error.Failure(
                "rate.unavailable",
                "The vendor's prices could not be read. Please try again shortly.");
        }

        var slab = slabs.FirstOrDefault(s =>
            units >= s.MinQuantity && (s.MaxQuantity is null || units <= s.MaxQuantity));

        return slab is null
            ? Error.Conflict(
                "rate.no_slab_for_quantity", $"No price is defined for {units} units.")
            : slab.UnitPricePaise;
    }

    private async Task<IReadOnlyList<SlabDto>?> GetSlabsAsync(
        Guid rateCardId, CancellationToken cancellationToken)
    {
        var key = CacheKey.ForPlatformWideData("rate-card", rateCardId.ToString("N"));

        var cached = await _cache.GetAsync<List<SlabDto>>(key, cancellationToken);

        if (cached is not null)
        {
            return cached;
        }

        try
        {
            var slabs = await _http.GetFromJsonAsync<List<SlabDto>>(
                $"api/rate-cards/{rateCardId}/slabs", cancellationToken);

            if (slabs is null || slabs.Count == 0)
            {
                return null;
            }

            await _cache.SetAsync(key, slabs, CacheDuration, cancellationToken);

            return slabs;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex, "Could not read rate card {RateCardId} from the vendor service.", rateCardId);

            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A timeout, not a caller cancellation. Same outcome, different cause, and worth
            // separating so the log says which.
            _logger.LogWarning("Timed out reading rate card {RateCardId}.", rateCardId);

            return null;
        }
    }

    private sealed record SlabDto(int MinQuantity, int? MaxQuantity, long UnitPricePaise);
}
