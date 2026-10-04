# Harmonia

Harmonia is a [Dalamud](https://github.com/goatcorp/Dalamud) plugin that shows Final Fantasy XIV in another language using community translations.

## Installing

Add the custom repository to Dalamud (`/xlsettings` → Experimental → Custom Plugin Repositories):

```text
https://raw.githubusercontent.com/AngelicaProject/Harmonia/main/repo.json
```

Then install **Harmonia** from `/xlplugins` and restart the game.

## Using it

1. Open the window with `/harmonia`.
2. Press **Add translation** and paste the link the translation's author gave you, or choose the translation file (`.hpk`) you downloaded.
3. Restart the game. The top of the window says whether the translation is on.

Translations added from a link update themselves. Turning a translation on or off, and new versions, take effect the next time you start the game.

After a game update, some text may stay in the original language until the translation's author updates it.

Know a name only in the original, say from a guide or a market site? Type `/hfind` and the name in chat: Harmonia lists what the game calls it now, with items as links. It also works the other way, and with names typed in the wrong keyboard layout. `/hfind` alone opens the **Dictionary** page, which searches items, actions, statuses, places, duties, and quests as you type; click a name to copy it, or open a row's menu to try an item on or open it on Universalis, Garland Tools, Teamcraft, or the Console Games Wiki. Right-click an item in your inventory or a chat link and choose **Copy the original name** to share it with players who play without the translation. Names in other game languages can be turned on in **Settings**.

Some translations add characters the game's own fonts don't have, such as Cyrillic in window titles and on the title screen. Those need **[Penumbra](https://github.com/xivdev/Penumbra)** installed and enabled; everything else is translated without it.

If something doesn't work, open **Diagnostics**, press **Copy report**, and send it to the translation's author.

## For developers

Requirements: the .NET 10 SDK and an XIVLauncher installation with Dalamud (the build and the tests reference its assemblies in `%APPDATA%\XIVLauncher\addon\Hooks\dev\`).

```powershell
dotnet build --configuration Release
dotnet test
```

The plugin is written to `Harmonia/bin/x64/Release/`. To run a development build, add `HarmoniaEngine.dll` to Dev Plugin Locations in `/xlsettings`.

Translations are packs made with [Aeria](https://github.com/AngelicaProject/Aeria), which specifies the pack and feed formats (`docs/formats/`). [docs/packs.md](docs/packs.md) describes what Harmonia adds on top of them: verification, publisher trust, and font glyphs. How the plugin works internally and the rules for changing it are in [AGENTS.md](AGENTS.md).

## License

[AGPL-3.0-or-later](LICENSE.md).
