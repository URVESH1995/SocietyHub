using SocietyHub.Society.Api.Domain;
using SocietyAggregate = SocietyHub.Society.Api.Domain.Society;

namespace SocietyHub.Society.Tests;

/// <summary>
/// Registration normalisation exists for one downstream consumer: the ANPR camera in Phase 3,
/// which reads a plate and must match it against what a resident typed months earlier.
/// </summary>
public sealed class VehicleTests
{
    private static readonly Guid SocietyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid FlatId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Theory]
    [InlineData("MH 12 AB 1234")]
    [InlineData("MH-12-AB-1234")]
    [InlineData("mh12ab1234")]
    [InlineData("  MH12AB1234  ")]
    public void Every_way_of_writing_one_plate_normalises_to_the_same_value(string written)
    {
        // No two people write a plate the same way, and a camera writes it a third way. Making
        // that decision once at write time is far cheaper than at every read.
        var vehicle = new Vehicle(Guid.CreateVersion7(), SocietyId, FlatId, written, VehicleType.Car);

        Assert.Equal("MH12AB1234", vehicle.RegistrationNumber);
    }

    [Fact]
    public void Normalisation_is_exposed_so_lookups_can_match_what_was_stored()
    {
        Assert.Equal("MH12AB1234", Vehicle.Normalise("MH 12-ab 1234"));
    }

    [Fact]
    public void A_slot_can_be_allotted_to_a_flat_and_released()
    {
        var slot = new ParkingSlot(Guid.CreateVersion7(), SocietyId, "B1-045", ParkingSlotType.Covered);

        slot.AllotTo(FlatId);
        Assert.Equal(FlatId, slot.AllottedToFlatId);

        slot.Release();
        Assert.Null(slot.AllottedToFlatId);
    }

    [Fact]
    public void A_visitor_slot_cannot_be_allotted_to_a_flat()
    {
        // Allotting the visitor bay is how a society ends up with nowhere for visitors to park
        // and a standing argument at the gate.
        var slot = new ParkingSlot(Guid.CreateVersion7(), SocietyId, "V-01", ParkingSlotType.Visitor);

        Assert.Throws<InvalidOperationException>(() => slot.AllotTo(FlatId));
    }
}

/// <summary>
/// Society settings are what make the platform region-configurable rather than India-only,
/// and the time zone in particular drives every SLA deadline in the society.
/// </summary>
public sealed class SocietySettingsTests
{
    [Fact]
    public void The_india_default_is_complete()
    {
        var settings = SocietySettings.ForIndia();

        Assert.Equal("en-IN", settings.DefaultLanguage);
        Assert.Equal("Asia/Kolkata", settings.TimeZoneId);
        Assert.Equal("INR", settings.Currency);
        Assert.Equal("IN", settings.CountryCode);
    }

    [Fact]
    public void A_valid_time_zone_resolves()
    {
        var resolved = SocietySettings.ForIndia().ResolveTimeZone();

        // +05:30 regardless of the season — India has no daylight saving.
        Assert.Equal(TimeSpan.FromMinutes(330), resolved.GetUtcOffset(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_broken_time_zone_falls_back_instead_of_throwing()
    {
        // A society row holding a stale or misspelled zone must not take the service down for
        // that tenant. Falling back to UTC is wrong but survivable; a 500 on every request
        // is neither.
        var settings = new SocietySettings("en-IN", "Mars/Olympus_Mons", "INR", "IN");

        Assert.Equal(TimeZoneInfo.Utc, settings.ResolveTimeZone());
    }

    [Fact]
    public void Settings_can_describe_a_society_outside_india()
    {
        // The point of carrying currency and country rather than assuming them.
        var settings = new SocietySettings("en-IN", "Asia/Dubai", "AED", "AE");

        Assert.Equal("AED", settings.Currency);
        Assert.Equal("AE", settings.CountryCode);
        Assert.Equal(TimeSpan.FromHours(4), settings.ResolveTimeZone().GetUtcOffset(DateTimeOffset.UtcNow));
    }
}
