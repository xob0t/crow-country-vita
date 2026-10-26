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
// draws its title at about 40 fps that way, frames of 16.7 and 33.3 ms in turn, and gameplay frames
// take more than one blank (most areas fit in 33.4 ms now, a fight 38-39): there it takes
// vSyncCount 2, an even 30 fps where the frame fits in 33 ms and the same rate as before where it
// doesn't (a 50 ms frame waits for the third blank either way).
//   -crow-vsync <n>       vSyncCount after the game's (1 is the game's; Vita default 2)
//   -crow-camera 624|550  with half, the camera's own picture (624x351 or 550x351; the PS Vita's
//                         default is 550, other players' 624)
// The chain itself (CrowCountryCamEffect.OnRenderImage asks RenderImage first, rewriter Patches.cs
// PatchCamEffect): the game's, unless one of these changes it:
//   -crow-post-filters all|none|<names>  the filters drawn: sfb, psx, blur, crt, joined by '+' (psx+crt),
//                         or no<name> for all but one (nocrt); a filter left out is skipped and the
//                         next reads the picture before it; whatever is last goes to the screen
//                         nearest-neighbour, so none shows the camera's own 550x351 picture as it is.
//                         all (every player's default) draws them as the game does.
//   -crow-post-vita on|off  at half size, the chain in three passes of vita_shaders/CrowPost.shader
//                         instead of the game's four (the PS Vita's default; off elsewhere): SFB and
//                         PSX in one pass at 550x400, the blur straight from it (no 624x351 PSX
//                         texture), the CRT pass with its noise from a random texture. With filters left out it draws
//                         those of its passes that still apply (sfb+psx: pass 0 only).
//   -crow-post-bundle <path>  CrowVita/Post from an asset bundle (for a build without it)
// -crow-cycle, -crow-fight-cycle and -crow-ab take postfilters=<value> and postvita=<value>
// (postdiscard=on|off: the Vita chain's targets' DiscardContents before each pass, on by default).
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
                    string value = CrowFlags.Value(ProbeFiles.Args, "-crow-post");
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
                    vsync = CrowFlags.Int(ProbeFiles.Args, "-crow-vsync", Application.platform == RuntimePlatform.PSP2 ? 2 : -1);
                }
                return vsync.Value;
            }
        }

        static int? cameraWidth;

        // -crow-camera <width>: the camera's own picture: 624 (624x351, the game's Switch size) or 550
        // (550x351 at the same 16:9 view: the chain's first pass reads 550 columns, point-sampled, so
        // it takes one in about 1.13 of a 624-wide picture anyway). The PS Vita takes 550: Roadside at
        // the GPU's 222 MHz 53.9 -> 51 ms a frame, its screenshots the same as 624's. (416x234, the size
        // the last pass draws, made 43 ms but stair-stepped the edges: the last pass reads 351 rows.)
        const int GameWidth = 624, Rows = 351;
        public static int CameraWidth
        {
            get
            {
                if (cameraWidth == null)
                {
                    int value = CrowFlags.Int(ProbeFiles.Args, "-crow-camera", 0);
                    cameraWidth = Allowed(value) ? value : Application.platform == RuntimePlatform.PSP2 ? 550 : GameWidth;
                }
                return cameraWidth.Value;
            }
        }

        static readonly System.Collections.Generic.List<Component> effects = new System.Collections.Generic.List<Component>();

        // -crow-ab and -crow-cycle's camera=<width>: every half-size effect's camera picture at that size.
        internal static void SetCameraWidth(int width)
        {
            if (!Allowed(width)) return;
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
            Replace(effect, type, "camRenderTexture2", new RenderTexture(width, Rows, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB));
            // The camera's aspect follows its target; 550x351 keeps the 624x351 view.
            var camera = effect.GetComponent<Camera>();
            if (camera == null) return;
            if (width != GameWidth) camera.aspect = (float)GameWidth / Rows;
            else camera.ResetAspect();
        }

        static bool Allowed(int width)
        {
            return width == GameWidth || width == 550;
        }

        // Called at the end of CrowCountryCamEffect.Awake (rewriter Patches.cs, PatchCamEffect).
        public static void AfterAwake(Component effect)
        {
            if (Application.isPlaying && VSync >= 0) QualitySettings.vSyncCount = VSync;
            if (!hooked)
            {
                hooked = true;
                CrowEvents.Unloaded("CamEffect.sceneUnloaded", scene => ForgetDestroyed());
            }
            if (!Half) return;
            Type type = effect.GetType();
            FieldInfo size = type.GetField("camTargetSize", Fields);
            // CamTargetSize.S1248x702 = 0, S624x351 = 1.
            if (Convert.ToInt32(size.GetValue(effect)) == 0) size.SetValue(effect, Enum.ToObject(size.FieldType, 1));
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
            // RenderImage's chains hold the textures too.
            chains.Clear();
            lastEffect = null;
            lastChain = null;
        }

        // ------------------------------------------------------------------ the chain

        const int Sfb = 1, Psx = 2, Blur = 4, Crt = 8, All = 15;
        static readonly string[] FilterNames = { "sfb", "psx", "blur", "crt" };
        static int filters = -1;
        static string vitaMode;
        static bool discard = true;

        static int Filters
        {
            get
            {
                if (filters < 0) filters = ParseFilters(CrowFlags.Value(ProbeFiles.Args, "-crow-post-filters") ?? "all");
                return filters;
            }
        }

        static string VitaMode
        {
            get
            {
                if (vitaMode == null)
                    vitaMode = CrowFlags.Value(ProbeFiles.Args, "-crow-post-vita", "on", "off");
                return vitaMode;
            }
        }

        static int ParseFilters(string value)
        {
            if (value == "all" || value == "on") return All;
            if (value == "none" || value == "off") return 0;
            if (value.StartsWith("no", StringComparison.Ordinal))
            {
                int one = Array.IndexOf(FilterNames, value.Substring(2));
                if (one >= 0) return All & ~(1 << one);
            }
            int mask = 0;
            foreach (string name in value.Split('+'))
            {
                int index = Array.IndexOf(FilterNames, name);
                if (index >= 0) mask |= 1 << index;
                else Debug.Log("CROW_POST unknown filter " + name);
            }
            return mask;
        }

        static string FiltersText(int mask)
        {
            if (mask == All) return "all";
            if (mask == 0) return "none";
            var names = new System.Collections.Generic.List<string>();
            for (int i = 0; i < FilterNames.Length; i++) if ((mask & (1 << i)) != 0) names.Add(FilterNames[i]);
            return string.Join("+", names.ToArray());
        }

        // -crow-cycle's, -crow-fight-cycle's and -crow-ab's postfilters=, postvita=, postdiscard=.
        internal static bool Change(string key, string value)
        {
            if (key == "postfilters") filters = ParseFilters(value);
            else if (key == "postvita") vitaMode = value;
            else if (key == "postdiscard") discard = value != "off";
            else return false;
            logged = false;
            return true;
        }

        // The textures and materials of one camera effect (found once; AfterAwake replaces some of them
        // before the first frame).
        class Chain
        {
            public Material sfb, psx, blur, crt;
            public RenderTexture a, b, c, d;
            public string notHalf;  // VitaReady's size check, made once: null when the sizes are the half-size chain's
            public bool sized;
        }

        static readonly System.Collections.Generic.Dictionary<int, Chain> chains = new System.Collections.Generic.Dictionary<int, Chain>();
        static Component lastEffect;
        static Chain lastChain;
        static bool logged;

        static Chain ChainOf(Component effect)
        {
            if (ReferenceEquals(effect, lastEffect) && lastChain != null && lastChain.a != null && lastChain.d != null) return lastChain;
            Chain known;
            if (chains.TryGetValue(effect.GetInstanceID(), out known) && known.a != null && known.d != null)
            {
                lastEffect = effect;
                return lastChain = known;
            }
            return FindChain(effect);
        }

        // The chain read from the effect's fields: a method of its own, so its lambda's closure (effect, type) is made
        // only here, not at the start of ChainOf in every frame.
        static Chain FindChain(Component effect)
        {
            Type type = effect.GetType();
            Func<string, object> get = name =>
            {
                FieldInfo field = type.GetField(name, Fields);
                return field == null ? null : field.GetValue(effect);
            };
            var chain = new Chain
            {
                sfb = get("sfbFilter") as Material,
                psx = get("psxPostProcessingMat") as Material,
                blur = get("crtBlurMaterial") as Material,
                crt = get("crtPostProMaterial") as Material,
                a = get("texA550x400") as RenderTexture,
                b = get("texB1248x702") as RenderTexture,
                c = get("texC1248x702") as RenderTexture,
                d = get("texD416x234") as RenderTexture,
            };
            if (chain.sfb == null || chain.psx == null || chain.blur == null || chain.crt == null || chain.a == null || chain.b == null || chain.c == null || chain.d == null)
                return null;
            chains[effect.GetInstanceID()] = chain;
            lastEffect = effect;
            lastChain = chain;
            return chain;
        }

        // Called first in CrowCountryCamEffect.OnRenderImage with the effect's switches (rewriter
        // PatchCamEffect); true when it drew the frame, false to leave it to the game's own chain: its
        // alpha form, a filter the game switched off, or the game's
        // filters and no Vita chain asked for. With the Vita chain on, the filters drawn take its passes
        // where they can: SFB and PSX together (pass 0), the blur after them (pass 1), the CRT pass
        // after anything; SFB or PSX alone, and the blur after PSX alone, are the game's.
        public static bool RenderImage(Component effect, RenderTexture src, RenderTexture dst, bool sfbOn, bool psxOn, bool blurOn, bool crtOn, bool alpha, bool sourcePoint)
        {
            LoopProfile.ImageMark(true);  // -crow-loop-profile's image stage (Quality.cs)
            bool drawn = DrawChain(effect, src, dst, sfbOn, psxOn, blurOn, crtOn, alpha, sourcePoint);
            if (drawn) LoopProfile.ImageMark(false);
            return drawn;
        }

        static bool DrawChain(Component effect, RenderTexture src, RenderTexture dst, bool sfbOn, bool psxOn, bool blurOn, bool crtOn, bool alpha, bool sourcePoint)
        {
            if (alpha || !sfbOn || !psxOn || !blurOn || !crtOn || src == null) return false;
            int mask = Filters;
            bool vita = VitaMode != "off" && mask != 0;
            if (mask == All && !vita) return false;
            Chain chain = ChainOf(effect);
            if (chain == null) return false;
            if (vita && !VitaReady(chain))
            {
                if (mask == All) return false;
                vita = false;
            }
            if (!logged)
            {
                logged = true;
                Debug.Log("CROW_POST filters=" + FiltersText(mask) + (vita ? " vita=" + VitaMode : " game shaders") +
                          " camera=" + src.width + "x" + src.height + (vita && !discard ? " nodiscard" : ""));
            }
            if (sourcePoint) src.filterMode = FilterMode.Point;
            if (vita)
            {
                post.SetTexture(GrainId, chain.sfb.GetTexture(GrainId));
                post.SetTexture(CrtId, chain.sfb.GetTexture(CrtId));
                post.SetMatrix(ColorMatId, chain.crt.GetMatrix(ColorMatId));
            }
            Texture current = src;
            bool psxPicture = false;  // current is pass 0's 550x400 PSX picture
            if (vita && (mask & (Sfb | Psx)) == (Sfb | Psx))
            {
                Draw(src, chain.a, post, 0);
                current = chain.a;
                psxPicture = true;
            }
            else
            {
                if ((mask & Sfb) != 0)
                {
                    Graphics.Blit(current, chain.a, chain.sfb);
                    current = chain.a;
                }
                if ((mask & Psx) != 0)
                {
                    Graphics.Blit(current, chain.b, chain.psx);
                    current = chain.b;
                }
            }
            if ((mask & Blur) != 0)
            {
                if (psxPicture) Draw(current, chain.c, post, 1);
                else Graphics.Blit(current, chain.c, chain.blur);
                current = chain.c;
            }
            if ((mask & Crt) != 0)
            {
                if (vita) Draw(current, chain.d, post, 2);
                else
                {
                    // The CRT pass reads the blur's texture as _BlurTex too.
                    chain.crt.SetTexture(BlurTexId, current);
                    Graphics.Blit(current, chain.d, chain.crt);
                    chain.crt.SetTexture(BlurTexId, chain.c);
                }
                current = chain.d;
            }
            // The last picture to the screen nearest-neighbour, as the game's 416x234 one goes.
            FilterMode mode = current.filterMode;
            if (mode != FilterMode.Point) current.filterMode = FilterMode.Point;
            Graphics.Blit(current, dst);
            if (mode != FilterMode.Point) current.filterMode = mode;
            return true;
        }

        // A Vita pass into a target it covers whole: its old contents need not be loaded first.
        static void Draw(Texture source, RenderTexture target, Material material, int pass)
        {
            if (discard) target.DiscardContents();
            Graphics.Blit(source, target, material, pass);
        }

        static readonly int BlurTexId = Shader.PropertyToID("_BlurTex"), SwitchId = Shader.PropertyToID("_switch");
        static Material post;
        static Texture2D dither, noise;
        static string unavailable;

        // CrowVita/Post, the noise and dither textures; the chain's sizes as the half-size chain makes
        // them and the game's _switch on (the passes are its Switch forms). The sizes are checked once per
        // chain: its textures are made once, by the game's Awake and AfterAwake, and nothing sets a size
        // after (Half is read once too), so a frame makes no engine reads for them.
        static bool VitaReady(Chain chain)
        {
            string reason = null;
            if (post == null && unavailable == null) unavailable = LoadPost();
            if (!chain.sized)
            {
                chain.sized = true;
                if (!Half || chain.a.width != 550 || chain.a.height != 400 || chain.c.width != 624 || chain.c.height != 351 || chain.d.width != 416 || chain.d.height != 234)
                    chain.notHalf = "the chain is not the half-size one (" + chain.a.width + "x" + chain.a.height + ", " + chain.c.width + "x" + chain.c.height + ", " + chain.d.width + "x" + chain.d.height + ")";
            }
            if (unavailable != null) reason = unavailable;
            else if (chain.notHalf != null) reason = chain.notHalf;
            else if (Shader.GetGlobalFloat(SwitchId) < 0.5f) reason = "_switch is off";
            if (reason == null) return true;
            if (!logged)
            {
                logged = true;
                Debug.Log("CROW_POST vita unavailable: " + reason + "; the game's shaders");
            }
            return false;
        }

        static string LoadPost()
        {
            Shader shader = null;
            string bundle = CrowFlags.Value(ProbeFiles.Args, "-crow-post-bundle");
            if (bundle != null)
            {
                AssetBundle loaded = AssetBundle.LoadFromFile(bundle);
                if (loaded != null) shader = loaded.LoadAllAssets<Shader>().FirstOrDefault(s => s.name == "CrowVita/Post");
                Debug.Log("CROW_POST bundle " + bundle + ": " + (loaded == null ? "failed" : shader == null ? "no CrowVita/Post" : "loaded"));
            }
            if (shader == null) shader = Shader.Find("CrowVita/Post");
            if (shader == null) return "CrowVita/Post is not in the build";
            if (!shader.isSupported) return "CrowVita/Post is not supported";
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (!material.HasProperty("_CrowPostV1")) return "CrowVita/Post is an older version";
            // The game's 4x4 dither table is the Bayer matrix: offset 0.00282353 - rank * 0.00117647, the
            // rank at (column, row) as rank / 15 (exact in 8 bits).
            int[] bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
            dither = new Texture2D(4, 4, TextureFormat.RGBA32, false, true)
            {
                filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat, hideFlags = HideFlags.HideAndDontSave
            };
            var cells = new Color32[16];
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                {
                    byte value = (byte)(bayer[row * 4 + column] * 17);
                    cells[row * 4 + column] = new Color32(value, value, value, 255);
                }
            dither.SetPixels32(cells);
            dither.Apply(false, true);
            // Uniform random noise, one value per texel and channel, a new offset whenever the game's time moves.
            const int size = 128;
            noise = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
            {
                filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat, hideFlags = HideFlags.HideAndDontSave
            };
            var random = new System.Random(20241018);
            var texels = new Color32[size * size];
            for (int i = 0; i < texels.Length; i++)
                texels[i] = new Color32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255);
            noise.SetPixels32(texels);
            noise.Apply(false, true);
            material.SetTexture("_CrowDither", dither);
            material.SetTexture("_CrowNoise", noise);
            material.SetVector("_CrowDitherScale", new Vector4(550f / 4f, 400f / 4f, 0f, 0f));
            material.SetVector("_CrowStep", new Vector4(1f / 624f, 1f / 351f, 0f, 0f));
            material.SetVector("_CrowNoiseScale", new Vector4(416f / size, 234f / size, size, size));
            post = material;
            Debug.Log("CROW_POST CrowVita/Post ready");
            return null;
        }

        static readonly int GrainId = Shader.PropertyToID("_GrainTex"), CrtId = Shader.PropertyToID("_CRTTex"), ColorMatId = Shader.PropertyToID("colorMat");

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
