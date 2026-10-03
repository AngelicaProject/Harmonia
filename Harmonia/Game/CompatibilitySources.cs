using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Harmonia.Game;

// Rows a compatibility profile names by meaning rather than by id, listed from
// the game data at startup (CompatibilityProfile.KnownSources).
internal sealed class CompatibilitySources(IDataManager data, IHarmoniaLog log)
{
    public IEnumerable<(string Sheet, uint Row)> Rows(string source)
    {
        try
        {
            return source switch
            {
                "aethernet-place-names" => AethernetPlaceNames(),
                _ => [],
            };
        }
        catch (Exception ex)
        {
            log.Error($"Compatibility source {source} could not be read; its rows are translated.", ex);
            return [];
        }
    }

    // Destination names of the aethernet menus (TelepotTown): city aethernets,
    // residential district aethernets, and Cosmic Exploration aetherytes.
    private List<(string, uint)> AethernetPlaceNames()
    {
        var rows = new List<(string, uint)>();
        foreach (var aetheryte in data.GetExcelSheet<Aetheryte>())
        {
            if (aetheryte.AethernetGroup != 0 && aetheryte.AethernetName.RowId != 0)
                rows.Add((nameof(PlaceName), aetheryte.AethernetName.RowId));
        }

        foreach (var aethernet in data.GetExcelSheet<HousingAethernet>())
        {
            if (aethernet.PlaceName.RowId != 0)
                rows.Add((nameof(PlaceName), aethernet.PlaceName.RowId));
        }

        foreach (var aetheryte in data.GetExcelSheet<WKSAetheryte>())
        {
            if (aetheryte.Name.RowId != 0)
                rows.Add((nameof(PlaceName), aetheryte.Name.RowId));
        }

        return rows;
    }
}
