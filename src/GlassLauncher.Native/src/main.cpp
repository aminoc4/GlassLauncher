#include <windows.h>
#include "app.h"

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);

    gl::LauncherApp app;
    int result = app.Run(instance);

    CoUninitialize();
    return result;
}
