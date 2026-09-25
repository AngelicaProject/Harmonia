# Harmonia

Harmonia is a [Dalamud](https://github.com/goatcorp/Dalamud) plugin that shows Final Fantasy XIV in another language. It applies translation packs made with [Aeria](https://github.com/AngelicaProject/Aeria) to the game's text as the game loads it.

A translation replaces a game string only when the running game still has exactly the source text it was made for. After a game patch, strings that changed stay in the original language until the pack is updated; nothing is ever applied to the wrong text.

## Installing

Add the custom repository to Dalamud (`/xlsettings` → Experimental → Custom Plugin Repositories):

```text
https://raw.githubusercontent.com/AngelicaProject/Harmonia/main/repo.json
```

Then install **Harmonia** from `/xlplugins` and restart the game. Harmonia loads before the game reads its text, so text is translated from the start; text the game loaded before the plugin started stays untranslated until the next start.

## Using it

Open the window with `/harmonia`.

- **Translations** — whether the translation is on, and your installed translations. *Add translation* takes either the link a translation project publishes (Harmonia installs the translation and keeps it up to date) or a pack file (`.hpk` or `.hpk.br`). Turning a translation on or off applies at the next game start.
- **Settings** — the window language, whether draft (unreviewed) lines are applied, and how updates are installed.
- **Diagnostics** — the engine and font state, how many strings were translated or skipped because their source changed, and a per-sheet breakdown. *Copy report* puts it on the clipboard for bug reports.

Packs are signed by their publisher. The first time you install a pack, Harmonia shows the publisher's key fingerprint and asks you to confirm it; after that, updates are accepted only from the same key or one it endorsed. Unsigned packs can be imported manually after an explicit confirmation.

Some game fonts lack the target language's letters: the display fonts of window titles, tabs, and the title screen (Jupiter, TrumpGothic, MiedingerMid) have no Cyrillic. A pack may carry the missing glyphs. Harmonia then adds them to the game's own fonts at startup, leaving every existing glyph as it is, and serves the result through **[Penumbra](https://github.com/xivdev/Penumbra)**, which must be installed and enabled for this. Without Penumbra the translation still applies and those labels keep showing dashes; the Translations page says so. The font sources and their licenses are listed in the active translation's details.

## Building

Requirements: the .NET 10 SDK and an XIVLauncher installation with Dalamud (the build and the tests reference its assemblies in `%APPDATA%\XIVLauncher\addon\Hooks\dev\`).

```powershell
dotnet build --configuration Release
dotnet test
```

The plugin is written to `Harmonia/bin/x64/Release/`. To run a development build, add `HarmoniaEngine.dll` to Dev Plugin Locations in `/xlsettings`.

How the plugin works internally and the rules for changing it are in [AGENTS.md](AGENTS.md). The pack and feed formats are specified in the Aeria repository (`docs/formats/`); [docs/packs.md](docs/packs.md) describes what Harmonia adds on top of them.

## License

[AGPL-3.0-or-later](LICENSE.md).
