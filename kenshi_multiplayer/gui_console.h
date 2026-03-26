#pragma once
#include <string>

namespace guiConsole {
    void create();
    void destroy();
    void appendLog(const std::string& text);
    void setStatus(const std::string& text);
    void clearLog();
}
