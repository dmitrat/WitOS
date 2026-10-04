#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>

/* Loads a bounds fixture of the WitOS dynamic TLS support, touches its thread_local objects on the loading thread and
 * prints how many the thread holds; a broken bound ends the process with __fastfail before the line. */
int main(int argc, char **argv)
{
    if (argc != 2) {
        return 1;
    }
    HMODULE library = LoadLibraryExA(argv[1], 0, LOAD_WITH_ALTERED_SEARCH_PATH);
    FARPROC raw = library ? GetProcAddress(library, "Touch") : 0;
    int (*touch)(void) = 0;
    memcpy(&touch, &raw, sizeof(touch));
    if (!touch) {
        return 2;
    }
    const int live = touch();
    if (!FreeLibrary(library)) {
        return 3;
    }
    printf("LIVE: %d\n", live);
    return 0;
}
