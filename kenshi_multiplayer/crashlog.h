#pragma once

namespace crashlog {
    void init();                    // Set up log file + global exception filter
    void phase(const char* msg);    // Record current phase (written to file immediately)
    const char* getLogPath();       // Get path to the crash log file
}
