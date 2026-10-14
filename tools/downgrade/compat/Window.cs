using System;
using UnityEngine;

// The game's fullscreen and resolution calls (ResolutionManager, PMFullscreen, PlayMaker's
// SetScreenResolution), redirected here by the rewriter (Redirect.cs, ScreenCalls). They do what
// they did, except with -crow-windowed (run_player.py passes it): then the window stays a window,
// so a test run never takes over the PC's screen, whatever the game's saved display setting says.
// On the PS Vita they do nothing: its screen is 960x544, and the game's settings (the Graphics page's
// Screen entry, the Master Control Code's fullscreen FSM, PMFullscreen's resolution pick 6 frames
// after start) are a PC's. The console mode hides the Screen entry there (see Settings).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class Window
    {
        static bool? windowed;

        static bool Windowed
        {
            get
            {
                if (windowed == null) windowed = Array.IndexOf(ProbeFiles.Args, "-crow-windowed") >= 0;
                return windowed.Value;
            }
        }

        static bool Fixed { get { return Application.platform == RuntimePlatform.PSP2; } }

        public static void set_fullScreen(bool value)
        {
            if (Fixed) return;
            Screen.fullScreen = value && !Windowed;
        }

        public static void set_fullScreenMode(FullScreenMode value)
        {
            if (Fixed) return;
            Screen.fullScreenMode = Windowed ? FullScreenMode.Windowed : value;
        }

        public static void SetResolution(int width, int height, bool fullscreen)
        {
            if (Fixed) return;
            if (Windowed) Screen.SetResolution(960, 544, false);
            else Screen.SetResolution(width, height, fullscreen);
        }

        public static void SetResolution(int width, int height, FullScreenMode mode)
        {
            if (Fixed) return;
            if (Windowed) Screen.SetResolution(960, 544, FullScreenMode.Windowed);
            else Screen.SetResolution(width, height, mode);
        }
    }
}
