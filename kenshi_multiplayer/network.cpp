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
#include "json.hpp"

using json = nlohmann::json;

namespace network {
    std::atomic<bool> running(true);
    int playerId = -1;
    bool isHost = false;
    std::string steamName = "Player";
    std::string steamId = "";

    bool initializeWinsock() {
        WSADATA wsaData;
        return WSAStartup(MAKEWORD(2, 2), &wsaData) == 0;
    }

    SOCKET connectToServer(const std::string& ip, int port) {
        SOCKET client_fd = socket(AF_INET, SOCK_STREAM, 0);
        if (client_fd == INVALID_SOCKET) {
            std::cerr << "Socket creation failed!\n";
            return INVALID_SOCKET;
        }

        sockaddr_in server_addr{};
        server_addr.sin_family = AF_INET;
        server_addr.sin_port = htons(port);
        inet_pton(AF_INET, ip.c_str(), &server_addr.sin_addr);

        if (connect(client_fd, (struct sockaddr*)&server_addr, sizeof(server_addr)) == SOCKET_ERROR) {
            std::cerr << "Connection failed!\n";
            closesocket(client_fd);
            return INVALID_SOCKET;
        }

        return client_fd;
    }

    // Read one newline-delimited JSON message from accumulating buffer
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
            if (n <= 0) return false;
            tmp[n] = '\0';
            buffer.append(tmp, n);
        }
    }

    void sendLine(SOCKET client_fd, const std::string& jsonStr) {
        std::string msg = jsonStr + "\n";
        int total = 0;
        int len = static_cast<int>(msg.size());
        while (total < len) {
            int sent = send(client_fd, msg.c_str() + total, len - total, 0);
            if (sent == SOCKET_ERROR) {
                std::cerr << "Failed to send message!\n";
                return;
            }
            total += sent;
        }
    }

    void receiveMessages(SOCKET client_fd) {
        std::string buffer;
        std::string line;

        // Step 1: Send handshake
        {
            json hello;
            hello["t"] = "hello";
            hello["steamName"] = steamName;
            hello["steamId"] = steamId;
            hello["v"] = "0.3";
            sendLine(client_fd, hello.dump());
            std::cout << "Handshake sent." << std::endl;
        }

        // Step 2: Read welcome
        {
            if (!readLine(client_fd, buffer, line)) {
                std::cerr << "Server disconnected during handshake!\n";
                running = false;
                return;
            }
            try {
                json welcome = json::parse(line);
                if (welcome.value("t", "") != "welcome") {
                    std::cerr << "Expected welcome, got: " << line << "\n";
                    running = false;
                    return;
                }
                playerId = welcome.value("id", -1);
                isHost = welcome.value("isHost", false);
                std::cout << "Welcome! Player ID: " << playerId
                          << (isHost ? " [HOST]" : " [GUEST]") << std::endl;
            }
            catch (const std::exception& e) {
                std::cerr << "Failed to parse welcome: " << e.what() << "\n";
                running = false;
                return;
            }
        }

        // Step 3: Main loop
        while (running) {
            // 3a. Receive world update from server
            if (!readLine(client_fd, buffer, line)) {
                std::cerr << "Server disconnected!\n";
                running = false;
                break;
            }

            try {
                json wu = json::parse(line);
                std::string type = wu.value("t", "");
                if (type == "wu") {
                    gameState::applyWorldUpdate(wu);
                }
            }
            catch (const std::exception& e) {
                std::cerr << "ERROR parsing world update: " << e.what() << "\n";
                std::cerr << "  Raw: " << line.substr(0, 200) << "\n";
            }

            // 3b. Send player state (our squad)
            try {
                json ps;
                ps["t"] = "ps";
                ps["speed"] = gameState::getSpeedFloat();
                ps["squad"] = gameState::getSquadJson();
                sendLine(client_fd, ps.dump());
            }
            catch (const std::exception& e) {
                std::cerr << "ERROR building player state: " << e.what() << "\n";
            }

            // 3c. If host, also send world state (NPCs + buildings)
            if (isHost) {
                try {
                    json ws;
                    ws["t"] = "ws";
                    ws["npcs"] = gameState::getNpcJson();
                    ws["buildings"] = gameState::getBuildingJson();
                    sendLine(client_fd, ws.dump());
                }
                catch (const std::exception& e) {
                    std::cerr << "ERROR building world state: " << e.what() << "\n";
                }
            }
        }
    }

    void cleanup(SOCKET client_fd) {
        closesocket(client_fd);
        WSACleanup();
    }
}
