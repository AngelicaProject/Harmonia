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

- **Packs** — import a pack file (`.hpk` or `.hpk.br`) and choose the pack to use. The choice applies at the next game start.
- **Updates** — add the feed URL a translation project publishes. Harmonia checks it periodically and offers new releases; automatic installation can be turned on.
- **Status** — the loaded pack, how many strings were translated or skipped because their source changed, and a per-sheet breakdown. *Copy diagnostics* puts a report on the clipboard for bug reports.
- **Settings** — the interface language and whether unreviewed translations are applied.

Packs are signed by their publisher. The first time you install a pack, Harmonia shows the publisher's key fingerprint and asks you to confirm it; after that, updates are accepted only from the same key or one it endorsed. Unsigned packs can be imported manually after an explicit confirmation.

The game's fonts must contain the characters of the target language. Harmonia replaces text only; fonts are not part of a pack.

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
