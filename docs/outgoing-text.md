# Game text sent to the server

Harmonia translates Excel rows used by both display code and command code.
A display name must not become a translated label in a message sent to other
players. Player-written prose, names, party-finder descriptions, and custom
emotes remain the player's text; this is not a Cyrillic filter.

## Chat encoding

Audited against installed game build `2026.10.16.0000.0000`. In this build,
`PronounModule::ProcessString` dispatches to an encoder at `0x1409BF930`
or a decoder at `0x1409C1090` (preferred image base `0x140000000`).
These addresses are evidence, not runtime offsets: the hook locates a checked
dispatch signature and follows its relative jump into the loaded code section.

The dispatch forwards a fifth argument (flags) from the stack. ClientStructs'
four-argument `ProcessString` declaration does not describe that argument.
The hook therefore intercepts the encoder branch, with the actual arguments
`(module, input, maxLength, flags)`, and forwards them unchanged.

The encoder expands chat parameters, including those used from macros, into
`Fixed(200,...)` payloads. The result is the wire representation, rather than
the `Link` macros the receiving client's decoder produces for display.
`OutgoingChatHook` normalizes the returned string before it reaches its caller.
It does not modify the player's input, inventory objects, or received messages.

| Path | Wire representation | Handling |
| --- | --- | --- |
| Item Link / `<item>` (`TextCommandParam` 1096) | `Fixed(200,4,itemId,rarity,0,0,name)` | Original client-language name from `Item` or `EventItem`; preserve id, rarity, HQ/collectable id encoding and marks. |
| Quest link / `<quest>` (1042) | `Fixed(200,12,questId,0,0,0,title)` | Original title from `Quest`; preserve all numeric fields. |
| Status link / `<status>` (1114) | `Fixed(200,10,statusId,1,title,description)` | Use the game's existing `Fixed(200,10,statusId,0,0)` form. Neither custom title nor description is transmitted; the recipient resolves them by id. |
| Position / flag | `Fixed(200,3,...)` with numeric territory, map, coordinates and flags | No sheet label is carried in this payload. The receiving decoder reads its own map text. |
| Class/job and level | `Fixed(200,2,...)` with numeric identifiers | No translated label transmitted in this payload. |
| NPC/object sheet name | `Fixed(200,6,objStrId)` | No translated sheet name transmitted in this payload. |
| Player names, dynamic object names, literal fixed strings, party-finder owner | Fixed subtypes 1, 7, 11 | Preserve them. These carry literal names without a reliable sheet/row identity; replacing them by a reverse text match could change player content. |
| Auto-translate tokens, sound effects, remaining time | Tokens or numeric fixed parameters | Preserve them. |

Only the exact audited item/quest/status shapes are changed. Unknown group,
subtype, arity or expression types remain unchanged. Original names are read
through `OriginalSheets`, including when `TranslatePluginData` has translated
the Lumina instance shared with other plugins. No reverse translation or
search-and-replace in ordinary chat prose is performed.

Unknown payload shapes remain unchanged. Exceptions leave the native result
unchanged and are counted and logged, including a missing original row or an
original label that would exceed the encoder's byte limit. The diagnostics
report shows whether the hook installed, how many lines changed, and errors;
this is a checked set of supported paths, not a promise to intercept every
possible string sent by the client or another plugin.

## Other server-facing text

`ServerNames` keeps the original Addon rows for the apartment and private-room
default names, and the default company airship and submersible names. These
are script results sent to the server, outside chat encoding. Run
`tools/CompatibilityCheck` after game updates to verify the script constants.

`SayHook` handles translated quest phrases when `OriginalSayPhrases` is enabled.
Its quest/step/target checks and fallback behavior are independent of chat-link
normalization. With the option disabled, a phrase typed by the player remains
their text. Tests for `SayPhrases` scan the installed game's quest scripts.

No global replacement is applied to free-company names, search comments,
party-finder descriptions, macros, custom emotes, or other player-authored text.
Plugins bypassing the game's chat encoder are outside this hook's coverage.

## Scope

This change covers the confirmed item, quest and status text carried by the
chat encoder. It does not add a process-wide network monitor or modify
unrecognized packets. The existing script default-name and Say-phrase
protections remain independent. No additional translation leak outside these
paths was established by the static follow-up; that is not proof that every
other outgoing packet is free of translated text.

Further fixes should start from a reproduced outgoing field and its source.
Searching arbitrary packet bytes for Cyrillic cannot distinguish a translated
game label from player-authored text or binary data.

## Verification

`dotnet test --filter OutgoingChat` checks independently written wire fixtures:
normal/HQ/collectable/event items, quest links, status title and description,
multiple links next to Russian prose, multibyte names and long string lengths,
idempotence, missing rows, malformed/unknown payloads and length limits.

With `HARMONIA_GAME` set to the installed `game/sqpack` directory, those tests
also verify the encoder dispatch and references to all four audited wire
templates in the executable, the parameter row ids, and every nonempty
item/event-item/quest name in English, Japanese, German and French. A patch
that changes this dispatch or these templates needs a new native-code audit.

These tests do not execute a detour in a running game or observe server packets.
Before release, use a second client without Harmonia to check inventory Link,
links forwarded from chat and the dictionary, `<item>` in a macro, normal/HQ/
collectable/event items, quest links, and statuses with descriptions. Check
position/flag links and ordinary Russian prose as preservation cases. Inspect
the diagnostics report for hook installation and replacement errors. Repeat
the housing/airship/submersible operations when their script contracts change.
