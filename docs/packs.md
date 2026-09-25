# Translation packs

Harmonia reads one pack format: Harmonia Pack v1 (`.hpk`). Aeria produces it
and owns the contract:

- pack layout, source guard, signature block: Aeria `docs/formats/pack-v1.md`
- update feed, release selection, publisher trust: Aeria `docs/formats/feed-v1.md`
- the project's pack identity and publisher key fingerprint, which the feed
  workflow publishes: Aeria `docs/formats/pack-settings-v1.md`

This file documents only what Harmonia adds on top of those contracts.

## On-disk layout

```text
<plugin-dir>/resources/packs/
  <packId>/
    <packHash hex>.hpk      installed pack, named by its content hash
    installed.json          {"formatVersion": 1, "packHash": "sha256:<hex>"}
  .staging/                 downloads and imports waiting for a trust decision
```

`installed.json` is written atomically and names the current file. A new
release is installed next to the old file; the old file stays mapped until the
game restarts and is removed on the next start, together with `.staging/`.
A folder without a valid `installed.json` is listed as invalid.

## Verification

| When | What is checked |
| --- | --- |
| Import or download | everything in the pack reader requirements: header, sections, digest, signature, every record, every SeString |
| Listing installed packs | header, sections and manifest only |
| Loading the active pack at startup | everything again; a failure leaves the game untranslated and shows the reason |
| Every row the game creates | sheet layout, the v3 row buffer layout, and the source guard of each string (the client language is checked when the pack is selected) |

A translation replaces a game string only when the running game still has
exactly the source text it was made for. After a game patch, strings that
changed stay in the original language until the pack is updated; nothing is
ever applied to the wrong text.

## Publisher trust

Packs are signed by their publisher. The first install of a pack id shows the
key fingerprint and asks the player to confirm it; the key is then pinned
(`PinnedPublisherKeys`), and feeds install only packs signed by that key or one
it endorsed. Unsigned packs and packs signed by another key can be imported
from a file only, after an explicit confirmation. Installing a lower release
sequence is manual only.

## Font glyphs

A pack of format minor 1 may have a `FONTS` section (Aeria
`docs/formats/pack-v1.md`). When the active pack has one, `GameFonts` at
startup:

1. reads the game's own `common/font/<font>_<size>.fdt` and `_lobby.fdt`
   files and their atlas textures through `IDataManager`;
2. runs `FontPatcher` for the main set (`fontN.tex`, candidate pages 10, 11,
   25, 26, 27) and the title-screen set (`font_lobbyN.tex`, candidate page 23).
   A candidate page is used only when its channel is entirely empty in the
   running game's texture;
3. per target: skips it when the `.fdt` is missing or its `fthd` line height or
   ascent differ from the section; skips glyphs the font already has; packs
   the rest with `AtlasPacker` (shelves, 1 px gap, taller glyphs first) into
   the free pages, all or nothing per target, and reports `NoRoom` otherwise;
4. writes coverage as `round(value × 15 / 255)` into the page's channel
   (channels 0–3 are bits 8, 4, 0, 12 of each 16-bit `0x1440` pixel), and
   inserts `.fdt` records in UTF-8 order: `texIndex` = page, `nextOffsetX` =
   advance − width, `offsetY` from the section, and the Shift-JIS code the
   game's own `AXIS_12.fdt` has for the character (0 when it has none).
   Existing records, the kerning table, and every other channel stay as read;
5. stores the changed files in `<plugin>/resources/font-cache/<game version>-<packHash>/`
   with `entry.json` written last; later starts reuse them, and other cache
   keys are deleted;
6. adds them to every Penumbra collection as the temporary mod
   `Harmonia fonts` (`AddTemporaryModAll`, priority 99), again when Penumbra
   reports `Initialized`, and removes it on dispose.

Penumbra queues a temporary mod added outside a framework tick and applies it
on its next framework update, while the game reads its fonts during its own
startup, before that. So one second (and at least ten ticks) after the mod is
added, `GameFonts` asks the game to read its fonts again: virtual function 43
of `RaptureAtkModule`, called as `(module, false, true)` like Penumbra's
*Reload Fonts*. It is called only when `ReloadFontsSignature` finds exactly the
function in that slot and the slot points into the game code. Otherwise the
state is `NeedsReload` and the Translations page asks the player to press
*Reload Fonts* in Penumbra. When the UI module does not exist yet, the game has
read no font and nothing is called.

The game version is the `ffxiv` repository version Lumina reads from
`ffxivgame.ver`. Without Penumbra, or with a Penumbra API other than 5,
translations still apply and the Translations and Diagnostics pages report that
the glyphs are not applied. Like everything else, fonts follow the restart-only rule: the game
reads its fonts at startup, so a new pack or game version takes effect at the
next start.

## Configuration

| Field | Meaning |
| --- | --- |
| `PinnedPublisherKeys` | packId → trusted signing key fingerprint |
| `ApplyUnreviewedTranslations` | apply cells exported as unreviewed (default on) |
| `FollowTestingChannel` | accept `testing` releases from feeds (default off) |
| `NotifiedPackVersions` | packId → last release sequence the user was notified about |
| `LastSeenGameVersion` | a change triggers an immediate feed check |

## Runtime

Translations are written when the game stores a newly parsed Excel row in its
sheet (`IExcelPageRowResolver::StoreRow`), before any other code can read it.
The row buffer is rebuilt at its exact new size through the game's
`ExdEnvironment`, in the layout `ExcelRow_Parse_v3` produces. Choosing,
updating, or turning off a pack applies at the next game start; see
`AGENTS.md` for the details and the addresses.
