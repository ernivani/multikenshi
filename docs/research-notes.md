# Kenshi Reverse Engineering & Research Notes

Consolidated from early development notes (v0.0, v0.0.1) and Cheat Engine research.

---

## v0.0 - Initial Proof of Concept

Early DLL entry point with hardcoded GOG offsets for pause/unpause:

```cpp
void HelloWorld() {
    Sleep(30000); // TODO replace this with when game loaded
    uintptr_t moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandle(NULL));
    GameWorldClass* gameWorld = reinterpret_cast<GameWorldClass*>(moduleBase + 0x2133040);
    using GameWorldOffset = void(*)(GameWorldClass*, bool);
    GameWorldOffset togglePause = reinterpret_cast<GameWorldOffset>(moduleBase + 0x7876A0);

    togglePause(gameWorld, true);   // Pause the game
    Sleep(2000);
    togglePause(gameWorld, false);  // Unpause the game
}
```

## v0.0.1 - Console + Game Speed Control

Added AllocConsole for interactive speed control:

```cpp
struct GameWorldClass {
    char padding1[0x700];
    float gameSpeed;     // Offset 0x700
    char padding2[0x1B5];
    bool paused;         // Offset 0x8B9
};

void spawn_console() {
    AllocConsole();
    FILE* stream;
    freopen_s(&stream, "CONOUT$", "w", stdout);
    freopen_s(&stream, "CONIN$", "r", stdin);
    std::cout << "[Console Ready] Enter new game speed:\n";
}
```

DLL exported as OGRE plugin (`dllStartPlugin`), spawned thread for console loop.

---

## Pause Game (Cheat Engine Assembly)

```asm
alloc(newmem, 2048)

newmem:
  sub rsp, 28
  mov rdx, 0
  mov rcx, kenshi_GOG_x64.exe+2133040
  call kenshi_GOG_x64.exe+7876A0
  add rsp, 28
  xor eax, eax
  ret

createthread(newmem)
dealloc(newmem)
```

**setPaused pattern** (byte signature):
```
48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 50 0F 29 74 24 40 F3 0F 10 B1 00 07 00 00
```

---

## Key Memory Offsets (GOG binary)

| Offset | Description |
|--------|-------------|
| `+2133040` | GameWorldClass pointer |
| `+7876A0` | togglePause function |
| `+16FB998` | Character movement vtable |
| `+16F1558` | CharacterHuman vtable |
| `+16C2F68` | Non-writable data (faction?) |

## Character / Struct Layout

```
super character struct:
0x000 - pointer to pos holder
  +0x20  vector3 floats (positions divided by 10!)
0x088 - pointer to CharacterHuman struct
  +0x10  faction pointer
  +0x40  gamedata pointer
    +0x58  stringID
  +0x650 AI pointer
    +0x20  AI task_system pointer
      +0x80  task_move double pointer
      +0x1B0 bool is idle
  +0x658 active platoon pointer

task_move:
  +0x58 movevector3

CharBody:
  +0x100 GameDataCopyStandalone

gameData:
  +0x28 name

building:
  +0x10  faction pointer
  +0x164 float building_progress
  +0x1F8 TownBuildingsManager
    +0xA8 town
      +0x10 faction
      +0x18 name
```

## Spawn / Birth Function

```
kenshi.exe+582587 - call qword ptr [r10+00000378]

virtual bool giveBirth(
    GameDataCopyStandalone*,
    const Ogre::Vector3&,
    const Ogre::Quaternion&,
    GameSaveState*,
    ActivePlatoon*,
    Faction*
)

Registers: rcx=this, rdx=GameDataCopyStandalone*, r8=Vector3&, r9=Quaternion&
Stack: GameSaveState*, ActivePlatoon*, Faction*
```

## Faction IDs

| Faction | ID | Source |
|---------|-----|--------|
| Holy Nation Outlaws | 42022 | rebirth.mod |
| Shek Kingdom | 11624 | Dialogue (10).mod |
| Nameless | 204 | gamedata.base |
| Player 1 | 10 | multiplayr.mod |
| Player 2 | 12 | multiplayr.mod |

## Item System

```
kenshi.exe+7490E5 - mov [rdi+000000DC], r13d  (moveItems: rdi=item, rbx=inventorySection)
kenshi.exe+715FE0 - call qword ptr [rax+18]    (changing r8/r9 changes real position)
kenshi.exe+746F1C - call qword ptr [rax+60]    (_addToList virtual, RVA=0x5CD620, vtable offset=0x60)
kenshi.exe+714A33 - call kenshi.exe+538EB       (item constructor)
kenshi.exe+71374C - call kenshi.exe+349F0       (getItem(inventorySection, x, y))
kenshi.exe+713AE8 - call qword ptr [rax+2A8]   (getHand(item*))
```

## Useful Addresses

```
kenshi_GOG_x64.exe+62B537  - function call to create stats
kenshi_GOG_x64.exe+62E910  - function to make char
kenshi_GOG_x64.exe+582587  - before calling to make char
kenshi_GOG_x64.exe+5D8F33  - reads currently selected CharacterHuman (rsi)
kenshi_GOG_x64.exe+D1C63   - moving character to other squad
kenshi_GOG_x64.exe+7961BC  - swapping characters
kenshi_GOG_x64.exe+148783  - when character is walking
kenshi.exe+65F6C7          - get all characters
kenshi.exe+50D212          - set task to aimless
kenshi.exe+599490           - set task to walk to point
```

## RTTI Class Name Lookup Steps

1. Search string `.?AV` and pick one
2. Search for that address - 0x10
3. If result - 0xC ~= 1 (byte) then discard
4. Search for remaining address + 8
5. Profit

## Cheat Engine Scripts

### Teleport All Characters to Location

```lua
local scan = createMemScan()
scan.firstScan(soExactValue, vtQword, rtRounded, "7ff61810b998", '',
    0, 0xFFFFFFFFFFFFFFFF, "*W*X*C", fsmNotAligned, '1', true, true, false, true)
scan.waitTillDone()

local foundList = createFoundList(scan)
foundList.initialize()

for i = 1, foundList.Count do
    local baseAddress = tonumber(foundList[i-1], 16)
    local offsetAddress = baseAddress + 0x320
    local dereferencedAddress = readPointer(offsetAddress)
    if dereferencedAddress ~= nil and dereferencedAddress ~= 0 then
        writeFloat(dereferencedAddress + 0x20, -5096.947754)
        writeFloat(dereferencedAddress + 0x24, 152.7362671)
        writeFloat(dereferencedAddress + 0x28, 297.7177429)
    end
end
foundList.deinitialize()
scan.destroy()
```

### Find All MSVC RTTI Class Instances

(Full script for scanning vtables and finding class instances - see `kenshi-items.CT` for the Cheat Engine table.)

### Pause on Specific Float Value

Use: https://gregstoll.com/~gregstoll/floattohex/

```asm
[ENABLE]
alloc(newmem, 2048, "OgreMain_x64.dll"+29EB2)
newmem:
cmp eax, 0xc746a12f
je edit
jmp originalcode
edit:
mov eax, 0xc7473800
originalcode:
mov [rcx], eax
mov eax, [rdx+04]
exit:
jmp returnhere

"OgreMain_x64.dll"+29EB2:
jmp newmem
returnhere:

[DISABLE]
dealloc(newmem)
"OgreMain_x64.dll"+29EB2:
db 89 01 8B 42 04
```
