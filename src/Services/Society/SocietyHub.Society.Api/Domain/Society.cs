using SocietyHub.SharedKernel.Primitives;

namespace SocietyHub.Society.Api.Domain;

/// <summary>
/// A housing society — the tenant itself.
///
/// Unusually, this is both the tenant root and a tenant-scoped row: its
/// <see cref="ITenantScoped.SocietyId"/> is its own <see cref="Entity.Id"/>. That is not a
/// trick for its own sake — it means the same query filter that stops one society reading
/// another's flats also stops it reading another's <em>profile</em>, with no special case
/// anywhere. A committee scoped to society A simply cannot see society B exists.
/// </summary>
public sealed class Society : AggregateRoot, ITenantScoped, IAuditable
{
    private readonly List<Tower> _towers = [];

    public Society(Guid id, string name, SocietySettings settings) : base(id)
    {
        Name = name;
        Settings = settings;
    }

    private Society()
    {
    }

    /// <summary>Its own id. See the type remarks — this is what makes the filter uniform.</summary>
    public Guid SocietyId => Id;

    public string Name { get; private set; } = string.Empty;

    /// <summary>Registration number with the state co-operative registrar, where it exists.</summary>
    public string? RegistrationNumber { get; set; }

    public string? AddressLine1 { get; set; }

    public string? AddressLine2 { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    /// <summary>Deliberately a free-text string, not an Indian PIN code.</summary>
    public string? PostalCode { get; set; }

    public SocietySettings Settings { get; private set; } = null!;

    public IReadOnlyCollection<Tower> Towers => _towers.AsReadOnly();

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    public Tower AddTower(string name)
    {
        if (_towers.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Tower '{name}' already exists in this society.");
        }

        var tower = new Tower(Guid.CreateVersion7(), Id, name);
        _towers.Add(tower);

        return tower;
    }

    public void Rename(string name) => Name = name;

    public void UpdateSettings(SocietySettings settings) => Settings = settings;
}

/// <summary>
/// The per-society configuration everything downstream reads.
///
/// Time zone is the one with teeth. All timestamps are stored UTC, but the 24-hour complaint
/// SLA, escalation windows and notification quiet hours are judged in <em>society-local</em>
/// time — a complaint raised at 11pm is not the same promise as one raised at 9am. Currency
/// and country are here rather than hard-coded so leaving India is configuration, not a
/// migration.
/// </summary>
public sealed class SocietySettings
{
    public SocietySettings(
        string defaultLanguage,
        string timeZoneId,
        string currency,
        string countryCode)
    {
        DefaultLanguage = defaultLanguage;
        TimeZoneId = timeZoneId;
        Currency = currency;
        CountryCode = countryCode;
    }

    private SocietySettings()
    {
    }

    /// <summary>
    /// BCP-47 tag. A fallback only — a resident's own choice and their device's
    /// Accept-Language both outrank it, because this is a committee's guess about everyone.
    /// </summary>
    public string DefaultLanguage { get; private set; } = "en-IN";

    /// <summary>IANA identifier, e.g. <c>Asia/Kolkata</c>.</summary>
    public string TimeZoneId { get; private set; } = "Asia/Kolkata";

    /// <summary>ISO 4217.</summary>
    public string Currency { get; private set; } = "INR";

    /// <summary>ISO 3166-1 alpha-2. Drives SMS routing and data-residency rules.</summary>
    public string CountryCode { get; private set; } = "IN";

    /// <summary>Complaints raised outside these hours have their SLA clock start next morning.</summary>
    public TimeOnly ActiveHoursStart { get; set; } = new(6, 0);

    public TimeOnly ActiveHoursEnd { get; set; } = new(22, 0);

    /// <summary>Non-urgent notifications are held until morning outside these hours.</summary>
    public TimeOnly QuietHoursStart { get; set; } = new(22, 0);

    public TimeOnly QuietHoursEnd { get; set; } = new(7, 0);

    /// <summary>
    /// Resolves the configured zone, falling back rather than throwing. A society row holding
    /// a stale or misspelled zone must not take the service down for that tenant.
    /// </summary>
    public TimeZoneInfo ResolveTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    public static SocietySettings ForIndia() => new("en-IN", "Asia/Kolkata", "INR", "IN");
}
