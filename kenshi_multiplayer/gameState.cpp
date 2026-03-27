#include "pch.h"
#include "gameStateSetters.h"
#include "gameStateGetters.h"
#include "gameState.h"
#include "offsets.h"
#include "crashlog.h"
#include <string>
#include <sstream>
#include <vector>
#include <iostream>
#include <map>
#include "utils.h"
#include <queue>
namespace gameState {
    uintptr_t moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandle(NULL));
    structs::GameWorldClass* gameWorld = nullptr;

    void initGameWorld() {
        gameWorld = reinterpret_cast<structs::GameWorldClass*>(moduleBase + offsets::gameWorldOffset);
    }

    std::vector < trackedVariable> variables;
    bool gameLoaded = false; // true = in main menu
    void init() {
        while (gameLoaded == false)Sleep(500);
        setupHooks();
        // Old tracked variables no longer used — JSON protocol handles everything
        // Kept as empty vector for backward compatibility with getData/setData
        std::cout << utils::ts() << "Hooks + JSON protocol ready." << std::endl;
    }
    void setData(const std::string& data) {
        std::string line;
        std::string key;
        std::istringstream iss(data);
        while (std::getline(iss, line)) {if (key == "") { key = line;continue; }
            trackedVariable& curVar = variables[std::stoi(key)];
            if(curVar.oldVal == curVar.getter()){//only accept server changes if local var wasn't changed
                if (line != curVar.getter()) {
                    //std::cout << line <<" " << curVar.getter() << "\n";
                    //std::cout << "applied from server: "<< key << "\n";
                    curVar.setter(line);
                }
                curVar.changed = false;
            } else {
                //std::cout << "changed" << key << "\n";
                curVar.changed = true;
            }
            curVar.oldVal = curVar.getter();
        key = "";}
    }
    std::string getData() {
        std::string data = "0\nB\n";
        for (int i = 1;i<variables.size();i++) {
            if (variables[i].changed == false)continue;
            data += std::to_string(i) + "\n" + variables[i].getter() + "\n";
            variables[i].changed = false;
        }
        return data;
    }
    std::map<structs::AnimationClassHuman*, std::pair<std::string, long long>> chars;
    std::map<std::string, structs::AnimationClassHuman*> charsByName;
    std::map<std::string, long long> charLastSeen;
    std::map<structs::Building*, std::pair<std::string, long long>> builds;
    structs::AnimationClassHuman* player = 0;
    structs::AnimationClassHuman* otherplayers = 0;
    // SEH-safe helper: read a pointer from an object at a given offset.
    // Returns 0 if the read faults.
    __declspec(nothrow) static uintptr_t safeReadPtr(void* base, size_t offset) {
        __try {
            return *(uintptr_t*)((char*)base + offset);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            return 0;
        }
    }
    // Faction name of the auto-detected player (empty until detected)
    std::string playerFactionName;

    void onCharUpdate(structs::AnimationClassHuman* num) {
        if (!num) return;
        structs::CharacterHuman* ch = (structs::CharacterHuman*)safeReadPtr(num, 0x2D8);
        if (!ch) return;
        if (chars.find((structs::AnimationClassHuman*)ch) == chars.end()) {
            const char* chosenName = ch->getName();
            if (!chosenName || !utils::isValidName(chosenName)) return;
            std::string nameStr(chosenName);
            chars[(structs::AnimationClassHuman*)ch] = std::make_pair(nameStr, 0);
            charsByName[nameStr] = num;
            charLastSeen[nameStr] = GetTickCount64();

            // Auto-detect player: first character we see becomes our player
            // (the player's characters are always loaded first)
            if (!player) {
                player = num;
                // Read faction name from this character
                structs::Faction* fac = (structs::Faction*)safeReadPtr(ch, 0x10);
                if (fac) {
                    const char* facName = fac->getName();
                    if (facName && utils::isValidName(facName)) {
                        playerFactionName = facName;
                        std::cout << utils::ts() << "Player detected: " << nameStr
                                  << " (faction: " << playerFactionName << ")" << std::endl;
                    }
                }
            }
        } else {
            // Update charsByName mapping (pointer may have changed)
            std::string& nameRef = chars[(structs::AnimationClassHuman*)ch].first;
            charsByName[nameRef] = num;
            charLastSeen[nameRef] = GetTickCount64();
        }
        chars[(structs::AnimationClassHuman*)ch].second = GetTickCount64();
    }
    void onBuildingUpdate(structs::Building* num) {
        if (!num) return;
        if (builds.find(num) == builds.end()) {
            const char* chosenName = num->getName();
            if (!chosenName || !utils::isValidName(chosenName)) return;
            builds[num] = std::make_pair(std::string(chosenName), 0);
        }
        builds[num].second = GetTickCount64();
    }
    int squadSpawnBypassAmm = 0;
    structs::activePlatoon* bypassedPlatoon = 0;
    int checkRestoration;
    void bypassSquadSpawningCheck(structs::activePlatoon* actvPlatoon) {
        if (squadSpawnBypassAmm > 0
            && actvPlatoon->skipSpawningCheck3 == 0 //check3 memory needs to be 0, usually its a pointer?
            && actvPlatoon->skipSpawningCheck1 == 1) { // use the platoon that would be skipped naturally
            bypassedPlatoon = actvPlatoon;
            actvPlatoon->skipSpawningCheck1 = 0;//if 1 then skip spawning
            checkRestoration = actvPlatoon->skipSpawningCheck2;
            actvPlatoon->skipSpawningCheck2 = 0;//if greater than 0 also cancel
            squadSpawnBypassAmm--;//i hope those steps always spawn char and not 99% of the time
        }
    }
    std::queue<customStructs::Char> charSpawningQueue;
    char returns[sizeof(customStructs::Char)];// Char will live here
    void* __fastcall spawnSquadInjection(
        void* garbage,        //rcx
        structs::Faction* faction,        //rdx
        structs::Vector3   position,       //r8
        void* townOrNest,     //r9
        long long          stackOffset1,
        long long          stackOffset2,//  -1 cause function called
        long long          magic,          //[rsp+20]  999
        void* homeBuilding,   //[rsp+28]
        structs::GameData* squadTemplate,  //[rsp+30]
        structs::activePlatoon* squad     //[rsp+38]
    ) {
        //garbage = *(reinterpret_cast<void**>(moduleBase + offsets::squadSpawningHand));//+offsets::squadSpawningHand
        customStructs::Char* returnDude = reinterpret_cast<customStructs::Char*>(returns);
        returnDude->moduleBase = moduleBase + offsets::squadSpawningHand;
        if (bypassedPlatoon == 0 || bypassedPlatoon != squad){ // don't interrupt natural spawning
            returnDude->data = 0;
            return &returns;
        };
        bypassedPlatoon = 0;
        squad->skipSpawningCheck1 = 1;//restore original state
        squad->skipSpawningCheck2 = checkRestoration;//restore original state
        customStructs::Char dude = charSpawningQueue.front();charSpawningQueue.pop();
        returnDude->data = dude.data;
        returnDude->pos = dude.pos;
        return &returns;
    }
    /*void* spawnSquadInjection() {

        return &returns;
    }*/
    void setupHooks() {
        utils::createHook(
            moduleBase + offsets::charUpdateHook,
            { 0x48, 0x8B, 0x8B, 0x20, 0x03, 0x00, 0x00,   //mov rcx,[rbx+00000320]
              0x40, 0x88, 0xB3, 0x7C, 0x03, 0x00, 0x00 }, //mov [rbx+0000037C],sil
            {},
            &onCharUpdate,
            {}
        );
        // Only hook the 15 bytes BEFORE the virtual call.
        // The call [rax+D8] must stay at the original location so exception
        // unwinding works (our trampoline has no .pdata unwind info).
        utils::createHook(
            moduleBase + offsets::buildingUpdateHook,
            { 0x48, 0x8B, 0x43, 0x60,              //mov rax,[rbx+60]
             0x4C, 0x8B, 0x24, 0x28,               //mov r12,[rax+rbp]
             0x49, 0x8B, 0xCC,                     //mov rcx,r12
             0x49, 0x8B, 0x04, 0x24 },             //mov rax,[r12]  (15 bytes)
            {},
            &onBuildingUpdate,
            {}
        );
        // Only install squad hooks if the full pipeline is available
        // (both bypass AND funcCall needed). The bypass hook replaces a function
        // prologue which breaks .pdata exception unwind info.
        if (offsets::spawnSquadBypass != 0 && offsets::spawnSquadFuncCall != 0) {
            utils::createHook(
                moduleBase + offsets::spawnSquadBypass,
                {
                    0x48, 0x8D, 0xAC, 0x24, 0x30, 0xFF, 0xFF, 0xFF, //- lea rbp,[rsp - 000000D0]
                    0x48, 0x81, 0xEC, 0xD0, 0x01, 0x00, 0x00        //- sub rsp,000001D0
                },
                {0x52},//push rdx
                &bypassSquadSpawningCheck,
                {0x5A}//pop rdx
            );
        } else {
            std::cout << utils::ts() << "SKIP: spawnSquadBypass hook (squad spawning not available)\n";
        }
        if (offsets::spawnSquadFuncCall != 0) {
            utils::createHook(
                moduleBase + offsets::spawnSquadFuncCall,
                {
                    //  0x4C, 0x8B, 0x4E, 0x30                         // mov r9,[rsi+30]
                    //, 0x4C, 0x8D, 0x45, 0xA0                        // lea r8,[rbp-60]
                      0x90, 0x90, 0x90, 0x90
                  , 0x90, 0x90, 0x90, 0x90
                  ,0x90,0x90,0x90,0x90,0x90,0x90,0x90
                //, 0x48, 0x8B, 0x0D, 0x49, 0x3A, 0xC3, 0x01     //- mov rcx,[kenshi.exe+21334E0]
                },
                { 0x52,0x41,0x52, //push rdx  push r10

                },
                & spawnSquadInjection, // TODO write this in assembler
                //& utils::nop,
                {
                    0x41,0x5A, 0x5A // pop r10, pop rdx
                    ,0x48 ,0x8B ,0x48 ,0x18 //- mov rcx,[rax+38]
                    , 0x48 , 0x8B , 0x09 //- mov rcx,[rcx]

                    , 0x48, 0x83, 0xF8, 0x00        // cmp rax,00
                    , 0x0F, 0x84, 0x15, 0x00, 0x00, 0x00  // je 204E001F
                    , 0x4C, 0x8B, 0x08              // mov r9,[rax]
                    , 0x4C, 0x89, 0x4C, 0x24, 0x30     // mov[rsp + 30],r9
                    , 0x4C, 0x8D, 0x40, 0x08        // lea r8,[rax + 08]
                    , 0x4C, 0x8B, 0x4E, 0x30        // mov r9,[rsi + 30]
                    , 0xE9, 0x08, 0x00, 0x00, 0x00     // jmp 204E0027
                    , 0x4C, 0x8B, 0x4E, 0x30        // mov r9,[rsi + 30]
                    , 0x4C, 0x8D, 0x45, 0xA0        // lea r8,[rbp - 60]

                }
            );
        } else {
            std::cout << utils::ts() << "SKIP: spawnSquadFuncCall hook (offset not resolved)\n";
        }
    }
    std::map<std::string,structs::GameData*> DB;
    void scanHeap() {
        long long startTime;
        std::vector<uintptr_t> result;

        crashlog::phase("heap_scan: scanning for GameDataManagerMain");
        std::cout << utils::ts() << "  GameDataManagerMain target: 0x" << std::hex
                  << (moduleBase + offsets::GameDataManagerMain) << std::dec << std::endl;

        int scanPass = 0;
        while (result.size() < 54949) {
            Sleep(500);
            startTime = GetTickCount64();
            crashlog::phase("heap_scan: scanMemoryForValue pass");
            result = std::move(utils::scanMemoryForValue(gameState::moduleBase + offsets::GameDataManagerMain));
            scanPass++;
            if (scanPass <= 5 || scanPass % 10 == 0) {
                std::cout << utils::ts() << "  Scan pass " << scanPass << ": " << result.size() << " results" << std::endl;
            }
        }
        std::cout << utils::ts() << "  Scan complete: " << result.size() << " results in " << scanPass << " passes." << std::endl;

        crashlog::phase("heap_scan: building DB");
        gameLoaded = true;
        for (uintptr_t i : result) {
            structs::GameData* data = reinterpret_cast<structs::GameData*>(i-0x10);
            auto name = data->getName();
            if(utils::isValidName(name))DB[name] = data;
        }
        std::cout << utils::ts() << "HeapScan: found " << DB.size() << " entries in " << ((GetTickCount64() - startTime) / 1000.) << "s.\n";
    }
}