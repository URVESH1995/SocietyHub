using SocietyHub.Identity.Api.Domain;

namespace SocietyHub.Identity.Tests;

/// <summary>
/// A gate tablet is shared, physically reachable and periodically stolen. These cover the
/// properties that make a six-digit PIN acceptable on such a device.
/// </summary>
public sealed class GuardDeviceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 6, 0, 0, TimeSpan.Zero);
    private static readonly Guid SocietyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid GuardId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static GuardDevice Device(string pin = "482913")
    {
        var device = new GuardDevice(Guid.CreateVersion7(), SocietyId, "TAB-GATE-01", "Main Gate");
        device.SetShiftPin(pin, Now);
        return device;
    }

    [Fact]
    public void The_correct_pin_starts_a_shift_attributed_to_a_guard()
    {
        var device = Device();

        Assert.True(device.TryStartShift("482913", GuardId, Now));

        // Attribution is the point. Without it a gate entry is recorded against "the gate"
        // rather than the person who admitted the visitor.
        Assert.Equal(GuardId, device.ActiveGuardUserId);
        Assert.Equal(Now, device.ShiftStartedAtUtc);
    }

    [Fact]
    public void The_pin_is_never_stored_in_readable_form()
    {
        var device = Device();

        Assert.NotNull(device.PinHash);
        Assert.DoesNotContain("482913", device.PinHash);
        Assert.NotNull(device.PinSalt);
    }

    [Fact]
    public void A_wrong_pin_is_refused_and_counted()
    {
        var device = Device();

        Assert.False(device.TryStartShift("000000", GuardId, Now));
        Assert.Equal(1, device.FailedPinAttempts);
        Assert.Null(device.ActiveGuardUserId);
    }

    [Fact]
    public void Five_wrong_pins_lock_the_device_out()
    {
        // The cap is what makes a six-digit PIN a factor at all. An attacker holding the
        // tablet would otherwise walk the million combinations in minutes.
        var device = Device();

        for (var i = 0; i < GuardDevice.MaxPinAttempts; i++)
        {
            device.TryStartShift("000000", GuardId, Now);
        }

        Assert.True(device.IsLockedOut(Now));

        // Even the right PIN is refused while locked out.
        Assert.False(device.TryStartShift("482913", GuardId, Now));
    }

    [Fact]
    public void The_lockout_expires_and_the_correct_pin_works_again()
    {
        var device = Device();

        for (var i = 0; i < GuardDevice.MaxPinAttempts; i++)
        {
            device.TryStartShift("000000", GuardId, Now);
        }

        // A permanent lockout would leave a gate unstaffed, which is its own security problem.
        var later = Now.AddMinutes(16);

        Assert.False(device.IsLockedOut(later));
        Assert.True(device.TryStartShift("482913", GuardId, later));
    }

    [Fact]
    public void A_successful_sign_in_clears_the_failure_count()
    {
        var device = Device();

        device.TryStartShift("000000", GuardId, Now);
        device.TryStartShift("000000", GuardId, Now);
        Assert.Equal(2, device.FailedPinAttempts);

        Assert.True(device.TryStartShift("482913", GuardId, Now));
        Assert.Equal(0, device.FailedPinAttempts);
    }

    [Fact]
    public void A_revoked_device_cannot_start_a_shift_even_with_the_right_pin()
    {
        // The single action taken when a tablet is reported stolen.
        var device = Device();
        device.Revoke();

        Assert.False(device.TryStartShift("482913", GuardId, Now));
    }

    [Fact]
    public void Resetting_the_pin_clears_an_existing_lockout()
    {
        var device = Device();

        for (var i = 0; i < GuardDevice.MaxPinAttempts; i++)
        {
            device.TryStartShift("000000", GuardId, Now);
        }

        // How an administrator recovers a locked gate without waiting out the timer.
        device.SetShiftPin("135790", Now);

        Assert.False(device.IsLockedOut(Now));
        Assert.True(device.TryStartShift("135790", GuardId, Now));
    }

    [Fact]
    public void Ending_a_shift_detaches_the_guard()
    {
        var device = Device();
        device.TryStartShift("482913", GuardId, Now);

        device.EndShift();

        Assert.Null(device.ActiveGuardUserId);
        Assert.Null(device.ShiftStartedAtUtc);
    }
}
