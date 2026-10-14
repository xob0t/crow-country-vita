using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

// The camera's post-processing chain (com.sfbgames.crowcountry.CrowCountryCamEffect), at half size on
// the PS Vita. The game renders the camera at 1248x702, reads it into a 550x400 texture (SFB filter),
// draws the PSX and CRT blur passes at 1248x702 and the last pass into a 416x234 texture, which is
// what the screen shows. The camera's picture is thus reduced to 550x400 before anything else reads
// it, and the 1248x702 passes are read at 416x234; at 624x351 each of them shades a quarter of the
// pixels. The camera takes the game's own 624x351 target (CamTargetSize.S624x351, its Switch size),
// and the two 1248x702 textures are made at 624x351. The blur's offsets are in uv units, so it
// spreads over the same part of the screen.
//   -crow-post full|half  full is the original chain, half the Vita's; Windows players default to
//                         full, the Vita to half.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class CamEffect
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        static bool? half;

        public static bool Half
        {
            get
            {
                if (half == null)
                {
                    string[] args = Environment.GetCommandLineArgs();
                    int index = Array.IndexOf(args, "-crow-post");
                    string value = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
                    half = value != null ? value == "half" : Application.platform == RuntimePlatform.PSP2;
                }
                return half.Value;
            }
        }

        // Called at the end of CrowCountryCamEffect.Awake (rewriter Patches.cs, PatchCamEffect).
        public static void AfterAwake(Component effect)
        {
            if (!Half) return;
            Type type = effect.GetType();
            FieldInfo size = type.GetField("camTargetSize", Fields);
            // CamTargetSize.S1248x702 = 0, S624x351 = 1.
            if (Convert.ToInt32(size.GetValue(effect)) == 0) size.SetValue(effect, Enum.ToObject(size.FieldType, 1));
            Replace(effect, type, "texB1248x702", new RenderTexture(624, 351, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB));
            RenderTexture blur = Replace(effect, type, "texC1248x702", new RenderTexture(624, 351, 0));
            foreach (string name in new[] { "crtPostProMaterial", "crtPostProAlphaMaterial" })
            {
                var material = type.GetField(name, Fields).GetValue(effect) as Material;
                if (material != null) material.SetTexture("_BlurTex", blur);
            }
        }

        static RenderTexture Replace(Component effect, Type type, string name, RenderTexture texture)
        {
            FieldInfo field = type.GetField(name, Fields);
            var old = field.GetValue(effect) as RenderTexture;
            if (old != null)
            {
                texture.filterMode = old.filterMode;
                old.Release();
                UnityEngine.Object.Destroy(old);
            }
            field.SetValue(effect, texture);
            return texture;
        }
    }
}
