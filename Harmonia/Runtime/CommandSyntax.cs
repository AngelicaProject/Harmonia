namespace Harmonia.Runtime;

// These cells are executable shell syntax. Descriptions may be translated,
// but changing the tokens breaks saved macros and native argument parsing.
public static class CommandSyntax
{
    public static void AddTo(KeptRows keep)
    {
        keep.AddSheet("TextCommandParam");
        keep.AddColumns("TextCommand", [5, 6, 8, 9], null);
    }
}
