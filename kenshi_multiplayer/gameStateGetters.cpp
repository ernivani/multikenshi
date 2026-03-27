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

    // SEH-safe faction name reader
    __declspec(nothrow) static const char* safeGetFactionName(structs::AnimationClassHuman* anim) {
        __try {
            structs::CharacterHuman* ch = anim->character;
            if (!ch) return "";
            structs::Faction* fac = ch->faction;
            if (!fac) return "";
            return fac->getName();
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

    // Get our squad characters as JSON array
    // "Our" = characters whose faction matches our player's faction
    // Uses charsByName which has correct AnimationClassHuman* pointers
    json getSquadJson() {
        json arr = json::array();
        if (!player) return arr;

        std::string ourFaction = getPlayerFaction();
        // Fallback: try to read faction live if it wasn't captured at detection
        if (ourFaction.empty()) {
            ourFaction = std::string(safeGetFactionName(player));
            if (!ourFaction.empty()) {
                playerFactionName = ourFaction;
                std::cout << utils::ts() << "Player faction detected (late): " << ourFaction << std::endl;
            }
        }
        if (ourFaction.empty()) return arr;

        long long now = GetTickCount64();
        for (auto it = charsByName.begin(); it != charsByName.end(); ++it) {
            // Skip stale characters (not seen in 10s)
            auto ts = charLastSeen.find(it->first);
            if (ts != charLastSeen.end() && now - ts->second > 10000) continue;

            structs::AnimationClassHuman* anim = it->second;
            std::string facName(safeGetFactionName(anim));
            if (facName != ourFaction) continue;

            float x, y, z;
            if (!safeGetPosition(anim, x, y, z)) continue;
            if (x == 0.0f && y == 0.0f && z == 0.0f) continue;

            json ch;
            ch["n"] = it->first;
            ch["x"] = x;
            ch["y"] = y;
            ch["z"] = z;
            ch["fn"] = facName;
            arr.push_back(ch);
        }
        return arr;
    }

    // Get NPC characters (not our faction) as JSON array
    // Uses charsByName which has correct AnimationClassHuman* pointers
    json getNpcJson() {
        json arr = json::array();
        if (!player) return arr;

        std::string ourFaction = getPlayerFaction();

        long long now = GetTickCount64();
        for (auto it = charsByName.begin(); it != charsByName.end(); ++it) {
            auto ts = charLastSeen.find(it->first);
            if (ts != charLastSeen.end() && now - ts->second > 10000) continue;

            structs::AnimationClassHuman* anim = it->second;
            std::string facName(safeGetFactionName(anim));
            // Skip our own faction — those go in squad
            if (!ourFaction.empty() && facName == ourFaction) continue;

            float x, y, z;
            if (!safeGetPosition(anim, x, y, z)) continue;
            if (x == 0.0f && y == 0.0f && z == 0.0f) continue;

            json ch;
            ch["n"] = it->first;
            ch["x"] = x;
            ch["y"] = y;
            ch["z"] = z;
            ch["fn"] = facName;
            arr.push_back(ch);
        }
        return arr;
    }

    // Get buildings as JSON array
    json getBuildingJson() {
        json arr = json::array();

        long long now = GetTickCount64();
        for (auto it = builds.begin(); it != builds.end(); ++it) {
            if (now - it->second.second > 30000) continue;

            structs::Building* bld = it->first;

            json b;
            b["n"] = it->second.first;
            b["x"] = bld->x;
            b["y"] = bld->y;
            b["z"] = bld->z;
            b["cond"] = bld->condition;
            arr.push_back(b);
        }
        return arr;
    }
}
