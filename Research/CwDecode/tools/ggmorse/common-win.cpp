// The example helpers, minus getBinaryPath - which is the only thing that wanted dlfcn.h, a
// Unix-only header, and which ggmorse-from-file never calls.
#include "ggmorse-common.h"

#include <cstring>
#include <fstream>
#include <iterator>

std::map<std::string, std::string> parseCmdArguments(int argc, char ** argv) {
    std::map<std::string, std::string> res;
    for (int i = 1; i < argc; ++i) {
        if (argv[i][0] == '-' && strlen(argv[i]) > 1) {
            res[std::string(1, argv[i][1])] = strlen(argv[i]) > 2 ? argv[i] + 2 : "";
        }
    }
    return res;
}

std::vector<char> readFile(const char* filename) {
    std::ifstream file(filename, std::ios::binary);
    if (!file.is_open() || !file.good()) return {};
    file.unsetf(std::ios::skipws);
    file.seekg(0, std::ios::end);
    std::streampos fileSize = file.tellg();
    file.seekg(0, std::ios::beg);
    std::vector<char> vec;
    vec.reserve(fileSize);
    vec.insert(vec.begin(), std::istream_iterator<char>(file), std::istream_iterator<char>());
    return vec;
}

std::string getBinaryPath() { return ""; }
