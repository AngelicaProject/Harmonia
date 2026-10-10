using Dalamud.Game;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Lumina;
using Lumina.Excel;

namespace Harmonia.Game;

// The game's sheets as its files hold them, for Harmonia's own readers (the
// name dictionary, the Coverage page samples). The Lumina instance Dalamud
// shares has them unless SharedSheets gives it the translation; then they
// are read through an instance of Harmonia's own, opened on first use.
internal sealed class OriginalSheets(IDataManager data, bool sharedIsTranslated) : IDisposable
{
    private readonly Lazy<GameData> own = new(() => new GameData(data.GameData.DataPath.FullName, data.GameData.Options));

    public ClientLanguage Language => data.Language;

    private ExcelModule Excel => sharedIsTranslated ? own.Value.Excel : data.Excel;

    public ExcelSheet<T> GetExcelSheet<T>(ClientLanguage? language = null, string? name = null)
        where T : struct, IExcelRow<T> =>
        Excel.GetSheet<T>(language?.ToLumina(), name);

    public SubrowExcelSheet<T> GetSubrowExcelSheet<T>(ClientLanguage? language = null, string? name = null)
        where T : struct, IExcelSubrow<T> =>
        Excel.GetSubrowSheet<T>(language?.ToLumina(), name);

    public void Dispose()
    {
        if (own.IsValueCreated)
            own.Value.Dispose();
    }
}
