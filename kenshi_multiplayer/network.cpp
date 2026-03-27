#include "pch.h"
#include "gameState.h"
#include <winsock2.h>  // Must be included BEFORE windows.h
#include <ws2tcpip.h>
#include <windows.h>
#include <iostream>
#include <string>
#include <thread>
#include <atomic>

#pragma comment(lib, "ws2_32.lib")

#include "network.h"
#include "offsets.h"
#include "charHijack.h"
#include "json.hpp"
#include "utils.h"

using json = nlohmann::json;

// Enforced speed from server (for guests or /speed override)
// -1 means no enforcement (host controls naturally)
// Global so gameStateSetters.cpp can set it, network.cpp sync loop reads it
float g_enforcedSpeed = -1.0f;

namespace network {
    std::atomic<bool> running(true);
    int playerId = -1;
    bool isHost = false;
    std::string steamName = "Player";
    std::string steamId = "";

    // Connection info for reconnection
    static std::string serverIp;
    static int serverPort = 0;

    bool initializeWinsock() {
        WSADATA wsaData;
        return WSAStartup(MAKEWORD(2, 2), &wsaData) == 0;
    }

    SOCKET connectToServer(const std::string& ip, int port) {
        // Store for reconnection
        serverIp = ip;
        serverPort = port;

        SOCKET client_fd = socket(AF_INET, SOCK_STREAM, 0);
        if (client_fd == INVALID_SOCKET) {
            std::cerr << utils::ts() << "Socket creation failed!\n";
            return INVALID_SOCKET;
        }

        sockaddr_in server_addr{};
        server_addr.sin_family = AF_INET;
        server_addr.sin_port = htons(port);
        inet_pton(AF_INET, ip.c_str(), &server_addr.sin_addr);

        if (connect(client_fd, (struct sockaddr*)&server_addr, sizeof(server_addr)) == SOCKET_ERROR) {
            std::cerr << utils::ts() << "Connection failed!\n";
            closesocket(client_fd);
            return INVALID_SOCKET;
        }

        // Set receive timeout to prevent indefinite blocking (10 seconds)
        DWORD timeout = 10000;
        setsockopt(client_fd, SOL_SOCKET, SO_RCVTIMEO, (const char*)&timeout, sizeof(timeout));

        return client_fd;
    }

    // Read one newline-delimited JSON message from accumulating buffer
    // Returns true on success, false on disconnect/error
    // Handles recv timeout (WSAETIMEDOUT) by retrying if still running
    static bool readLine(SOCKET fd, std::string& buffer, std::string& outLine) {
        while (true) {
            size_t pos = buffer.find('\n');
            if (pos != std::string::npos) {
                outLine = buffer.substr(0, pos);
                buffer.erase(0, pos + 1);
                // Skip empty lines
                if (outLine.empty()) continue;
                return true;
            }
            char tmp[65536];
            int n = recv(fd, tmp, sizeof(tmp) - 1, 0);
            if (n > 0) {
                tmp[n] = '\0';
                buffer.append(tmp, n);
                continue;
            }
            if (n == 0) return false; // graceful disconnect
            // n < 0: check if it's a timeout
            int err = WSAGetLastError();
            if (err == WSAETIMEDOUT) {
                if (!running) return false;
                continue; // retry recv after timeout
            }
            return false; // real error
        }
    }

    void sendLine(SOCKET client_fd, const std::string& jsonStr) {
        std::string msg = jsonStr + "\n";
        int total = 0;
        int len = static_cast<int>(msg.size());
        while (total < len) {
            int sent = send(client_fd, msg.c_str() + total, len - total, 0);
            if (sent == SOCKET_ERROR) {
                std::cerr << utils::ts() << "Failed to send message!\n";
                return;
            }
            total += sent;
        }
    }

    // Perform handshake: send hello, receive welcome
    // Returns true on success
    static bool doHandshake(SOCKET client_fd, std::string& buffer) {
        // Send handshake
        json hello;
        hello["t"] = "hello";
        hello["steamName"] = steamName;
        hello["steamId"] = steamId;
        hello["v"] = "0.5.4";
        sendLine(client_fd, hello.dump());
        std::cout << utils::ts() << "Handshake sent." << std::endl;

        // Read welcome
        std::string line;
        if (!readLine(client_fd, buffer, line)) {
            std::cerr << utils::ts() << "Server disconnected during handshake!\n";
            return false;
        }
        try {
            json welcome = json::parse(line);
            if (welcome.value("t", "") != "welcome") {
                std::cerr << utils::ts() << "Expected welcome, got: " << line << "\n";
                return false;
            }
            playerId = welcome.value("id", -1);
            isHost = welcome.value("isHost", false);
            std::cout << utils::ts() << "Welcome! Player ID: " << playerId
                      << (isHost ? " [HOST]" : " [GUEST]") << std::endl;
            return true;
        }
        catch (const std::exception& e) {
            std::cerr << utils::ts() << "Failed to parse welcome: " << e.what() << "\n";
            return false;
        }
    }

    // Main sync loop: entity sync at ~5Hz, speed enforcement at ~60Hz
    static void syncLoop(SOCKET client_fd, std::string& buffer) {
        std::string line;

        // Wait until gameplay starts before sending entity data.
        // During character creation, game speed is 0 and map walks can interfere.
        // Static: once activated, stays active across reconnections.
        static long long syncStartTime = GetTickCount64();
        static bool fullSyncActive = false;
        long long lastSyncTime = 0;
        const long long SYNC_INTERVAL = 200; // 5Hz for entity sync

        while (running) {
            Sleep(16); // ~60Hz tick for responsive speed enforcement

            // Enforce server speed instantly (guest or override)
            if (::g_enforcedSpeed >= 0.0f && gameState::gameWorld) {
                static auto gamePauseFn = offsets::setPaused != 0
                    ? reinterpret_cast<void(*)(structs::GameWorldClass*, bool)>(
                          gameState::moduleBase + offsets::setPaused)
                    : (void(*)(structs::GameWorldClass*, bool))nullptr;

                gameState::gameWorld->gameSpeed = ::g_enforcedSpeed;

                bool shouldBePaused = (::g_enforcedSpeed == 0.0f);
                if (shouldBePaused && !gameState::gameWorld->paused) {
                    // Server says pause — pause properly
                    if (gamePauseFn)
                        gamePauseFn(gameState::gameWorld, true);
                    else
                        gameState::gameWorld->paused = true;
                }
                else if (!shouldBePaused && gameState::gameWorld->paused) {
                    // Server says run — unpause properly
                    if (gamePauseFn) {
                        gameState::gameWorld->paused = false;
                        gamePauseFn(gameState::gameWorld, true);
                        gamePauseFn(gameState::gameWorld, false);
                    } else {
                        gameState::gameWorld->paused = false;
                    }
                }
            }

            // Process NPC hijacks (check for new characters to claim)
            if (charHijack::hasPendingSpawns())
                charHijack::processHijacks();

            // Only do entity sync every 200ms
            long long now = GetTickCount64();
            if (now - lastSyncTime < SYNC_INTERVAL) continue;
            lastSyncTime = now;

            if (!fullSyncActive) {
                bool timeOk = (now - syncStartTime) > 5000;
                bool gameRunning = gameState::getSpeedFloat() > 0.0f;
                if (timeOk && gameRunning) {
                    fullSyncActive = true;
                    std::cout << utils::ts() << "Full sync active (game running)." << std::endl;
                }
            }

            // Build message — only include entity data after warmup
            try {
                json ps;
                ps["t"] = "ps";
                ps["speed"] = gameState::getSpeedFloat();
                ps["pf"] = gameState::getPlayerFaction();
                if (fullSyncActive) {
                    ps["chars"] = gameState::getSquadJson();
                    ps["buildings"] = gameState::getBuildingJson();
                } else {
                    ps["chars"] = json::array();
                    ps["buildings"] = json::array();
                }
                sendLine(client_fd, ps.dump());
            }
            catch (const std::exception& e) {
                std::cerr << utils::ts() << "ERROR building player state: " << e.what() << "\n";
            }

            // Receive one message from server
            if (!readLine(client_fd, buffer, line)) {
                std::cerr << utils::ts() << "Server disconnected!\n";
                break;
            }

            try {
                json msg = json::parse(line);
                std::string type = msg.value("t", "");

                if (type == "wu") {
                    // Only apply world updates when full sync is active
                    // (avoids writing gameSpeed during character creation)
                    if (fullSyncActive) {
                        gameState::applyWorldUpdate(msg);
                    }
                }
                else if (type == "ping") {
                    json pong;
                    pong["t"] = "pong";
                    sendLine(client_fd, pong.dump());
                }
                else if (type == "hostChange") {
                    isHost = msg.value("isHost", false);
                    std::cout << utils::ts() << "Host status changed: "
                              << (isHost ? "HOST" : "GUEST") << std::endl;
                }
            }
            catch (const std::exception& e) {
                std::cerr << utils::ts() << "ERROR parsing server message: " << e.what() << "\n";
                std::cerr << utils::ts() << "  Raw: " << line.substr(0, 200) << "\n";
            }
        }
    }

    // Attempt to reconnect to the server
    // Returns valid socket on success, INVALID_SOCKET on failure
    static SOCKET tryReconnect() {
        std::cout << utils::ts() << "Attempting reconnection..." << std::endl;
        for (int attempt = 1; attempt <= 3 && running; ++attempt) {
            std::cout << utils::ts() << "Reconnect attempt " << attempt << "/3..." << std::endl;
            Sleep(5000);
            if (!running) break;

            SOCKET fd = socket(AF_INET, SOCK_STREAM, 0);
            if (fd == INVALID_SOCKET) continue;

            sockaddr_in addr{};
            addr.sin_family = AF_INET;
            addr.sin_port = htons(serverPort);
            inet_pton(AF_INET, serverIp.c_str(), &addr.sin_addr);

            if (connect(fd, (struct sockaddr*)&addr, sizeof(addr)) != SOCKET_ERROR) {
                // Set receive timeout on reconnected socket
                DWORD timeout = 10000;
                setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, (const char*)&timeout, sizeof(timeout));
                std::cout << utils::ts() << "Reconnected!" << std::endl;
                return fd;
            }
            closesocket(fd);
        }
        std::cerr << utils::ts() << "Reconnection failed after 3 attempts." << std::endl;
        return INVALID_SOCKET;
    }

    void receiveMessages(SOCKET client_fd) {
        std::string buffer;

        // Step 1: Handshake
        if (!doHandshake(client_fd, buffer)) {
            running = false;
            return;
        }

        // Step 2: Wait for player detection before starting sync
        std::cout << utils::ts() << "Waiting for player detection..." << std::endl;
        for (int i = 0; i < 60 && !gameState::player; ++i)
            Sleep(500);

        if (!gameState::player) {
            std::cerr << utils::ts() << "WARNING: Player not detected after 30s!" << std::endl;
        } else {
            std::cout << utils::ts() << "Player detected, starting sync." << std::endl;
        }

        // Step 3: Main loop with reconnection
        SOCKET currentFd = client_fd;
        while (running) {
            syncLoop(currentFd, buffer);

            if (!running) break;

            // Connection lost — try to reconnect
            closesocket(currentFd);
            currentFd = tryReconnect();
            if (currentFd == INVALID_SOCKET) {
                running = false;

                // Force-pause the game so nothing happens while dialog is shown
                if (gameState::gameWorld) {
                    gameState::gameWorld->gameSpeed = 0.0f;
                    gameState::gameWorld->paused = true;
                    // Use setPaused if available for proper UI freeze
                    if (offsets::setPaused != 0) {
                        auto pauseFn = reinterpret_cast<void(*)(structs::GameWorldClass*, bool)>(
                            gameState::moduleBase + offsets::setPaused);
                        pauseFn(gameState::gameWorld, true);
                    }
                }

                MessageBoxA(NULL,
                    "Lost connection to the multiplayer server.\n"
                    "Reconnection failed after 3 attempts.\n\n"
                    "The game will now close.",
                    "Kenshi Multiplayer — Disconnected",
                    MB_OK | MB_ICONERROR | MB_TOPMOST);
                TerminateProcess(GetCurrentProcess(), 1);
                break;
            }

            // Re-handshake on new connection
            buffer.clear();
            if (!doHandshake(currentFd, buffer)) {
                closesocket(currentFd);
                running = false;
                break;
            }
            std::cout << utils::ts() << "Sync resumed after reconnection." << std::endl;
        }

        closesocket(currentFd);
    }

    void cleanup(SOCKET client_fd) {
        // client_fd is already closed by receiveMessages
        WSACleanup();
    }
}
