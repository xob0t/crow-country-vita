using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

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
// The same Awake sets vSyncCount 1 (a frame every vertical blank, at most 60 fps). The PS Vita
// draws its title at about 40 fps that way, frames of 16.7 and 33.3 ms in turn, and gameplay at
// 15-25: there it takes vSyncCount 2, an even 30 fps where the frame fits in 33 ms and the same
// rate as before where it doesn't (a 50 ms frame waits for the third blank either way).
//   -crow-vsync <n>       vSyncCount after the game's (1 is the game's; Vita default 2)
//   -crow-camera 624|550|416  with half, the camera's own picture (624x351, 550x351 or 416x234; the
//                         PS Vita's default is 550, other players' 624)
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
                    string[] args = ProbeFiles.Args;
                    int index = Array.IndexOf(args, "-crow-post");
                    string value = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
                    half = value != null ? value == "half" : Application.platform == RuntimePlatform.PSP2;
                }
                return half.Value;
            }
        }

        static int? vsync;

        public static int VSync
        {
            get
            {
                if (vsync == null)
                {
                    string[] args = ProbeFiles.Args;
                    int index = Array.IndexOf(args, "-crow-vsync");
                    int value;
                    vsync = index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out value) ? value
                        : Application.platform == RuntimePlatform.PSP2 ? 2 : -1;
                }
                return vsync.Value;
            }
        }

        static int? cameraWidth;

        // -crow-camera <width>: the camera's own picture: 624 (624x351, the game's Switch size), 550
        // (550x351 at the same 16:9 view: the chain's first pass reads 550 columns, point-sampled, so
        // it takes one in about 1.13 of a 624-wide picture anyway) or 416 (416x234, the size the last
        // pass draws; it loses detail: the last pass reads 351 rows, bilinear, and thin lines break up).
        // The PS Vita takes 550: Roadside at the GPU's 222 MHz 53.9 -> 51 ms a frame, its screenshots
        // the same as 624's (416: 43 ms, but edges stair-step).
        public static int CameraWidth
        {
            get
            {
                if (cameraWidth == null)
                {
                    string[] args = ProbeFiles.Args;
                    int index = Array.IndexOf(args, "-crow-camera");
                    int value;
                    cameraWidth = index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out value) && Height(value) > 0 ? value
                        : Application.platform == RuntimePlatform.PSP2 ? 550 : 624;
                }
                return cameraWidth.Value;
            }
        }

        static readonly System.Collections.Generic.List<Component> effects = new System.Collections.Generic.List<Component>();

        // -crow-ab and -crow-cycle's camera=<width>: every half-size effect's camera picture at that size.
        internal static void SetCameraWidth(int width)
        {
            if (Height(width) == 0) return;
            cameraWidth = width;
            effects.RemoveAll(e => e == null);
            foreach (Component effect in effects) CameraTarget(effect, effect.GetType());
            Debug.Log("CROW_CAMERA picture " + width + " wide on " + effects.Count + " camera effects");
        }

        static void CameraTarget(Component effect, Type type)
        {
            FieldInfo field = type.GetField("camRenderTexture2", Fields);
            var current = field == null ? null : field.GetValue(effect) as RenderTexture;
            int width = CameraWidth;
            if (current == null || current.width == width) return;
            Replace(effect, type, "camRenderTexture2", new RenderTexture(width, Height(width), 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB));
            // The camera's aspect follows its target; 550x351 keeps the 624x351 view.
            var camera = effect.GetComponent<Camera>();
            if (camera == null) return;
            if (width * 351 != Height(width) * 624) camera.aspect = 624f / 351f;
            else camera.ResetAspect();
        }

        static int Height(int width)
        {
            return width == 624 ? 351 : width == 550 ? 351 : width == 416 ? 234 : 0;
        }

        // Called at the end of CrowCountryCamEffect.Awake (rewriter Patches.cs, PatchCamEffect).
        public static void AfterAwake(Component effect)
        {
            if (Application.isPlaying && VSync >= 0) QualitySettings.vSyncCount = VSync;
            if (!Half) return;
            Type type = effect.GetType();
            FieldInfo size = type.GetField("camTargetSize", Fields);
            // CamTargetSize.S1248x702 = 0, S624x351 = 1.
            if (Convert.ToInt32(size.GetValue(effect)) == 0) size.SetValue(effect, Enum.ToObject(size.FieldType, 1));
            if (!hooked)
            {
                hooked = true;
                SceneManager.sceneUnloaded += scene => ForgetDestroyed();
            }
            effects.Add(effect);
            CameraTarget(effect, type);
            Replace(effect, type, "texB1248x702", new RenderTexture(624, 351, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB));
            RenderTexture blur = Replace(effect, type, "texC1248x702", new RenderTexture(624, 351, 0));
            foreach (string name in new[] { "crtPostProMaterial", "crtPostProAlphaMaterial" })
            {
                var material = type.GetField(name, Fields).GetValue(effect) as Material;
                if (material != null) material.SetTexture("_BlurTex", blur);
            }
        }

        // The effects list held every camera effect ever woken, so the effects of unloaded scenes kept
        // their six render textures (about 7 MB each set, the unused 1248x702 camera target included):
        // a static's references keep them through Resources.UnloadUnusedAssets. One set more per scene
        // with a camera of its own (Logos, Title, Driving Intro, Roadside), about 21 MB by Station Square,
        // where the user's game ran out of main memory. An effect destroyed with its scene now has its
        // render textures destroyed and leaves the list.
        static readonly string[] Textures = { "camRenderTexture1", "camRenderTexture2", "texA550x400", "texB1248x702", "texC1248x702", "texD416x234" };
        static bool hooked;

        static void ForgetDestroyed()
        {
            foreach (Component effect in effects)
            {
                if (effect != null) continue;
                foreach (string name in Textures)
                {
                    FieldInfo field = effect.GetType().GetField(name, Fields);
                    var texture = field == null ? null : field.GetValue(effect) as RenderTexture;
                    if (texture == null) continue;
                    texture.Release();
                    UnityEngine.Object.Destroy(texture);
                    field.SetValue(effect, null);
                }
            }
            effects.RemoveAll(e => e == null);
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
