namespace SocietyHub.SharedKernel.Features;

/// <summary>
/// Every capability a society can have switched on or off.
///
/// The platform ships new features every year to societies that are all on different
/// versions of their contract, on different plans, and with different appetites for change.
/// A release therefore cannot be all-or-nothing: a feature has to be enablable for five
/// societies, watched for a month, and only then opened to the rest.
///
/// Keys are strings rather than an enum on purpose — enabling a feature is a data change,
/// so a pilot does not require a deployment.
/// </summary>
public static class FeatureKey
{
    // ---- v1.0 — every society gets these, on every plan -------------------

    public const string VisitorManagement = "gate.visitors";
    public const string DeliveryEntry = "gate.delivery";
    public const string DailyHelpAttendance = "gate.daily-help";
    public const string Complaints = "helpdesk.complaints";
    public const string NoticeBoard = "notice.board";
    public const string SosAlert = "safety.sos";
    public const string ResidentDirectory = "society.directory";

    // ---- v1.5 — bulk service drives --------------------------------------

    public const string BulkServiceDrives = "drives.enabled";
    public const string OnlinePayments = "payments.online";
    public const string VendorMarketplace = "drives.marketplace";

    // ---- v2.0 — vision, wave 1 -------------------------------------------

    public const string CameraAnpr = "vision.anpr";
    public const string CameraTailgating = "vision.tailgating";
    public const string CameraParking = "vision.parking";
    public const string CameraIntrusion = "vision.intrusion";
    public const string CameraFleetHealth = "vision.fleet-health";

    // ---- v2.5 — vision, wave 2, life safety ------------------------------

    public const string CameraFireDetection = "vision.fire";
    public const string CameraFallDetection = "vision.fall";
    public const string CameraPoolSafety = "vision.pool";

    // ---- v2.5 — identity, opt-in only ------------------------------------

    /// <summary>
    /// Gates resident face entry. Enabling it is necessary but never sufficient: each
    /// resident must additionally hold a consent record, and a non-face entry path stays
    /// available regardless. A society-level switch cannot enrol anybody.
    /// </summary>
    public const string ResidentFaceEntry = "vision.face-entry";

    // ---- v3.0 — society operations ---------------------------------------

    public const string MaintenanceBilling = "billing.maintenance";
    public const string AmenityBooking = "amenity.booking";
    public const string ParkingManagement = "parking.management";
    public const string CommitteeVoting = "notice.voting";
    public const string DocumentVault = "society.documents";
}
