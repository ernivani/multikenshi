# Kenshi-Online (The404Studios) - Comprehensive Codebase Analysis

## Research Date: 2026-03-27
## Repository: https://github.com/The404Studios/Kenshi-Online
## Purpose: Extract all useful patterns, approaches, and pitfalls for KServerMod development

---

## 1. Architecture Overview

### Project Structure (6 modules)

| Module | Type | Purpose |
|--------|------|---------|
| KenshiMP.Common | Static lib | Shared types, protocol, serialization, config |
| KenshiMP.Scanner | Static lib | Pattern scanning, MinHook wrapper, MovRaxRsp fix |
| KenshiMP.Core | Ogre DLL plugin | Hooks, networking, sync, UI - loaded by game engine |
| KenshiMP.Server | Standalone exe | Dedicated server with persistence |
| KenshiMP.MasterServer | Standalone exe | Server browser registry (port 27801) |
| KenshiMP.Injector | Win32 exe | Modifies Plugins_x64.cfg, launches game |

### Key Differences from KServerMod

| Aspect | KServerMod | Kenshi-Online |
|--------|------------|---------------|
| Injection | CreateRemoteThread + LoadLibraryA | Ogre Plugins_x64.cfg modification |
| Network transport | TCP (JSON-over-TCP via C# relay) | UDP (ENet, binary protocol) |
| Protocol format | Newline-delimited JSON | Binary packets with 8-byte headers |
| Server language | C# (.NET 9) | C++ (standalone ENet server) |
| Console | Custom GUI window (streambuf redirect) | Native MyGUI overlay in-game |
| Authority model | Host-authoritative (first client = host) | Server-authoritative (dedicated server) |
| Tick rate | Not fixed (JSON relay) | 20 Hz (50ms intervals) |
| Build system | MSBuild + dotnet | CMake + vcpkg |
| Max players | 2 (expanding) | 16 |

### Injection Method

Kenshi-Online uses the **Ogre3D plugin system** (borrowed from RE_Kenshi). The injector:
1. Modifies `Plugins_x64.cfg` to add `Plugin=KenshiMP.Core`
2. Copies `kenshi-online.mod` to the game's data directory
3. Writes connection config to `%APPDATA%\KenshiMP\client.json`
4. Launches the game normally

The DLL exports `dllStartPlugin()` and `dllStopPlugin()` which Ogre calls automatically. This avoids the AllocConsole-during-OGRE-init crash that KServerMod previously encountered.

**Key insight for us**: They do NOT use `DllMain` for initialization. `DllMain` only calls `DisableThreadLibraryCalls`. All real init happens in `dllStartPlugin()`.

---

## 2. Network Protocol (Binary over ENet/UDP)

### Packet Header (8 bytes, packed)
```cpp
struct PacketHeader {
    MessageType type;      // uint8_t - message type enum
    uint8_t     flags;     // Bit 0: compressed
    uint16_t    sequence;  // Sequence number
    uint32_t    timestamp; // Server tick
};
```

### Channel Architecture (3 channels)
- **Channel 0** (Reliable Ordered): Connection, world state, entity lifecycle, buildings, squads, chat
- **Channel 1** (Reliable Unordered): Combat, stats, health, equipment, inventory
- **Channel 2** (Unreliable Sequenced): Position updates, movement commands

**Design rationale**: Position updates are unreliable+sequenced (ENet drops late packets automatically, preventing stale position data from overwriting current state). Combat/health must be reliable but order doesn't matter between different entities. Connection/entity lifecycle MUST be ordered.

### Message Types (~60 distinct types)
Organized into categories with hex ranges:
- 0x01-0x08: Connection (handshake, keepalive)
- 0x10-0x13: World state (snapshots, time sync, zone data)
- 0x20-0x23: Entity lifecycle (spawn, despawn)
- 0x30-0x33: Movement (position updates, move commands)
- 0x40-0x47: Combat (attacks, hits, blocks, deaths, KOs)
- 0x50-0x53: Stats (health, equipment)
- 0x60-0x65: Inventory (pickup, drop, transfer, trade)
- 0x70-0x77: Buildings (build, progress, destroy, doors)
- 0x80-0x82: Chat
- 0x90-0x91: Admin commands
- 0xA0-0xA1: Server query (lightweight, no handshake)
- 0xB0-0xB3: Squad management
- 0xC0-0xC1: Faction relations
- 0xD0-0xD4: Master server registration
- 0xE0-0xE3: Pipeline debug (forwarded snapshots)
- 0xF0-0xF2: Lobby (faction assignment, ready, start)

### Serialization
Custom `PacketWriter`/`PacketReader` classes with explicit byte-level serialization:
- WriteU8/U16/U32/I32/F32
- WriteVec3 (3 floats)
- WriteString (length-prefixed, uint16 + raw bytes)
- WriteRaw (raw memory copy)

**Key safety**: `ReadString` has a `maxLen` parameter (default 1024) that rejects oversized strings. All reads return bool for failure detection.

### Connection Flow
1. Client sends `C2S_Handshake` (protocol version, player name, game version)
2. Server validates version, checks capacity, sanitizes name
3. Server sends `S2C_HandshakeAck` (player ID, server tick, time, weather)
4. Server sends `S2C_FactionAssignment` (assigns mod faction string + slot)
5. Server sends `S2C_PlayerJoined` for each existing player
6. Server sends world snapshot (all entities via `S2C_EntitySpawn` packets)
7. Client transitions to Connected phase

### Keepalive
- Client sends `C2S_Keepalive`, server responds with `S2C_KeepaliveAck`
- Server timeout: 10s min, 15s max (enet_peer_timeout)
- Client timeout after connect: 30s min, 60s max (generous for gameplay)
- **Important**: Connect timeout is only 5s, but session timeout is set higher after successful connection

### Server Query
Lightweight protocol for server browser - client sends `C2S_ServerQuery`, server responds with `S2C_ServerInfo` (player count, server name, etc.) without requiring a full handshake. Server disconnects the query peer after responding.

---

## 3. Entity Sync System

### Entity Registry (Thread-Safe)
- Uses `std::shared_mutex` for reader/writer locking
- Bidirectional maps: `EntityID -> EntityInfo` and `void* -> EntityID`
- Local entities start IDs at 0x10000000 to avoid collision with server-assigned IDs
- `RemapEntityId()` allows re-mapping local IDs to server-assigned IDs

### Entity State Machine
```
Inactive -> Spawning -> Active -> Despawning -> Inactive
                         |
                       Frozen (zone authority handoff)
```

### Authority Model
```
None         -> Server-managed
Local        -> This client owns & simulates
Remote       -> Another client owns, we interpolate
Host         -> Server/host authoritative
Transferring -> Authority handoff in progress
```

### Dirty Flags (Selective Replication)
Bitmask tracking what changed per entity:
```cpp
Dirty_Position, Dirty_Rotation, Dirty_Animation, Dirty_Health,
Dirty_Stats, Dirty_Inventory, Dirty_CombatState, Dirty_LimbDamage,
Dirty_SquadInfo, Dirty_FactionRel, Dirty_Equipment, Dirty_AIState
```

### Pointer Validation
Before registering a game pointer:
1. Minimum threshold check (> 0x10000)
2. Maximum canonical address (< 0x00007FFFFFFFFFFF)
3. 4-byte alignment check
This prevents invalid/SSO string pointers from being registered.

### NPC Hijacking (Brilliant Approach)
Instead of creating new characters for remote players (which risks crashes from faction pointer issues), they **hijack existing NPCs**:
1. Wait for the game to create an NPC via CharacterCreate
2. Pop a pending spawn request from the queue
3. Take over the just-created NPC: register it, teleport it, rename it, disable its AI
4. The NPC already has valid faction/model/animations - zero crash risk

This is their primary spawn strategy, falling back to direct factory calls only when necessary.

### Per-Player Spawn Cap
Maximum 4 spawns per player (MAX_SPAWNS_PER_PLAYER) to prevent entity explosion.

---

## 4. Interpolation System

### Adaptive Jitter Buffer
Per-entity jitter estimator using Exponential Moving Average (EMA):
```
jitterEma = alpha * jitter + (1 - alpha) * jitterEma
adaptiveDelay = 50ms + t * (200ms - 50ms)  // where t = normalized jitter
```
This dynamically adjusts the interpolation delay based on network conditions.

### Snapshot Ring Buffer
- 8 snapshots per entity (ring buffer)
- Each snapshot: timestamp, position, velocity, rotation, moveSpeed, animState
- Velocity computed from consecutive snapshots

### Interpolation Cases
1. **No data**: Return false
2. **Only future snapshots**: Use earliest (snap to it)
3. **Past all snapshots**: Dead reckoning extrapolation (max 250ms)
4. **Normal**: Linear lerp between bracket snapshots + slerp for rotation

### Snap Correction
When a large position discontinuity is detected:
- Below 5m: Normal interpolation handles it
- 5m to 50m: Smooth blend correction over 150ms
- Above 50m: Instant teleport

### Quaternion Compression
"Smallest-three" encoding: drop the largest component, pack 3 remaining into 10 bits each (30 bits + 2 bit index = 32 bits total). Includes proper slerp with nlerp fallback for near-identical quaternions.

---

## 5. Hook Implementation

### The `mov rax, rsp` Problem (CRITICAL)

Many Kenshi functions (compiled by MSVC x64) start with:
```asm
mov rax, rsp          ; 48 8B C4 - snapshot caller's RSP
push rbp
push rsi/rdi/r12-r15
lea rbp, [rax-0xNN]   ; derive RBP from RAX
sub rsp, 0xMM
```

The compiler **aliases** push-saved register slots with `[rbp+XX]` frame offsets. Any extra data on the stack (like MinHook's trampoline overhead) shifts pushes by 8 bytes, breaking the aliasing and corrupting ALL locals/register restores.

### The Solution: MovRaxRsp Fix (Two Runtime-Generated ASM Stubs)

**Naked Detour** (what MinHook JMPs to):
```
1. Check software bypass flag (skip if passthrough mode)
2. Check reentrancy depth counter (skip if nested call)
3. Save game's RSP to data slot
4. Save game's return address to data slot
5. Create 4KB+8 stack gap (sub rsp, 0x1008)
6. CALL C++ hook function
7. Pop stack gap (add rsp, 0x1008)
8. Restore game's return address at [RSP]
9. Decrement depth counter
10. RET to game caller
```

**Trampoline Wrapper** (what C++ hook calls as "original"):
```
1. Save C++ hook's RSP to data slot
2. Swap RSP to captured game RSP
3. Patch [RSP] with return_point address
4. Set RAX = RSP (what the function expects!)
5. JMP to trampoline+3 (skip mov rax,rsp)
... original function runs with ZERO extra bytes on stack ...
6. return_point: Swap RSP back to C++ hook's stack
7. RET to C++ hook
```

### Why 4KB Gap?
The trampoline wrapper swaps RSP to captured_rsp, so the original function's pushes land at the correct addresses. Without the gap, the C++ hook's stack frame would be overwritten by the original function's pushes.

### Software Bypass Flag
Instead of using `MH_EnableHook`/`MH_DisableHook` (which suspend all threads and re-patch function bytes), MovRaxRsp hooks use an atomic bypass flag:
- bypass=0: Hook active, calls C++ hook
- bypass=1: Passthrough, JMPs directly to raw trampoline

### Hook Manager Features
- Automatic MovRaxRsp detection (checks first 3 bytes for `48 8B C4`)
- `.pdata` validation: Verifies target is a real function entry point (not mid-function)
- Prologue byte logging for diagnostics
- Per-hook diagnostics: call count, crash count, enabled state
- VTable hooks supported (swap vtable entry directly)
- RAII `HookBypass` guard (deprecated but still available)

### Hooks That Are NOT Installed
Some functions are deliberately NOT hooked:
- **CharacterSetPosition**: `mov rax, rsp` prologue, called hundreds of times per frame. Position updates are polled from OnGameTick instead.
- **CharacterMoveTo**: `mov rax, rsp` AND has a 5th stack parameter. The naked detour cannot forward stack parameters. Movement sync via position polling.
- **ApplyDamage**: `mov rax, rsp` prologue, fires hundreds of times per combat tick. Would corrupt under rapid-fire calls.

### Deferred Event Processing
Combat hooks use a **lock-free ring buffer** to defer heavy operations:
1. Hook body: minimal work (call original + capture entity IDs into ring buffer)
2. `ProcessDeferredEvents()` runs from OnGameTick: logging, packet building, network sends

Rationale: hooks fire inside MovRaxRsp naked detours where spdlog/heap allocations would corrupt the 4KB stack gap.

---

## 6. Pattern Scanning

### Two-Phase Resolution
1. **IDA-style byte patterns** with `?` wildcards (primary)
2. **Runtime string xref scanner** as fallback (finds unique strings in .rdata, traces xrefs back to functions)

### Pattern Format
Standard IDA notation: `"48 8B C4 55 56 57 41 54 41 55 41 56 41 57 48 8D A8 A8 FE FF FF"` with `?` for wildcard bytes.

### RIP-Relative Resolution
```cpp
static uintptr_t ResolveRIP(uintptr_t instructionAddr, int operandOffset, int instructionLength);
static uintptr_t FollowCall(uintptr_t callAddr);  // E8 xx xx xx xx
static uintptr_t FollowJmp(uintptr_t jmpAddr);    // E9 xx xx xx xx
```

### Known Pointer Chains (Cheat Engine community)
Hardcoded fallbacks for v1.0.68:
```cpp
{"PlayerBase",  0x01AC8A90, {-1}}
{"Health",      0x01AC8A90, {0x2B8, 0x5F8, 0x40, -1}}
{"Money",       0x01AC8A90, {0x298, 0x78, 0x88, -1}}
```

### String Anchors (~35 functions)
Each game function has a unique string that appears in its body. The scanner finds these strings in memory and traces backward to find the function start:
```cpp
{"CharacterSpawn", "[RootObjectFactory::process] Character", 38}
{"CharacterDeath", "{1} has died from blood loss.", 29}
{"ZoneLoad",       "zone.%d.%d.zone", 15}
```

### GameFunctions Resolution
41 function pointers resolved at runtime, with `IsMinimallyResolved()` checking if at least CharacterSpawn + GameFrameUpdate/TimeUpdate are found (allowing singletons to be retried later).

---

## 7. Game State Reading/Writing

### Offset Tables
All game structure offsets are stored in runtime-configurable structs:
```cpp
struct CharacterOffsets {
    int name = 0x18;
    int faction = 0x10;
    int position = 0x48;
    int rotation = 0x58;
    int inventory = 0x2E8;
    int stats = 0x450;
    // ... many more
};
```

These match KServerMod's known offsets, confirming cross-project accuracy.

### Accessor Pattern
Clean `CharacterAccessor`, `SquadAccessor`, `BuildingAccessor`, `FactionAccessor`, `InventoryAccessor`, `StatsAccessor` classes that wrap a `uintptr_t` and use the offset table for safe memory reads.

### Writable Position Chain
Same chain as KServerMod:
```
character -> AnimationClassHuman* (+animClassOffset, discovered at runtime)
  -> CharMovement* (+0xC0)
    -> writable position struct (+0x320)
      -> Vec3 x,y,z (+0x20)
```

### AnimClass Offset Discovery
Uses deferred probing: schedule characters for animClassOffset discovery over multiple game ticks. Needs non-zero cached position to validate the chain (freshly spawned characters need several frames).

---

## 8. Client Lifecycle State Machine

```
Startup -> MainMenu -> Loading -> GameReady -> Connecting -> Connected
                                    ^                          |
                                    |__________________________|
                                          (on disconnect)
```

### Phase Detection
- **Loading**: Detected by a >2s gap between Present calls (game blocking on load screen)
- **GameReady**: World loaded, characters exist, OnGameLoaded has fired
- **Connecting**: ConnectAsync called, waiting for handshake
- **Connected**: Handshake done, hooks enabled, entities syncing

### Critical Hook Timing
- CharacterCreate hook installed **DISABLED** during loading (130+ character creates during savegame load would corrupt MovRaxRsp wrapper)
- Hook enabled in `OnGameLoaded()` after loading burst completes
- Hook disabled again on disconnect (zone-load bursts while disconnected are unsafe)

---

## 9. Thread Safety Patterns

### Mutex Usage
- `std::shared_mutex` for EntityRegistry (reader/writer lock)
- `std::mutex` for ENet operations (not thread-safe natively)
- `std::mutex` for per-player spawn cap tracking
- `std::mutex` for host spawn point (network thread writes, game thread reads)
- `std::recursive_mutex` for server state (public methods + internal HandlePacket)

### Atomic Variables
Extensive use of `std::atomic` for cross-thread state:
- `m_connected`, `m_gameLoaded`, `m_timeHookActive`
- `m_clientPhase` (with acquire/release memory ordering)
- `s_earlyPlayerFaction`, `s_fallbackFaction`
- Various diagnostic counters

### Double-Buffered Frame Data
Background workers fill buffer[writeBuffer], game thread reads buffer[readBuffer], atomics swap indices.

### Lock-Free Ring Buffer
Used for deferred combat events: single producer (hook) + single consumer (game tick), atomic head/tail with acquire/release semantics. Cap processing at 16 events per tick.

---

## 10. Faction Detection

### Multi-Stage Detection
1. **Early Loading Capture**: First character created during savegame load is the player's squad leader. Capture its faction pointer (`s_earlyPlayerFaction`).
2. **PlayerController**: Stores the authoritative local faction pointer after bootstrap.
3. **Fallback**: Any valid faction seen from recent character creates (`s_fallbackFaction`).
4. **Server-Assigned**: Server sends `S2C_FactionAssignment` with mod faction string.

### Faction Assignment Flow
Server assigns faction strings from the mod (e.g., "10-kenshi-online.mod" for Player 1, "12-kenshi-online.mod" for Player 2). The client patches these in `.rdata` before save load to determine which faction's characters they control.

### Entity Ownership Matching
In `SEH_ReadAndRegisterEntity()`, only characters matching the local player's faction are registered. This prevents random NPCs/buildings from being sent as player entities.

---

## 11. Character Spawning

### Spawn Strategies (Ordered by Priority)
1. **NPC Hijacking**: Wait for game to create an NPC, intercept via CharacterCreate hook, repurpose for remote player
2. **Factory Create** (RootObjectFactory::create at RVA 0x583400): High-level dispatcher that builds proper request structs internally. Bypasses stale-pointer problem.
3. **CreateRandomChar** (RVA 0x5836E0): Creates random NPC character. Last resort.
4. **Direct Factory Call**: Uses captured request struct + trampoline. Most fragile.

### Pre-Call Data Capture
During loading, the hook captures:
- Factory pointer (RootObjectFactory instance)
- Template data (first 1024 bytes of the request struct)
- Position offset within struct (auto-detected by comparing struct fields to created character's position)
- GameData pointer offset (auto-detected similarly)

### Spawn Queue
Thread-safe queue with retry logic:
- Max retries per spawn request
- Per-player spawn cap (4 max)
- Queue cleared on disconnect
- Requeue on spawn failure

### Direct Call Stub
For calling hooked functions directly (bypassing the hook), a custom code stub is generated:
```asm
mov rax, rsp           ; Capture correct RSP
jmp [rip+0]            ; Jump to rawTrampoline+3 (skip mov rax,rsp)
dq rawTrampoline + 3   ; Target address
```

---

## 12. Server Architecture

### Server Features
- Entity management with server-authoritative IDs
- Server-authoritative combat resolution
- Zone-based interest management (3x3 grid around each player)
- World persistence (save/load to JSON file)
- Auto-save at configurable intervals
- UPnP port mapping (with Windows Firewall fallback)
- Master server registration with exponential backoff reconnection
- Admin commands (kick, time, weather, announce)
- Server query protocol for browser (no handshake required)

### Server-Side Validation
- Position validation: reject NaN/Inf/extreme values
- Entity ownership: all mutations require ownership proof
- Combat distance validation: melee < 15m, ranged < 150m
- Trade validation: quantity limits, entity existence checks
- Faction relation bounds: -100 to +100
- Zone bounds: +/-500 zones
- Global entity limit: 2048 synced entities
- Protocol version matching

### World Persistence
`SaveWorldToFile`/`LoadWorldFromFile` using JSON format with entities, time, weather, and next entity ID.

### Master Server Connection
- Separate ENet host for master connection (1 peer, 1 channel)
- Register on connect, heartbeat every 30s
- Exponential backoff reconnection (5s -> 10s -> 20s -> 40s -> max 60s)
- Deregister on shutdown

---

## 13. Known Pitfalls and Their Solutions

### Pitfall 1: MovRaxRsp Corruption
**Problem**: MinHook's trampoline for `mov rax, rsp` functions corrupts the stack.
**Solution**: Custom naked detour + trampoline wrapper that preserves the exact stack layout.
**Lesson for us**: We should check all hooked functions for `48 8B C4` prologue.

### Pitfall 2: Loading Burst (130+ Creates)
**Problem**: During savegame load, 130+ CharacterCreate calls fire rapidly. The MovRaxRsp wrapper's global RSP slots get corrupted by concurrent/rapid calls.
**Solution**: Hook starts DISABLED. Capture factory data from first call, then disable hook entirely for remaining loading. Re-enable after loading completes.
**Lesson for us**: Our spawnSquadBypass might face similar issues during loading.

### Pitfall 3: Stale Pointers in Request Structs
**Problem**: Request structs captured during loading have stale external pointers (faction, squad, AI). Using them for spawning crashes.
**Solution**: Refresh pre-call data from connected NPC spawns (fresh structs have live heap pointers). Or use high-level factory functions that build fresh structs internally.

### Pitfall 4: MH_Uninitialize Crashes at Exit
**Problem**: `MH_Uninitialize` frees trampoline memory, but Kenshi's atexit handlers may still call functions through stale pointers referencing those trampolines.
**Solution**: Only DISABLE hooks at shutdown (restores original bytes). Never remove or uninitialize MinHook. Trampoline memory freed when process exits.

### Pitfall 5: Connect Timeout vs Session Timeout
**Problem**: The initial connect timeout (5s) is too short for ongoing gameplay.
**Solution**: Set generous session timeout (30s min, 60s max) AFTER successful connection.

### Pitfall 6: Heap Allocation in Hook Context
**Problem**: spdlog/std::string allocations inside MovRaxRsp naked detours corrupt the 4KB stack gap.
**Solution**: Deferred event processing via lock-free ring buffer. Hooks do minimal work, game tick handles logging/networking.

### Pitfall 7: ENet Thread Safety
**Problem**: ENet host/peer operations are not thread-safe.
**Solution**: Mutex wrapping all ENet operations. Non-blocking service call (timeout=0) minimizes lock contention.

### Pitfall 8: Zone-Load Bursts While Disconnected
**Problem**: Walking near a town while disconnected triggers 90+ CharacterCreate calls through the hook, corrupting the heap.
**Solution**: Disable CharacterCreate hook when disconnecting from multiplayer.

### Pitfall 9: NaN/Inf Position Propagation
**Problem**: A single NaN position corrupts the interpolation ring buffer for multiple frames.
**Solution**: Reject NaN/Inf at every entry point (AddSnapshot, server HandlePositionUpdate, world snapshot).

### Pitfall 10: SSO String Pointers as Game Objects
**Problem**: Small String Optimization (SSO) stores data inline, making string addresses look like valid pointers.
**Solution**: Pointer validation: minimum threshold (0x10000), canonical address check, alignment check.

---

## 14. What We Can Learn From / Should Adopt

### 1. Binary Protocol (vs our JSON)
Their binary protocol is far more efficient. A position update is ~30 bytes vs hundreds in JSON. For 16 players at 20Hz, this matters enormously. We should consider migrating to binary framing.

### 2. ENet (vs our TCP)
UDP with reliability layers (ENet) is fundamentally better for game networking:
- Unreliable sequenced for positions (automatically drops stale packets)
- Reliable ordered for entity lifecycle
- No head-of-line blocking (unlike TCP)

### 3. Dedicated Server (vs our relay)
Their dedicated server provides:
- Server-authoritative combat resolution
- Proper ownership validation
- World persistence
- Master server integration
We could evolve our C# relay into a proper game server.

### 4. Interpolation System
Our current approach is direct position writes. Their system is much more sophisticated:
- Adaptive jitter buffer
- 8-snapshot ring buffer per entity
- Dead reckoning extrapolation
- Snap correction with smooth blending
- Proper slerp for rotation

### 5. Entity Registry with Bidirectional Lookup
Their `EntityRegistry` with `shared_mutex` and bidirectional maps (ptr<->netId) is cleaner than our `charsByName` approach. Server-assigned IDs prevent ID collisions in multi-client scenarios.

### 6. Accessor Pattern for Game Memory
Their `CharacterAccessor`, `SquadAccessor`, etc. with configurable offset tables is much cleaner than scattered raw pointer arithmetic. We should adopt this pattern.

### 7. NPC Hijacking for Remote Players
Instead of trying to spawn new characters (which has faction/crash issues), hijacking existing NPCs is brilliantly pragmatic. Zero faction issues since the NPC's original faction is already valid.

### 8. Deferred Hook Processing
Using a ring buffer to defer heavy operations from hook context to game tick is a pattern we should adopt for crash prevention.

### 9. Software Bypass Flag
Using an atomic bypass flag instead of MH_Enable/MH_Disable avoids thread suspension and detour chain corruption. Essential for MovRaxRsp hooks.

### 10. Zone-Based Interest Management
Their zone system (750m zones, 3x3 grid) could help us scale beyond 2 players without sending every entity to every client.

### 11. Position Change Threshold
Only send position updates when movement > 0.1m. Avoids unnecessary network traffic for stationary characters.

### 12. Comprehensive SEH Protection
Every game memory read wrapped in `__try/__except`, with separate pure-C helper functions (no C++ objects with destructors, which MSVC forbids in SEH functions). We do some of this but not as systematically.

---

## 15. What We Should Avoid

### 1. Over-Engineering Early
Their codebase has 200+ files and massive complexity. For 2-4 player support, we don't need zone engines, pipeline orchestrators, or master servers. Keep it simple.

### 2. Hardcoded RVAs
They use hardcoded RVAs for some functions (e.g., `0x583400` for FactoryCreate). These break on different game versions. Pattern scanning is more robust.

### 3. Complex Hook Workarounds
The MovRaxRsp fix is necessary but extremely complex and fragile. Where possible, prefer polling (their movement_hooks explicitly avoid hooking SetPosition and MoveTo, using polling instead).

### 4. Monolithic Server State
Their server stores ALL entity state (health, equipment, position, etc.) which creates a single point of failure and massive state management burden. Our host-authoritative model is simpler for small player counts.

---

## 16. Verified Kenshi Offsets (Cross-Referenced with KServerMod)

These offsets appear in both projects, providing high confidence:

| Field | Offset | Verified By |
|-------|--------|-------------|
| character.faction | +0x10 | Both projects |
| character.name | +0x18 | Both projects |
| character.gameDataPtr | +0x40 | Both projects |
| character.position | +0x48 | Both projects |
| character.rotation | +0x58 | Both projects |
| character.inventory | +0x2E8 | Both projects |
| character.stats | +0x450 | Both projects |
| character.healthChain | +0x2B8 -> +0x5F8 -> +0x40 | Both projects |
| character.charMovement | AnimClass+0xC0 | Both projects |
| character.writablePos | CharMovement+0x320+0x20 | Both projects |
| faction.id | +0x08 | Both projects |
| faction.name | +0x10 | Both projects |
| world.gameSpeed | +0x700 | Both projects |
| world.characterList | +0x0888 | Both projects |
| world.paused | +0x08B9 | Both projects |

---

## 17. Pattern Signatures for Key Functions

These could be used to validate or replace our pattern scanning:

| Function | RVA (v1.0.68) | String Anchor |
|----------|---------------|---------------|
| CHARACTER_SPAWN | 0x581770 | "[RootObjectFactory::process] Character" |
| CHARACTER_DESTROY | 0x38A720 | "NodeList::destroyNodesByBuilding" |
| CHARACTER_SET_POSITION | 0x145E50 | "HavokCharacter::setPosition moved someone off the world" |
| CHARACTER_MOVE_TO | 0x2EF4E3 | "pathfind" |
| CHARACTER_DEATH | 0x7A6200 | "{1} has died from blood loss." |
| GAME_FRAME_UPDATE | 0x123A10 | "Kenshi 1.0." |
| TIME_UPDATE | 0x214B50 | "timeScale" |
| ZONE_LOAD | 0x377710 | "zone.%d.%d.zone" |
| SAVE_GAME | 0x7EF040 | "quicksave" |
| LOAD_GAME | 0x373F00 | "[SaveManager::loadGame] No towns loaded." |
| SQUAD_CREATE | 0x480B50 | "Reset squad positions" |
| AI_CREATE | 0x622110 | "[AI::create] No faction for" |
| FACTION_RELATION | 0x872E00 | "faction relation" |
| BUILDING_PLACE | 0x57CC70 | "[RootObjectFactory::createBuilding] Building" |

---

## 18. Summary of Key Takeaways

1. **Binary protocol over UDP** is the standard for game networking. Our JSON-over-TCP works but limits scalability.
2. **The `mov rax, rsp` problem** is the single biggest Kenshi hooking challenge. Their solution is sophisticated but proven.
3. **NPC hijacking** is a safer spawning strategy than factory calls.
4. **Deferred processing** (ring buffers, double-buffering) prevents crashes in hook context.
5. **Adaptive interpolation** with jitter estimation provides smooth remote movement.
6. **Zone-based interest management** is essential for scaling beyond a few players.
7. **Comprehensive input validation** (NaN/Inf rejection, ownership checks, bounds checks) prevents cascading failures.
8. **Hook timing matters**: Disable during loading bursts, enable for runtime spawns only.
9. **Software bypass flags** are safer than MinHook enable/disable for `mov rax, rsp` hooks.
10. **The same game offsets** are independently verified by both projects, giving high confidence.
