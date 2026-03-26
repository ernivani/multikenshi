#include <string>
namespace commands {
    void commandsLoop();
    void init();
    void dispatch(const std::string& line);
}
