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
