#include "pch.h"
#include "gameState.h"
#include "offsets.h"
#include "json.hpp"
#include "utils.h"
#include <string>
#include <map>

using json = nlohmann::json;

namespace gameState {

    // SEH-safe string reader: returns empty string on fault
    __declspec(nothrow) static const char* safeGetName(void* obj, size_t nameOffset, size_t nameLenOffset) {
        __try {
            int nameLen = *(int*)((char*)obj + nameLenOffset);
            if (nameLen > 15)
                return *((const char**)((char*)obj + nameOffset));
            return (const char*)((char*)obj + nameOffset);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            return "";
        }
    }

    // SEH-safe position reader
    __declspec(nothrow) static bool safeGetPosition(structs::AnimationClassHuman* anim, float& ox, float& oy, float& oz) {
        __try {
            if (!anim || !anim->movement || !anim->movement->position) return false;
            ox = anim->movement->position->x;
            oy = anim->movement->position->y;
            oz = anim->movement->position->z;
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            return false;
        }
    }

    // ---------- SEH-safe map snapshot helpers ----------
    // These are plain-C style functions (no C++ objects needing unwinding)
    // that walk the live maps inside __try/__except. If the game thread
    // corrupts the tree mid-walk, SEH catches the fault and we return
    // whatever we collected so far.

    struct CharEntry {
        const char* name;  // points into live map (read-only, brief use)
        const char* faction; // from charFactions map (null if unknown)
        structs::AnimationClassHuman* anim;
        long long lastSeen;
    };

    static const int MAX_CHAR_ENTRIES = 512;
    static const int MAX_BUILD_ENTRIES = 256;

    // Walk charsByName + charLastSeen + charFactions under SEH, collect into flat array.
    // If filterFaction is non-null, only include characters matching that faction.
    __declspec(nothrow) static int safeCollectChars(
        std::map<std::string, structs::AnimationClassHuman*>* mapPtr,
        std::map<std::string, long long>* lastSeenPtr,
        std::map<std::string, std::string>* factionPtr,
        const char* filterFaction, int filterLen,
        CharEntry* out, int maxOut)
    {
        int count = 0;
        __try {
            for (auto it = mapPtr->begin(); it != mapPtr->end() && count < maxOut; ++it) {
                // Filter by faction if requested
                if (filterFaction && filterLen > 0) {
                    auto facIt = factionPtr->find(it->first);
                    if (facIt == factionPtr->end()) continue;
                    if (facIt->second.size() != (size_t)filterLen ||
                        memcmp(facIt->second.c_str(), filterFaction, filterLen) != 0)
                        continue;
                }

                CharEntry e;
                e.name = it->first.c_str();
                e.faction = nullptr;
                e.anim = it->second;
                e.lastSeen = 0;

                auto facIt2 = factionPtr->find(it->first);
                if (facIt2 != factionPtr->end())
                    e.faction = facIt2->second.c_str();

                auto ts = lastSeenPtr->find(it->first);
                if (ts != lastSeenPtr->end())
                    e.lastSeen = ts->second;
                out[count++] = e;
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            // Tree walk faulted — return what we have so far
        }
        return count;
    }

    struct BuildEntry {
        const char* name;
        structs::Building* bld;
        long long lastSeen;
    };

    // Walk builds under SEH, collect into flat array.
    __declspec(nothrow) static int safeCollectBuilds(
        std::map<structs::Building*, std::pair<std::string, long long>>* mapPtr,
        BuildEntry* out, int maxOut)
    {
        int count = 0;
        __try {
            for (auto it = mapPtr->begin(); it != mapPtr->end() && count < maxOut; ++it) {
                BuildEntry e;
                e.name = it->second.first.c_str();
                e.bld = it->first;
                e.lastSeen = it->second.second;
                out[count++] = e;
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            // Tree walk faulted — return what we have so far
        }
        return count;
    }

    // ---------- Legacy getters (kept for backward compat) ----------

    std::string getSpeed() {
        if (!gameWorld) return std::to_string(0.0f);
        if (gameWorld->paused) return std::to_string(0.0f);
        return std::to_string(gameWorld->gameSpeed);
    }

    float getSpeedFloat() {
        if (!gameWorld) return 0.0f;
        if (gameWorld->paused) return 0.0f;
        return gameWorld->gameSpeed;
    }

    std::string getFaction() {
        if (offsets::factionString == 0) return "!unknown";
        char* factionStringPtr = reinterpret_cast<char*>(moduleBase + offsets::factionString);
        return std::string(factionStringPtr, 17);
    }
    std::string getOwnCharName() {
        static std::map<std::string, std::string> factionToChar = {
        { "204-gamedata.base", "!fail" },
        { "10-multiplayr.mod", "Player 1" },
        { "12-multiplayr.mod", "Player 2" }
        };
        std::string faction = getFaction();
        auto it = factionToChar.find(faction);
        return (it != factionToChar.end()) ? it->second : "!fail";
    }
    std::string getOtherCharName() {
        static std::map<std::string, std::string> factionToChar = {
        { "204-gamedata.base", "!fail" },
        { "10-multiplayr.mod", "Player 2" },
        { "12-multiplayr.mod", "Player 1" }
        };
        std::string faction = getFaction();
        auto it = factionToChar.find(faction);
        return (it != factionToChar.end()) ? it->second : "!fail";
    }
    std::string getPlayer() {
        if (player == 0)return "0,0,0";
        return player->movement->position->toString();
    }
    std::string getPlayer1() {
        if (!otherplayers || !player)return "-5139.11,158.019,345.631";
        if ("Player 1" == getOtherCharName()) {
            return otherplayers->movement->position->toString();
        }
        else {
            return player->movement->position->toString();
        }
    }
    std::string getPlayer2() {
        if (!otherplayers || !player)return "-5139.11,158.019,345.631";
        if ("Player 2" == getOtherCharName()) {
            return otherplayers->movement->position->toString();
        }
        else {
            return player->movement->position->toString();
        }
    }

    // ---------- New JSON entity collection ----------

    std::string getPlayerFaction() {
        return playerFactionName;
    }

    // Get player's squad characters as JSON array
    // Filters by player faction using charFactions map (populated on game thread)
    json getSquadJson() {
        json arr = json::array();
        if (!player) return arr;

        // Filter by player faction if known
        std::string ourFaction = playerFactionName;
        const char* filterFac = ourFaction.empty() ? nullptr : ourFaction.c_str();
        int filterLen = (int)ourFaction.size();

        CharEntry entries[MAX_CHAR_ENTRIES];
        int count = safeCollectChars(&charsByName, &charLastSeen, &charFactions,
                                     filterFac, filterLen, entries, MAX_CHAR_ENTRIES);

        long long now = GetTickCount64();
        for (int i = 0; i < count; ++i) {
            // Skip stale characters (not seen in 10s)
            if (entries[i].lastSeen > 0 && now - entries[i].lastSeen > 10000) continue;

            float x, y, z;
            if (!safeGetPosition(entries[i].anim, x, y, z)) continue;
            if (x == 0.0f && y == 0.0f && z == 0.0f) continue;

            json ch;
            ch["n"] = std::string(entries[i].name);
            ch["x"] = x;
            ch["y"] = y;
            ch["z"] = z;
            arr.push_back(ch);
        }
        return arr;
    }

    // NPC collection no longer needed — all chars go through getSquadJson
    json getNpcJson() {
        return json::array();
    }

    // Get buildings as JSON array
    json getBuildingJson() {
        json arr = json::array();

        // Collect raw entries from live map under SEH protection
        BuildEntry entries[MAX_BUILD_ENTRIES];
        int count = safeCollectBuilds(&builds, entries, MAX_BUILD_ENTRIES);

        long long now = GetTickCount64();
        for (int i = 0; i < count; ++i) {
            if (now - entries[i].lastSeen > 30000) continue;

            structs::Building* bld = entries[i].bld;

            json b;
            b["n"] = std::string(entries[i].name);
            b["x"] = bld->x;
            b["y"] = bld->y;
            b["z"] = bld->z;
            b["cond"] = bld->condition;
            arr.push_back(b);
        }
        return arr;
    }
}
