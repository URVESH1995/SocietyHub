using SocietyHub.Society.Api.Domain;
using SocietyAggregate = SocietyHub.Society.Api.Domain.Society;

namespace SocietyHub.Society.Tests;

/// <summary>
/// Occupancy and primary contact are both derived, and both are relied on elsewhere: the gate
/// calls the primary contact when a visitor arrives, and bulk-drive pricing counts occupied
/// flats. These pin the rules that keep them consistent.
/// </summary>
public sealed class FlatTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid SocietyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Tenant = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Spouse = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static Flat NewFlat()
    {
        var society = new SocietyAggregate(SocietyId, "Green Meadows", SocietySettings.ForIndia());
        var tower = society.AddTower("A");
        return tower.AddFlat("A-101", 1, "2BHK");
    }

    [Fact]
    public void A_new_flat_is_vacant()
    {
        Assert.Equal(Occupancy.Vacant, NewFlat().Occupancy);
    }

    [Fact]
    public void An_owner_moving_in_makes_it_owner_occupied()
    {
        var flat = NewFlat();

        flat.AddResident(Owner, Relationship.Owner, Now);

        Assert.Equal(Occupancy.OwnerOccupied, flat.Occupancy);
    }

    [Fact]
    public void A_tenant_makes_it_rented_even_when_the_owner_is_also_listed()
    {
        // Common in practice: the owner stays on the record while a tenant actually lives
        // there. What matters downstream is that somebody is renting it.
        var flat = NewFlat();

        flat.AddResident(Owner, Relationship.Owner, Now);
        flat.AddResident(Tenant, Relationship.Tenant, Now);

        Assert.Equal(Occupancy.Rented, flat.Occupancy);
    }

    [Fact]
    public void The_last_resident_moving_out_returns_it_to_vacant()
    {
        var flat = NewFlat();
        var resident = flat.AddResident(Owner, Relationship.Owner, Now);

        flat.RemoveResident(resident.Id, Now.AddYears(1));

        Assert.Equal(Occupancy.Vacant, flat.Occupancy);
    }

    [Fact]
    public void The_first_resident_becomes_the_primary_contact_automatically()
    {
        // A flat with residents and no contact means the gate has nobody to call.
        var flat = NewFlat();

        var resident = flat.AddResident(Owner, Relationship.Owner, Now);

        Assert.True(resident.IsPrimaryContact);
    }

    [Fact]
    public void Naming_a_new_primary_contact_demotes_the_previous_one()
    {
        var flat = NewFlat();
        var owner = flat.AddResident(Owner, Relationship.Owner, Now);

        var spouse = flat.AddResident(Spouse, Relationship.FamilyMember, Now, isPrimaryContact: true);

        Assert.True(spouse.IsPrimaryContact);
        Assert.False(owner.IsPrimaryContact);

        // Exactly one, always. "Several" and "none" are both broken answers at the gate.
        Assert.Single(flat.Residents.Where(r => r.IsActive && r.IsPrimaryContact));
    }

    [Fact]
    public void The_primary_contact_moving_out_promotes_a_successor()
    {
        var flat = NewFlat();
        var owner = flat.AddResident(Owner, Relationship.Owner, Now);
        flat.AddResident(Spouse, Relationship.FamilyMember, Now);

        flat.RemoveResident(owner.Id, Now.AddMonths(6));

        // The flat must not be left contactable-by-nobody while someone still lives there.
        Assert.Single(flat.Residents.Where(r => r.IsActive && r.IsPrimaryContact));
        Assert.Equal(Spouse, flat.Residents.Single(r => r.IsActive && r.IsPrimaryContact).UserId);
    }

    [Fact]
    public void An_owner_is_preferred_over_a_family_member_when_promoting()
    {
        var flat = NewFlat();
        flat.AddResident(Owner, Relationship.Owner, Now);
        var spouse = flat.AddResident(Spouse, Relationship.FamilyMember, Now, isPrimaryContact: true);

        flat.RemoveResident(spouse.Id, Now.AddMonths(3));

        Assert.Equal(Owner, flat.Residents.Single(r => r.IsActive && r.IsPrimaryContact).UserId);
    }

    [Fact]
    public void The_same_person_cannot_be_added_to_one_flat_twice()
    {
        var flat = NewFlat();
        flat.AddResident(Owner, Relationship.Owner, Now);

        Assert.Throws<InvalidOperationException>(
            () => flat.AddResident(Owner, Relationship.Owner, Now));
    }

    [Fact]
    public void A_move_out_is_recorded_rather_than_deleted()
    {
        // Gate logs and complaints reference residents as evidence. "Who admitted this visitor
        // last March" has to keep resolving after they leave.
        var flat = NewFlat();
        var resident = flat.AddResident(Owner, Relationship.Owner, Now);

        flat.RemoveResident(resident.Id, Now.AddYears(1));

        Assert.Single(flat.Residents);
        Assert.False(resident.IsActive);
        Assert.Equal(Now.AddYears(1), resident.MovedOutAtUtc);
    }

    [Fact]
    public void Someone_who_moved_out_can_move_back_in()
    {
        var flat = NewFlat();
        var first = flat.AddResident(Owner, Relationship.Owner, Now);
        flat.RemoveResident(first.Id, Now.AddYears(1));

        var second = flat.AddResident(Owner, Relationship.Owner, Now.AddYears(2));

        Assert.True(second.IsActive);
        Assert.Equal(Occupancy.OwnerOccupied, flat.Occupancy);
    }

    [Fact]
    public void Two_towers_may_each_have_a_flat_with_the_same_number()
    {
        // Flat numbers are unique within a tower, not across the society. Getting this wrong
        // makes onboarding fail for every society with more than one tower.
        var society = new SocietyAggregate(SocietyId, "Green Meadows", SocietySettings.ForIndia());

        var a = society.AddTower("A").AddFlat("101", 1, "2BHK");
        var b = society.AddTower("B").AddFlat("101", 1, "2BHK");

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal("101", a.FlatNumber);
        Assert.Equal("101", b.FlatNumber);
    }

    [Fact]
    public void A_duplicate_flat_within_one_tower_is_refused()
    {
        var society = new SocietyAggregate(SocietyId, "Green Meadows", SocietySettings.ForIndia());
        var tower = society.AddTower("A");
        tower.AddFlat("101", 1, "2BHK");

        Assert.Throws<InvalidOperationException>(() => tower.AddFlat("101", 1, "3BHK"));
    }

    [Fact]
    public void A_duplicate_tower_name_is_refused_case_insensitively()
    {
        var society = new SocietyAggregate(SocietyId, "Green Meadows", SocietySettings.ForIndia());
        society.AddTower("A");

        Assert.Throws<InvalidOperationException>(() => society.AddTower("a"));
    }
}
