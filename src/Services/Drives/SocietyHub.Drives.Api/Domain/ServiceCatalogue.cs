using SocietyHub.SharedKernel.Primitives;

namespace SocietyHub.Drives.Api.Domain;

/// <summary>
/// A service the platform knows how to run a drive for.
///
/// <para>
/// Platform data, not society data. The catalogue is the same everywhere — an AC service is an
/// AC service in Pune and in Mumbai — and letting each society define its own would mean no two
/// societies could ever be offered the same vendor's rate card.
/// </para>
///
/// <para>
/// The code is the join to a vendor's rate card, so it is an identifier and not a display
/// string. Renaming "Split AC servicing" to "AC servicing (split)" must never change
/// <c>ac.service.split</c>, or every rate card in the system silently stops matching.
/// </para>
/// </summary>
public sealed class ServiceCatalogueItem : Entity
{
    private ServiceCatalogueItem() { }

    public ServiceCatalogueItem(
        Guid id,
        string code,
        string nameEn,
        string nameHi,
        string unitLabelEn,
        string unitLabelHi,
        ServiceCategory category)
        : base(id)
    {
        Code = code;
        NameEn = nameEn;
        NameHi = nameHi;
        UnitLabelEn = unitLabelEn;
        UnitLabelHi = unitLabelHi;
        Category = category;
    }

    /// <summary>Stable identifier, e.g. <c>ac.service.split</c>. Never shown to anyone.</summary>
    public string Code { get; private set; } = string.Empty;

    public string NameEn { get; private set; } = string.Empty;

    public string NameHi { get; private set; } = string.Empty;

    /// <summary>
    /// What one unit is. Held per language because "per AC unit" and "प्रति एसी यूनिट" are
    /// not interchangeable, and a price with the wrong unit beside it is the single most
    /// common source of a billing complaint.
    /// </summary>
    public string UnitLabelEn { get; private set; } = string.Empty;

    public string UnitLabelHi { get; private set; } = string.Empty;

    public ServiceCategory Category { get; private set; }

    public bool IsActive { get; private set; } = true;

    /// <summary>
    /// Typical minimum for this service to be worth a vendor's trip.
    ///
    /// A suggestion the committee can override, not a rule. A vendor will travel for eight
    /// flats in a dense city and refuse forty in a distant suburb, and the platform is not in
    /// a position to know which — but a committee opening its first drive has no idea what
    /// number to type, and a blank field there produces drives that never had a chance.
    /// </summary>
    public int SuggestedQuorum { get; private set; } = 10;

    public string NameFor(string languageTag) =>
        languageTag.StartsWith("hi", StringComparison.OrdinalIgnoreCase) ? NameHi : NameEn;

    public string UnitLabelFor(string languageTag) =>
        languageTag.StartsWith("hi", StringComparison.OrdinalIgnoreCase)
            ? UnitLabelHi
            : UnitLabelEn;

    public void SetSuggestedQuorum(int quorum) => SuggestedQuorum = Math.Max(1, quorum);

    public void Deactivate() => IsActive = false;
}

public enum ServiceCategory
{
    Appliance = 0,
    Cleaning = 1,
    Vehicle = 2,
    PestControl = 3,
    Plumbing = 4,
    Electrical = 5,
}

/// <summary>
/// The services the platform ships with.
///
/// Seeded rather than left for an operator to type, because a drive cannot be opened against a
/// service that does not exist and an empty catalogue makes the whole feature invisible on
/// first run. Bilingual by construction — a catalogue entry with no Hindi is a screen a Hindi
/// resident cannot read.
/// </summary>
public static class CatalogueSeed
{
    public static IReadOnlyList<ServiceCatalogueItem> All { get; } =
    [
        Item("ac.service.split", "Split AC servicing", "स्प्लिट एसी सर्विसिंग",
             "per AC unit", "प्रति एसी यूनिट", ServiceCategory.Appliance, 10),

        Item("ac.service.window", "Window AC servicing", "विंडो एसी सर्विसिंग",
             "per AC unit", "प्रति एसी यूनिट", ServiceCategory.Appliance, 10),

        Item("appliance.washingmachine", "Washing machine servicing", "वॉशिंग मशीन सर्विसिंग",
             "per machine", "प्रति मशीन", ServiceCategory.Appliance, 8),

        Item("cleaning.deep.home", "Deep home cleaning", "गहरी घर की सफाई",
             "per flat", "प्रति फ्लैट", ServiceCategory.Cleaning, 6),

        Item("cleaning.sofa", "Sofa and upholstery cleaning", "सोफा और अपहोल्स्ट्री सफाई",
             "per seat", "प्रति सीट", ServiceCategory.Cleaning, 10),

        Item("cleaning.watertank", "Water tank cleaning", "पानी की टंकी की सफाई",
             "per tank", "प्रति टंकी", ServiceCategory.Cleaning, 4),

        Item("vehicle.carwash", "Car cleaning", "कार की सफाई",
             "per car", "प्रति कार", ServiceCategory.Vehicle, 15),

        Item("pest.general", "Pest control", "कीट नियंत्रण",
             "per flat", "प्रति फ्लैट", ServiceCategory.PestControl, 12),

        Item("plumbing.inspection", "Plumbing inspection", "प्लंबिंग निरीक्षण",
             "per flat", "प्रति फ्लैट", ServiceCategory.Plumbing, 8),

        Item("electrical.inspection", "Electrical safety check", "बिजली सुरक्षा जाँच",
             "per flat", "प्रति फ्लैट", ServiceCategory.Electrical, 8),
    ];

    private static ServiceCatalogueItem Item(
        string code,
        string nameEn,
        string nameHi,
        string unitEn,
        string unitHi,
        ServiceCategory category,
        int quorum)
    {
        var item = new ServiceCatalogueItem(
            Guid.CreateVersion7(), code, nameEn, nameHi, unitEn, unitHi, category);

        item.SetSuggestedQuorum(quorum);

        return item;
    }
}
