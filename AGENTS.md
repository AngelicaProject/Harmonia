# AGENTS.md — Harmonia

Harmonia is a Dalamud plugin for Final Fantasy XIV that applies Harmonia translation packs (`.hpk`) to the game's text. `AssemblyName` and `InternalName` are `HarmoniaEngine`; author `pokeda`; license `AGPL-3.0-or-later`. The solution `HarmoniaEngine.slnx` has two projects.

Packs are built by Aeria (https://github.com/AngelicaProject/Aeria), which owns the pack, feed, and pack settings contracts: `docs/formats/pack-v1.md`, `docs/formats/feed-v1.md`, and `docs/formats/pack-settings-v1.md` in that repository. Harmonia implements the reader side and must follow them exactly.

## Layout

- `Harmonia/HarmoniaPlugin.cs` — entry point (`IDalamudPlugin`, services injected through the constructor). Loads the configuration and UI language, checks `SessionPid`, opens the selected pack, installs the row hooks, starts feeds, and registers windows and `/harmonia`. Owns the `Dispose` order.
- `Harmonia/Configuration.cs` — persisted settings (`Version = 1`). Property names are the saved JSON keys.
- `Harmonia/Packs/` — installed packs. `Hpk/` reads the format (`HpkFile` maps the file and verifies it in `Metadata` or `Full` mode; `HpkManifest`, `HpkSignature`, `SourceGuard`, `SeStringCheck`, `HpkFonts` for the optional `FONTS` section). `TranslationPackStore` manages `packs/<id>/<hash>.hpk` in the plugin's configuration directory (never next to the assembly: Dalamud deletes old version folders on update; `PackStorageMigration` copies packs of earlier builds) with `installed.json` (the trusted key and the feed of each installed translation; `<id>` is Harmonia's own name, packs carry none); `PackInstaller` stages, verifies, and commits imports and downloads; `PublisherTrust` decides trust against the translation's pinned key.
- `Harmonia/Feeds/` — feed v1 parsing and release selection (`FeedDocument`), polling and installation (`FeedUpdateService`), runtime status for the UI (`FeedUpdateState`).
- `Harmonia/Runtime/` — `TranslationRuntime` (the session's pack: sheet binding by layout, per-cell decisions, counters) and `RowLayout` (parsing and rebuilding a version 3 row buffer; no game dependency).
- `Harmonia/Game/ExcelRowHooks.cs` — writes translations into Excel rows; with `TextCaseHooks.cs`, the only code that hooks the game.
- `Harmonia/Game/TextCaseHooks.cs` — while a Cyrillic pack is loaded, extends `Utf8String::ToUpper` and `ToLower` (which the `<head>`, `<headall>`, `<caps>`, and `<lower>` macros use) to Cyrillic letters; the rule itself is `Runtime/CyrillicCase.cs`, without a game dependency.
- `Harmonia/Fonts/` — the pack `FONTS` glyphs: `FdtFile` (game font tables), `FontTexture` (`0x1440` atlases), `AtlasPacker`, `FontPatcher` (adds glyphs to the main and title-screen font sets without touching existing data), `FontCache`. No game dependency.
- `Harmonia/Game/GameFonts.cs` — builds or reuses the patched font files at startup, serves them through a Penumbra temporary mod (`Penumbra.Api`), and asks the game to reload its fonts once Penumbra has applied the mod.
- `Harmonia/UI/` — `MainWindow`, a partial class with a sidebar: Translations (status, installed packs merged with their feeds, the add dialog and publisher confirmation in `MainWindow.Actions`), Coverage (`MainWindow.Content`: sheet groups, or every sheet of the selected pack, kept in the game's language), Settings, Diagnostics; `RestartWindow`; `Ui` (cards, badges, notices, buttons).
- `Harmonia/Localization/Lang.cs` with `Harmonia/Assets/Localization/{en,ru}.json`.
- `Harmonia/Assets/Icon/harmonia_icon.png` — the plugin icon referenced by `IconUrl`.
- `Harmonia.Tests/` — xUnit tests: localization dictionaries, the pack reader (`HpkBuilder` is an independent `.hpk` writer for fixtures), installer, feeds, `SeStringCheck` (against `TestData/well_formed.vectors.txt`, a copy of Aeria's vectors for the well-formed string rule of the pack format), `RowLayout`, `TranslationRuntime`, the font patching (`Fonts/`, with independent `.fdt`, `.tex`, and `FONTS` writers in `FontTestData`), and `AeriaInteropTests`, which reads `TestData/harmonia-interop.hpk` and `harmonia-interop-fonts.hpk` produced by Aeria's tests.
- `repo.json` — the custom repository manifest for Dalamud. `docs/packs.md` — what Harmonia adds on top of the Aeria contracts.

## How translation is applied

The game builds every Excel row in `ExcelRow_Parse_v3` and immediately hands it to the sheet's row resolver through `IExcelPageRowResolver::StoreRow` (vtable slot 3; implemented by `HashTableExcelPageRowResolver` and `RingBufferExcelPageRowResolver`). No other code can see the row before that call. The `StoreRow` detour:

1. collects the sheet's String columns from `ColumnDefinitions` and binds the sheet to the pack by name, variant, and layout, unless the player keeps the sheet in the game's language (`UntranslatedSheets`);
2. takes the row id from the descriptor; for `MultiRow` sheets the subrow id is `SubRowIds[0]` (`LowerRowIdPart` is a hash of the keys, not an id);
3. parses the row buffer with `RowLayout.TryRead`: a fixed part of `sheet->DataOffset` bytes followed by one NUL-terminated string per String column, in column order; any other layout leaves the row untouched;
4. decides every pack cell of the row: the source guard must match;
5. if anything applies, allocates a buffer of the exact new size through the game's `ExdEnvironment` (vtable +8, `(env, size, 0)`), writes the row in the game's own layout, frees the old buffer (vtable +16, `(env, ptr)`), and replaces `row->Data`.

`ExcelRowHooks` holds signatures for both `StoreRow` functions and for the global `ExdEnvironment` (taken from the allocation site in `ExcelRow_Parse_v3`). A resolved `StoreRow` must be slot 3 of a vtable in `.rdata` whose slots point into the loaded module's code; otherwise no hook is installed and the Diagnostics page shows why. Dalamud scans a copy of the module, so compare against `Module.BaseAddress + TextSectionOffset`, not `TextSectionBase`.

## Build and run

Requires the .NET 10 SDK and a Dalamud installation (the test project references `Dalamud.dll` through `DalamudLibPath`, by default `%APPDATA%\XIVLauncher\addon\Hooks\dev\`).

```powershell
dotnet build --configuration Release
dotnet test
```

For a development run, add the built `HarmoniaEngine.dll` to Dev Plugin Locations in `/xlsettings`, enable it in `/xlplugins`, and open `/harmonia`.

## Before committing

1. `dotnet build --configuration Release` with no errors or warnings.
2. `dotnet test` passes.
3. Never commit `bin/`, `obj/`, `.vs/`, `.idea/`, or `*.user`.

## Code style

- `.editorconfig`: UTF-8, LF, 4 spaces, braces on new lines; private fields `lowerCamelCase`, private static and const members `UpperCamelCase`.
- File-scoped namespaces; `ImplicitUsings` and `Nullable` are enabled.
- Async code uses `ConfigureAwait(false)` and passes `CancellationToken`; logging goes through `IHarmoniaLog`.
- Every user-visible string goes through `Lang.T("...")`.
- Comments explain invariants and reasons only.

## Invariants

- **Plugin metadata.** Do not rename `AssemblyName`, `RootNamespace`, or `InternalName`, and do not change `LoadSync`, `LoadPriority`, or `ApplicableVersion` without a reason. Keep the manifest fields in `HarmoniaEngine.csproj` and `repo.json` in sync, including the version.
- **Restart only.** Selecting, updating, or turning off a pack takes effect at the next game start. Live switching is deliberately not offered: the game copies strings into its own caches, so a partial switch would mislead players. Loading the plugin again in the same game session (`SessionPid`) installs no hooks and shows `RestartWindow`.
- **Hooks exist only while a pack is loaded.** Having no pack is a normal state.
- **Translation checks.** A string is written only when (1) the pack's source language equals the client language (enforced when the pack is selected), (2) the sheet's String columns (index, offset, order) and variant equal the pack `LAYOUT`, (3) the source string's guard equals the cell's `sourceGuard`, and (4) the row buffer has the expected version 3 layout. Never weaken these. The active pack is verified completely (`HpkOpenMode.Full`) when it is loaded.
- **Row memory.** Row buffers are allocated and freed only by the game's `ExdEnvironment`, with exactly the calls `ExcelRow_Parse_v3` and `ExcelRow_Clear` make. Untouched strings keep their original hash byte, because the game hashes the resolved text of `_rsv_` strings.
- **Dispose order.** Windows and command, then feeds, then `GameFonts.Dispose()` (removes the Penumbra temporary mod), then `ExcelRowHooks.Dispose()` (disables the hooks and waits for running detours), then `TranslationRuntime.Dispose()` (unmaps the pack). Detours read translations directly from the mapping.
- **Updates.** Everything a feed claims is checked against the downloaded pack. Each installed translation pins one signing key in its `installed.json`: a pack from its feed installs only when signed by that key or a key it endorsed; a file signed by the pinned key updates it, any other key makes a new translation after the player confirms it; unsigned packs install from a file only, with explicit confirmation; installing a lower version is manual only; automatic installation is off by default.
- **Game fonts.** Pack glyphs are only added: existing `.fdt` records, kerning pairs, and used atlas pages are never changed, glyphs the font already has are skipped, and new glyphs go only into candidate pages that are empty in the running game. A target whose native line height or ascent changed is skipped. Penumbra is optional; without it translations still apply and fonts are reported as not applied.
- **Localization.** `en` has every key; other languages have a subset without technical keys (`Lang.TechnicalPrefixes`, currently `diagnostics.*` and `command.*`, always shown in English). Tests check that every key used in code exists in `en`.

## Common tasks

- New UI string: add the key to `en.json` and `ru.json`, use `Lang.T(...)`, run `dotnet test`.
- Pack or feed format change: change the specification in Aeria first, then `Packs/Hpk/` and `HpkBuilder`, then `docs/packs.md`.
- A game patch broke the hooks: compare `ExcelRow_Parse_v3`, `ExcelRow_Clear`, and both `StoreRow` implementations with a decompilation of the new build, update the signatures in `ExcelRowHooks`, and run the `RowLayout` tests. Also check `GameFonts.ReloadFontsSignature`: it must find the function in `RaptureAtkModule` vtable slot `ReloadFontsVFunc` (Penumbra keeps the slot in `VolatileOffsets.FontReloader`); fonts report `NeedsReload` when it does not.
- Release: bump `Version` in `HarmoniaEngine.csproj` and `AssemblyVersion`, `TestingAssemblyVersion`, and the download links in `repo.json` together, commit, and push the tag `vX.Y.Z.W`. `.github/workflows/release.yml` builds, tests, refuses to publish when the tag, the built manifest, and `repo.json` disagree, and attaches `HarmoniaEngine.zip` to the GitHub release. `build.yml` builds and tests every push against the stable Dalamud distribution.
