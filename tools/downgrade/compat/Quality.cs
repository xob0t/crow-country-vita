using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.SceneManagement;

// Lighting and shadow settings, for trying the PS Vita's cheaper ones on any player. The game uses
// one quality level (4 pixel lights, soft and hard shadows at High resolution, 50 m shadow
// distance); in Station Square that is about 300 base draws, 250-270 additive light draws and one
// shadow-casting directional light a frame (Diag's heartbeat). Each flag overrides one setting, at
// start and after every scene load:
//   -crow-pixel-lights <n>          lights per object drawn per pixel (the others per vertex)
//   -crow-shadows off|hard|all      no shadows, hard shadows only, or the game's soft and hard
//   -crow-shadow-distance <m>       how far from the camera shadows are drawn
//   -crow-shadow-resolution <0-3>   Low, Medium, High, VeryHigh
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class Quality
    {
        static int pixelLights = -1, shadowResolution = -1;
        static float shadowDistance = -1f;
        static string shadows;

        // Called by Diag.Install, the probe's one start hook: Unity lists a build's start hooks in the
        // build itself, so a probe DLL swapped into a built player can't add another.
        public static void Install()
        {
            string[] args = Environment.GetCommandLineArgs();
            pixelLights = (int)Number(args, "-crow-pixel-lights", -1);
            shadowResolution = (int)Number(args, "-crow-shadow-resolution", -1);
            shadowDistance = Number(args, "-crow-shadow-distance", -1);
            int index = Array.IndexOf(args, "-crow-shadows");
            shadows = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            if (pixelLights < 0 && shadowResolution < 0 && shadowDistance < 0 && shadows == null) return;
            Apply();
            SceneManager.sceneLoaded += (scene, mode) => Apply();
        }

        static float Number(string[] args, string flag, float fallback)
        {
            int index = Array.IndexOf(args, flag);
            float value;
            return index >= 0 && index + 1 < args.Length &&
                   float.TryParse(args[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        static void Apply()
        {
            if (pixelLights >= 0) QualitySettings.pixelLightCount = pixelLights;
            if (shadowResolution >= 0) QualitySettings.shadowResolution = (ShadowResolution)shadowResolution;
            if (shadowDistance >= 0) QualitySettings.shadowDistance = shadowDistance;
            if (shadows == "off") QualitySettings.shadows = ShadowQuality.Disable;
            else if (shadows == "hard") QualitySettings.shadows = ShadowQuality.HardOnly;
            else if (shadows == "all") QualitySettings.shadows = ShadowQuality.All;
        }
    }
}
