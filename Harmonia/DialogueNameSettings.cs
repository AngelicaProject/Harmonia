using Harmonia.Packs.Hpk;

namespace Harmonia;

public sealed class DialogueNameSettings
{
    public string OriginalName { get; set; } = string.Empty;
    public Dictionary<PlayerNameCase, DialogueNameForms> Forms { get; set; } = [];
}

public sealed class DialogueNameForms
{
    public string Full { get; set; } = string.Empty;
    public string First { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
}
