# Action names in commands

While row hooks are installed, `Game/ActionNameHook.cs` lets the shell find
actions by their original client-language names as well as the shown names.
This includes `/micon "Inner Release"` in saved macros: icon resolution reads
the macro separately from command execution, so changing chat input alone
would not fix hotbar icons.

The hook retries a failed native sheet-name match with the shown name. It
handles the name columns of `Action`, `CraftAction`, and `GeneralAction` only.
It builds the names from `OriginalSheets` and `TranslationRuntime.FindSheet`
and `TryGetShown`: excluded sheets/parts, kept cells, layout mismatches and
changed source guards never introduce aliases. Names containing SeString
macros are left to the game. Names and macro files are never rewritten.

The native match always runs first, so translated names retain the game's
usual behavior. Retries preserve the sheet descriptor and both row bounds;
the outer search still determines the action type. A retry succeeds only if
the native result is one of the rows that supplied that original name and
translation. Thus duplicate translations cannot select an unrelated action.
If an original name is also another action's shown name, the native shown
name takes precedence.

## Audited client path

In the installed executable inspected on 2026-11-03:

- `RaptureMacroModule::TryResolveMacroIcon` at `+0x7CB150` calls
  `RaptureShellModule::TryGetMacroIconCommand` at `+0x914130` to parse the
  first `/micon` line, then the name-search wrapper at `+0x9141E0`.
- The wrapper tail-calls the shell's sheet search at `+0xDB610` for
  nonnegative categories. That search iterates descriptors of size `0x88`
  and calls the hooked matcher at `+0xDA590` with six arguments:
  `(module, name, outRowId, descriptor, startRow, endRow)`.
- A descriptor starts with its sheet name as a `Utf8String`, followed by a
  sheet pointer at `+0x68` and a signed name-column index at `+0x70`.
  The native matcher also reads the descriptor's own row/column filters.
- The search combines the descriptor's index in the category with the
  matched row id. The hook leaves this encoding to the original function.

Addresses are audit references, not installation offsets. Installation uses
the matcher's checked, unique signature; failure is logged and included in
the Diagnostics report, without preventing translation.

`HARMONIA_GAME=<game>/game/sqpack dotnet test --filter ActionNameHook`
checks the installed matcher signature, descriptor accesses, and the calls
from macro icon resolution through both search layers. Unit tests cover
case-insensitive original names and duplicate names/translations.

The player confirmed the installed development build works in game on
2026-11-04. Further regression checks: put original and translated `/micon` lines in
separate saved macros, inspect their hotbar icons and cooldowns before
executing them and after login, and exercise `/ac` with both forms, including
crafting actions. Also check an unknown name, an explicit icon category,
an excluded names part, and operation with translated shared sheets enabled.
