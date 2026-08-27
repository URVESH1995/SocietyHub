using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Gate.Api.Features;

public sealed class VisitorPhotoOptions
{
    public const string SectionName = "VisitorPhotos";

    public string ContainerName { get; set; } = "visitor-photos";

    /// <summary>
    /// How long a viewing link stays usable. Minutes, not hours: the link is handed to one
    /// resident looking at one arrival, and a long-lived URL is a photo that outlives the
    /// permission to see it.
    /// </summary>
    public TimeSpan ReadLinkLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Upload window for a guard device on a slow connection.</summary>
    public TimeSpan UploadLinkLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Bounded because the client compresses before sending. At roughly 40 KB a photo and
    /// 105,000 visits a day, the difference between 40 KB and an uncompressed 4 MB is about
    /// 1.7 TB a year of storage nobody budgeted for.
    /// </summary>
    public int MaxSizeBytes { get; set; } = 512 * 1024;
}

public interface IVisitorPhotoService
{
    /// <summary>A short-lived write-only link the guard device uploads directly to.</summary>
    Result<PhotoUploadTicket> CreateUploadTicket(Guid societyId, Guid passId);

    /// <summary>A short-lived read-only link for one viewer.</summary>
    Result<Uri> CreateReadLink(string blobKey, Guid societyId);
}

public sealed record PhotoUploadTicket(string BlobKey, Uri UploadUrl, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Issues time-limited links for gate photos.
///
/// Two things matter here and both are about not leaking faces.
///
/// First, the container is private and nothing ever returns a permanent URL. A visitor photo
/// reachable by anyone holding a link is a photo that will end up in a WhatsApp group — these
/// are pictures of couriers and guests who never agreed to be published.
///
/// Second, the blob key embeds the society, and every read link is checked against the
/// caller's society before it is signed. Without that check a resident of one society who
/// guessed a key could fetch another society's gate photos, and no query filter or row-level
/// security policy sits anywhere near blob storage.
/// </summary>
public sealed class VisitorPhotoService : IVisitorPhotoService
{
    private readonly BlobServiceClient _blobService;
    private readonly VisitorPhotoOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<VisitorPhotoService> _logger;

    public VisitorPhotoService(
        BlobServiceClient blobService,
        IOptions<VisitorPhotoOptions> options,
        TimeProvider timeProvider,
        ILogger<VisitorPhotoService> logger)
    {
        _blobService = blobService;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Result<PhotoUploadTicket> CreateUploadTicket(Guid societyId, Guid passId)
    {
        if (societyId == Guid.Empty)
        {
            return Error.Forbidden("Photo.NoSociety", "No society in scope.");
        }

        var now = _timeProvider.GetUtcNow();

        // Society first, then year and month — so a retention sweep or a society offboarding
        // is a prefix delete rather than a scan of every blob in the container.
        var blobKey = $"{societyId:N}/{now:yyyy/MM}/{passId:N}.jpg";

        var expiresAt = now.Add(_options.UploadLinkLifetime);

        // Write-only, and no Read. A device that could read back would be able to enumerate
        // other people's photos with a key it guessed.
        var sasUri = BuildSas(blobKey, BlobSasPermissions.Create | BlobSasPermissions.Write, expiresAt);

        return sasUri is null
            ? Error.Failure("Photo.StorageUnavailable", "Photo storage is not configured.")
            : new PhotoUploadTicket(blobKey, sasUri, expiresAt);
    }

    public Result<Uri> CreateReadLink(string blobKey, Guid societyId)
    {
        if (string.IsNullOrWhiteSpace(blobKey))
        {
            return Error.NotFound("Photo.NotFound", "No photo for that entry.");
        }

        // The isolation check. Blob storage has no tenant filter, so this is the only thing
        // standing between a guessed key and another society's gate photos.
        if (!blobKey.StartsWith($"{societyId:N}/", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Refused a cross-society photo request for {BlobKey} from society {SocietyId}.",
                blobKey,
                societyId);

            // 404 rather than 403: confirming the blob exists would itself leak that another
            // society holds a photo for that pass.
            return Error.NotFound("Photo.NotFound", "No photo for that entry.");
        }

        var expiresAt = _timeProvider.GetUtcNow().Add(_options.ReadLinkLifetime);
        var sasUri = BuildSas(blobKey, BlobSasPermissions.Read, expiresAt);

        return sasUri is null
            ? Error.Failure("Photo.StorageUnavailable", "Photo storage is not configured.")
            : sasUri;
    }

    private Uri? BuildSas(string blobKey, BlobSasPermissions permissions, DateTimeOffset expiresAt)
    {
        var blob = _blobService
            .GetBlobContainerClient(_options.ContainerName)
            .GetBlobClient(blobKey);

        // Requires a shared key credential. With managed identity in production this becomes a
        // user-delegation SAS, which is why the failure is reported rather than thrown.
        if (!blob.CanGenerateSasUri)
        {
            return null;
        }

        var builder = new BlobSasBuilder
        {
            BlobContainerName = _options.ContainerName,
            BlobName = blobKey,
            Resource = "b",
            ExpiresOn = expiresAt,

            // Slight backdating absorbs clock skew between us and the storage account, which
            // otherwise produces links that are briefly not yet valid.
            StartsOn = _timeProvider.GetUtcNow().AddMinutes(-2),
        };

        builder.SetPermissions(permissions);

        return blob.GenerateSasUri(builder);
    }
}
