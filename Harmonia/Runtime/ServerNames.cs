namespace Harmonia.Runtime;

// Text the game sends to the server: a purchase registers a default name that
// the event script reads with GetAddonText and returns with its result, and
// the server refuses a name it does not accept ("Failed to complete housing
// action."). These rows stay in the game's language in every session; the
// player renames the apartment, airship, or submersible afterwards. The event
// scripts (CustomTalk and Quest) have no other such name.
public static class ServerNames
{
    public const string Sheet = "Addon";

    // The script, the constant its CustomTalk row gives it, and the Addon row
    // that constant names (tools/CompatibilityCheck compares them with the game).
    public static readonly IReadOnlyList<ServerName> All =
    [
        new("HouFixMansionOfficer_00363", "MANSION_DEFAULT_NAME", 6710),
        new("CmnDefHousingPersonalRoomEntrance_00178", "DEFAULT_NAME_PRIVATE_ROOM", 6487),
        new("CmnDefCompanyCommanderBoard_00258", "DEFAULT_NAME_AIRSHIP", 6489),
        new("HouFixCompanySubmarine_00447", "DEFAULT_NAME_SUBMARINE", 6888),
    ];

    public static void AddTo(KeptRows keep) => keep.AddRows(Sheet, All.Select(static n => n.AddonRow));
}

public sealed record ServerName(string Script, string Constant, uint AddonRow);
