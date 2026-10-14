using UnityEngine;

// What Rewired's InputManager.DetectPlatform stores (see PatchRewiredPlatform in rewriter/Patches.cs):
// the game's build says Windows and Mono whatever it runs on. Also which prompts the game starts with. Values are Rewired's enums
// (Rewired.Platforms.Platform, ScriptingBackend); CrowCompat doesn't reference Rewired.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class RewiredPlatform
    {
        const int PSVita = 14, IL2CPP = 2;

        public static int Platform(int built)
        {
            return Application.platform == RuntimePlatform.PSP2 ? PSVita : built;
        }

        public static int ScriptingBackend(int built)
        {
            return Application.platform == RuntimePlatform.PSP2 ? IL2CPP : built;
        }

        // KeyboardOrControllerCheck.isKeyboard's initial value (PatchKeyboardOrController): the game
        // starts with keyboard prompts until a pad button goes down; the PS Vita has only its pad.
        public static bool StartsWithKeyboard(bool built)
        {
            return Application.platform != RuntimePlatform.PSP2 && built;
        }
    }
}
