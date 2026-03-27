# Stable Sync Findings — 2026-03-27

## setPaused Discovery (Steam Binary)

**RVA**: `0x787d40` (instruction writing at `0x787d82`)

**How found**: Scanned .text section for any x64 instruction writing to displacement `0x8B9` (GameWorldClass->paused offset). Found a `mov byte [reg+0x8B9], reg` instruction, then walked backwards to the function boundary (INT3 padding `CC CC`).

**Scan technique**: Search for 4-byte displacement `B9 08 00 00` in .text, then check if preceding bytes form a valid MOV byte write instruction (opcode `0x88` or `0xC6`, with or without REX prefix `0x40-0x4F`), with ModRM mod=10 (disp32 addressing).

**Function signature**: `void setPaused(GameWorldClass* gw, bool pause)` — x64 cdecl: rcx=gw, dl=pause

**Unpause sequence** (required to properly update UI):
```cpp
gameWorld->paused = false;
gamePauseFn(gameWorld, true);
gamePauseFn(gameWorld, false);
```
Just writing `gameWorld->paused = false` directly does NOT update the UI — the pause HUD stays visible and game functions break.

## Character Creation vs Network Sync

**Root cause**: ANY network activity (TCP send/recv) during character creation breaks it. Not the map walks, not applyWorldUpdate — the TCP activity itself.

**Solution**: Warmup delay. Don't send entity data or apply world updates until `getSpeedFloat() > 0` AND at least 5 seconds have passed. During warmup, send empty arrays.

**What was tested**:
1. Network disabled → character creation works
2. Network + Sleep(100) throttle → broken
3. Network + 30s empty arrays + no applyWorldUpdate → broken
4. Network + 30s pure Sleep (no send/recv at all) → broken (TCP connection exists)
5. Warmup with fullSyncActive check (speed > 0 && 5s) → WORKS

**Key insight**: The warmup delay + speed-based activation is the correct approach. Character creation runs at speed=0 (paused), so fullSyncActive never triggers until the player enters gameplay.

## Speed Control Architecture

**Host-authoritative with server override**:
- Host sends speed in every `ps` message
- Server stores host's speed, relays to guests
- Server `/speed N` sets override that applies to ALL clients (including host)
- Server `/speed reset` clears override
- Only host's speed is accepted by server (guests' speed ignored)

**Client enforcement**:
- 60Hz loop (Sleep 16ms) checks g_enforcedSpeed
- If >= 0: writes `gameWorld->gameSpeed` every tick (blocks local button clicks within 16ms)
- If player pauses while enforced: calls `setPaused` properly to unpause (updates UI)
- getSpeedFloat() returns enforced speed when active (prevents server seeing 0.0 from paused state)
- When server stops sending speed field in wu: clears enforcement (g_enforcedSpeed = -1)

**Server-side wu speed logic**:
- For host with no override: omit `speed` field (host controls naturally)
- For host with override: include `speed` field
- For guests: always include `speed` field (host's speed or override)

## Faction Filtering

**Problem**: Cross-thread faction reads (network thread reading `anim->character->faction->getName()`) always SEH-fault on the network thread — 100% failure rate.

**Solution**: Store faction per character on the GAME thread during `onCharUpdate`. New `charFactions` map (`name -> faction string`) populated in `gameState.cpp`. Network thread reads from this map (SEH-protected) to filter characters by player faction.

**gameState.cpp change**: Added `charFactions[nameStr] = facName` in the new-character branch of onCharUpdate. Also added name re-read on updates (catches character creation renames like "Alec" -> "Alecs").

## Thread Safety Approach

**Cannot use CRITICAL_SECTION in gameState.cpp** — even adding lock/unlock wrapping to onCharUpdate breaks character creation. Confirmed by testing.

**Alternative**: SEH-guarded map walks in plain-C helper functions:
- `safeCollectChars()`: walks charsByName + charLastSeen + charFactions under __try/__except, collects into flat CharEntry array
- `safeCollectBuilds()`: walks builds under __try/__except, collects into flat BuildEntry array
- `safeFindChar()`: linear search with memcmp (can't use std::string::find inside __try due to MSVC SEH restriction)
- Cap at 512 chars / 256 buildings per collection

If the game thread corrupts the tree mid-walk, SEH catches the fault and returns what was collected so far. Infinite loops from tree corruption are the remaining risk (accepted tradeoff).

## Protocol Changes (v0.4)

**Merged ps message** (client -> server):
```json
{"t":"ps", "speed":1.0, "pf":"Sans-Nom", "chars":[{"n":"Kumo","x":1,"y":2,"z":3}], "buildings":[...]}
```
- All clients send same format (no separate `ws` message)
- Server knows who is host via `_hostId`
- 1 send -> 1 receive per cycle (no desync)

**wu message** (server -> client):
```json
{"t":"wu", "speed":1.0, "players":[{"id":2,"sn":"friend","chars":[...]}], "buildings":[...]}
```
- `speed` field only present when server wants to control client
- `players` excludes the recipient's own data

**New messages**:
- `ping/pong`: server heartbeat (30s silence -> ping, 45s -> disconnect)
- `hostChange`: server notifies new host on reassignment

## Steam Binary Offsets (v1.0.68)

| Offset | RVA | Discovery Method |
|--------|-----|-----------------|
| charUpdateHook | 0x65fd27 | Pattern: `48 8B 8B 20 03 00 00 40 88 B3 7C 03 00 00` |
| buildingUpdateHook | 0x9fb317 | Pattern: `48 8B 43 60 4C 8B 24 28 49 8B CC 49 8B 04 24 FF 90 D8 00 00 00` |
| spawnSquadBypass | 0x22357b | Pattern: `48 8D AC 24 30 FF FF FF 48 81 EC D0 01 00 00` |
| setPaused | 0x787d40 | Displacement scan: write to [reg+0x8B9] -> function boundary |
| GameDataManagerMain | 0x2134130 | Runtime frequency analysis (most-referenced .data address with 55800+ hits) |
| gameWorldOffset | 0x2134110 | Derived: GameDataManagerMain - 0x20 |
| squadSpawningHand | 0x21345b0 | Derived: GameDataManagerMain + 0x480 |
| spawnSquadFuncCall | not found | MOV scan in spawnSquadBypass function (Steam binary uses different encoding) |

## Mod Issues

- `mods.cfg` gets wiped by Steam verify — needs auto-restore
- `__mods.list` also persists mod state — must be cleared alongside mods.cfg
- The kenshi-online.mod has broken game starts for our use case (dead characters, extra NPCs)
- Currently using vanilla Wanderer start without any mod — works for testing
- Future: create proper multiplayer mod with Kenshi FCS tool
