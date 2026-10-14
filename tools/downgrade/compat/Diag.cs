using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// The measurement probe: off unless a -crow-* flag is given (on the PS Vita, in ux0:data/crow/args.txt;
// see ProbeFiles). Each flag's line in Help says what it does. Heartbeats go to the player log as
// "HEARTBEAT key=value ...", for tools to read.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public class Diag : MonoBehaviour
    {
        const string Help =
            "-crow-scene <name>       load this scene once the Title has loaded (an area: the scene loads the player)\n" +
            "-crow-heartbeat <s>      every s seconds, log frame times, GC and memory, visible renderers and lights\n" +
            "-crow-quit-after <s>     quit s seconds after the probe starts\n" +
            "-crow-shot <s> <path>    save a screenshot s seconds after the last scene load\n" +
            "-crow-no-vsync           vSyncCount 0 and no frame cap, for unsynchronized frame times\n" +
            "-crow-mute               no sound (AudioListener.volume 0, kept there), for test runs on a PC in use\n" +
            "-crow-input              log Unity's joystick buttons as they go down, and every 5 s the joysticks Unity and Rewired see\n" +
            "-crow-stall-dump <s>     crash the player when a frame has taken s seconds, for a core dump with the main thread's stack\n" +
            "-crow-stall-after <t>    with -crow-stall-dump, only frames that start t seconds or more after startup (skips earlier stalls)\n" +
            "-crow-dump-text <s>      s seconds after the last scene load, log each TextMeshPro text with its font and mesh\n" +
            "-crow-language <name>    keep I2 Localization on this language (English, French, Japanese, Korean, \"Chinese (Simplified)\", ...)";

        static string[] args;
        static string sceneToLoad;
        static float heartbeat, quitAfter = -1f, shotAfter = -1f;
        static string shotPath;
        static bool noVsync, mute, input;
        static float dumpTextAfter = -1f;
        static string textShader, language;
        static PropertyInfo currentLanguage;
        static float lastLanguageCheck = -10f;
        static float stallDump = -1f, stallAfter;
        static volatile float frameStartTime;  // realtimeSinceStartup at frameStartTicks
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static long frameStartTicks;

        float started, lastBeat, lastLoad, worst;
        int frames, collections;
        double frameSum;
        bool shotTaken, sceneRequested, textDumped;
        float lastShaderSwap, lastInputReport = -10f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            Quality.Install();
            Memory.Install();
            Loading.Install();
            Clock.Install();
            args = ProbeFiles.Args;
            if (!args.Any(a => a.StartsWith("-crow-"))) return;
            sceneToLoad = Value("-crow-scene");
            heartbeat = Number("-crow-heartbeat", 0f);
            quitAfter = Number("-crow-quit-after", -1f);
            shotAfter = Number("-crow-shot", -1f);
            int shot = Array.IndexOf(args, "-crow-shot");
            shotPath = shot >= 0 && shot + 2 < args.Length ? args[shot + 2] : null;
            noVsync = args.Contains("-crow-no-vsync");
            mute = args.Contains("-crow-mute");
            input = args.Contains("-crow-input");
            if (mute) AudioListener.volume = 0f;
            dumpTextAfter = Number("-crow-dump-text", -1f);
            language = Value("-crow-language");
            textShader = Value("-crow-text-shader");
            stallDump = Number("-crow-stall-dump", -1f);
            stallAfter = Number("-crow-stall-after", 0f);
            if (stallDump > 0f) new System.Threading.Thread(WatchForStall) { IsBackground = true, Name = "CrowStallWatch" }.Start();
            if (args.Contains("-crow-help")) Debug.Log("CROW_DIAG flags:\n" + Help);
            var probe = new GameObject("CrowDiag");
            DontDestroyOnLoad(probe);
            probe.AddComponent<Diag>();
        }

        // -crow-stall-dump: a frame that never ends in the logs (the PS Vita's 40-60 s frames after a
        // scene load) can't say what it spends its time on, and the console has no sampling profiler.
        // Crashing the player mid-frame leaves a core dump with every thread's stack, the main
        // thread's among them (vita.py crashes --pull --thread main).
        static void WatchForStall()
        {
            while (true)
            {
                System.Threading.Thread.Sleep(250);
                long started = System.Threading.Interlocked.Read(ref frameStartTicks);
                if (started == 0) continue;
                double seconds = (clock.ElapsedTicks - started) / (double)System.Diagnostics.Stopwatch.Frequency;
                if (seconds < stallDump || frameStartTime < stallAfter) continue;
                System.Runtime.InteropServices.Marshal.WriteInt32(new IntPtr(16), 0);  // unmapped: a data abort
                Environment.FailFast("CrowCompat stall dump");
            }
        }

        static string Value(string flag)
        {
            int index = Array.IndexOf(args, flag);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        static float Number(string flag, float fallback)
        {
            string value = Value(flag);
            float number;
            return value != null && float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out number) ? number : fallback;
        }

        void Start()
        {
            started = lastBeat = lastLoad = Time.realtimeSinceStartup;
            collections = GC.CollectionCount(0);
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                lastLoad = Time.realtimeSinceStartup;
                shotTaken = false;
                textDumped = false;
                Debug.Log("CROW_DIAG scene loaded " + scene.name + " (" + mode + ") at " + lastLoad.ToString("F2") + " s");
            };
            Debug.Log("CROW_DIAG started: " + string.Join(" ", args.Where(a => a.StartsWith("-crow-")).ToArray()));
        }

        void Update()
        {
            frameStartTime = Time.realtimeSinceStartup;
            System.Threading.Interlocked.Exchange(ref frameStartTicks, clock.ElapsedTicks);
            if (mute) AudioListener.volume = 0f;  // the game sets it from its sound settings
            if (noVsync)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
            }
            float now = Time.realtimeSinceStartup;
            // Only once the Logos scene's own load of the Title is over (2 s after it), so two scenes are
            // never in flight at once (that overstated memory: Roadside ran out of it that way).
            bool titleUp = SceneManager.GetActiveScene().name == "Title" && now - lastLoad >= 2f;
            if (sceneToLoad != null && !sceneRequested && (titleUp || now - started >= 180f))
            {
                sceneRequested = true;
                Debug.Log("CROW_DIAG loading " + sceneToLoad);
                MaraScene.Request();  // the player's scene, which the title would have loaded (Loading.cs)
                SceneManager.LoadScene(sceneToLoad);
            }
            float delta = Time.unscaledDeltaTime * 1000f;
            frames++;
            frameSum += delta;
            if (delta > worst) worst = delta;
            if (heartbeat > 0f && now - lastBeat >= heartbeat) Beat(now);
            if (input)
            {
                LogInput();
                if (now - lastInputReport >= 5f)
                {
                    lastInputReport = now;
                    ReportInput();
                }
            }
            if (shotAfter >= 0f && !shotTaken && shotPath != null && now - lastLoad >= shotAfter)
            {
                shotTaken = true;
                ScreenCapture.CaptureScreenshot(shotPath);
                Debug.Log("CROW_DIAG screenshot " + shotPath);
            }
            if (dumpTextAfter >= 0f && !textDumped && now - lastLoad >= dumpTextAfter)
            {
                textDumped = true;
                DumpText();
                DumpCanvas();
            }
            if (language != null && now - lastLanguageCheck >= 0.5f)
            {
                lastLanguageCheck = now;
                KeepLanguage();
            }
            if (textShader != null && now - lastShaderSwap >= 1f)
            {
                lastShaderSwap = now;
                SwapTextShader(textShader);
            }
            if (quitAfter >= 0f && now - started >= quitAfter)
            {
                Debug.Log("CROW_DIAG quitting");
                Application.Quit();
            }
        }

        // -crow-dump-text: each TextMeshPro text in the loaded scenes, with its font asset's tables and
        // what TMP generated for it (read by reflection: CrowCompat doesn't reference TMP).
        static void DumpText()
        {
            Type textType = Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro");
            if (textType == null)
            {
                Debug.Log("CROW_TEXT no TMPro.TMP_Text type");
                return;
            }
            var all = Resources.FindObjectsOfTypeAll(textType).Cast<Component>().Where(c => c.gameObject.scene.IsValid()).ToArray();
            Debug.Log("CROW_TEXT " + all.Length + " texts");
            LogMissingCharacters(all);
            foreach (Component text in all.OrderByDescending(c => c.gameObject.activeInHierarchy).Take(40))
            {
                object font = Get(text, "font");
                object material = Get(text, "fontSharedMaterial") as Material;
                object info = Get(text, "textInfo");
                var mesh = Get(text, "mesh") as Mesh;
                var line = new StringBuilder("CROW_TEXT ");
                line.Append(Path(text.transform)).Append(" active=").Append(text.gameObject.activeInHierarchy);
                line.Append(" world=").Append(text.transform.position.ToString("F1")).Append(" scale=").Append(text.transform.lossyScale.ToString("F3"));
                line.Append(" rotation=").Append(text.transform.rotation.eulerAngles.ToString("F0"));
                var box = text.transform as RectTransform;
                if (box != null) line.Append(" rect=").Append(box.rect.ToString("F1"));
                line.Append(" enabled=").Append(((Behaviour)text).enabled);
                line.Append(" text='").Append((Get(text, "text") as string ?? "").Replace('\n', '|')).Append('\'');
                line.Append(" font=").Append(font == null ? "null" : ((UnityEngine.Object)font).name);
                if (font != null)
                {
                    line.Append(" glyphs=").Append(Count(Get(font, "glyphTable"))).Append(" chars=").Append(Count(Get(font, "characterTable")));
                    line.Append(" atlas=").Append(Get(font, "atlasTexture") is Texture ? ((Texture)Get(font, "atlasTexture")).width + "x" + ((Texture)Get(font, "atlasTexture")).height : "null");
                    object face = Get(font, "faceInfo");
                    line.Append(" pointSize=").Append(face == null ? "?" : Get(face, "pointSize"));
                    line.Append(" lineHeight=").Append(face == null ? "?" : Get(face, "lineHeight"));
                }
                line.Append(" shader=").Append(material == null ? "null" : ((Material)material).shader.name + (((Material)material).shader.isSupported ? "" : " (unsupported)"));
                line.Append(" characters=").Append(info == null ? "?" : Get(info, "characterCount"));
                line.Append(" vertices=").Append(mesh == null ? "?" : mesh.vertexCount.ToString());
                if (mesh != null && mesh.vertexCount > 0)
                {
                    line.Append(" bounds=").Append(mesh.bounds.ToString("F2"));
                    Color32[] colors = mesh.colors32;
                    if (colors.Length > 0) line.Append(" vertexColor=").Append(colors[0]);
                    var uv1 = new List<Vector2>();
                    mesh.GetUVs(1, uv1);
                    if (uv1.Count > 0) line.Append(" uv1=").Append(uv1[0].ToString("F3"));
                }
                var glyphs = font == null ? null : Get(font, "glyphTable") as System.Collections.IList;
                if (glyphs != null && glyphs.Count > 40)
                {
                    object glyph = glyphs[40];
                    line.Append(" glyph40=[index ").Append(Get(glyph, "index")).Append(" scale ").Append(Get(glyph, "scale"));
                    object metrics = Get(glyph, "metrics");
                    line.Append(" w ").Append(Get(metrics, "width")).Append(" h ").Append(Get(metrics, "height"));
                    line.Append(" adv ").Append(Get(metrics, "horizontalAdvance"));
                    object rect = Get(glyph, "glyphRect");
                    line.Append(" rect ").Append(Get(rect, "x")).Append(',').Append(Get(rect, "y")).Append(' ')
                        .Append(Get(rect, "width")).Append('x').Append(Get(rect, "height")).Append(']');
                }
                line.Append(" size=").Append(Get(text, "fontSize")).Append(" color=").Append(Get(text, "color"));
                var canvasRenderer = text.GetComponent<CanvasRenderer>();
                if (canvasRenderer != null)
                {
                    line.Append(" cr=[materials ").Append(canvasRenderer.materialCount);
                    Material drawn = canvasRenderer.materialCount > 0 ? canvasRenderer.GetMaterial(0) : null;
                    line.Append(" material ").Append(drawn == null ? "null" : drawn.name + "/" + drawn.shader.name);
                    line.Append(" cull ").Append(canvasRenderer.cull).Append(" alpha ").Append(canvasRenderer.GetAlpha())
                        .Append(" color ").Append(canvasRenderer.GetColor());
                    float groups = 1f;
                    foreach (CanvasGroup group in text.GetComponentsInParent<CanvasGroup>(true)) groups *= group.alpha;
                    line.Append(" groups ").Append(groups).Append(" absoluteDepth ").Append(canvasRenderer.absoluteDepth);
                    line.Append(" hasMoved ").Append(canvasRenderer.hasMoved).Append(']');
                    if (drawn != null)
                    {
                        line.Append(" props=[");
                        foreach (string name in new[] { "_GradientScale", "_ScaleRatioA", "_ScaleRatioB", "_ScaleRatioC", "_FaceDilate", "_WeightNormal",
                                     "_WeightBold", "_TextureWidth", "_TextureHeight", "_ScaleX", "_ScaleY", "_PerspectiveFilter", "_Sharpness",
                                     "_OutlineWidth", "_OutlineSoftness", "_VertexOffsetX", "_VertexOffsetY", "_MaskSoftnessX", "_StencilComp" })
                            if (drawn.HasProperty(name)) line.Append(name).Append('=').Append(drawn.GetFloat(name)).Append(' ');
                        foreach (string name in new[] { "_FaceColor", "_OutlineColor" })
                            if (drawn.HasProperty(name)) line.Append(name).Append('=').Append(drawn.GetColor(name)).Append(' ');
                        if (drawn.HasProperty("_ClipRect")) line.Append("_ClipRect=").Append(drawn.GetVector("_ClipRect")).Append(' ');
                        line.Append("keywords=").Append(string.Join(",", drawn.shaderKeywords)).Append(" tex=")
                            .Append(drawn.mainTexture == null ? "null" : drawn.mainTexture.name + " " + drawn.mainTexture.width + "x" + drawn.mainTexture.height)
                            .Append(" texformat=").Append(drawn.mainTexture is Texture2D ? ((Texture2D)drawn.mainTexture).format.ToString() : "?").Append(']');
                    }
                }
                Canvas canvas = text.GetComponentInParent<Canvas>();
                if (canvas != null)
                    line.Append(" canvas=[").Append(canvas.name).Append(' ').Append(canvas.renderMode).Append(" order ").Append(canvas.sortingOrder)
                        .Append(" camera ").Append(canvas.worldCamera == null ? "null" : canvas.worldCamera.name)
                        .Append(" channels ").Append(canvas.additionalShaderChannels).Append(']');
                Debug.Log(line.ToString());
            }
        }

        // With -crow-dump-text: each text whose characters its font asset, the font's fallbacks and TMP's
        // default font (and its fallbacks) lack: TMP draws those as the missing glyph, or nothing.
        static void LogMissingCharacters(Component[] texts)
        {
            Type settings = Type.GetType("TMPro.TMP_Settings, Unity.TextMeshPro");
            object defaultFont = null;
            try
            {
                PropertyInfo property = settings == null ? null : settings.GetProperty("defaultFontAsset", BindingFlags.Static | BindingFlags.Public);
                defaultFont = property == null ? null : property.GetValue(null, null);
            }
            catch (Exception) { }
            int withMissing = 0;
            foreach (Component text in texts)
            {
                object font = Get(text, "font");
                string content = Get(text, "text") as string;
                if (font == null || string.IsNullOrEmpty(content)) continue;
                var fonts = new List<object>();
                AddFontChain(font, fonts);
                if (defaultFont != null) AddFontChain(defaultFont, fonts);
                var missing = new List<char>();
                bool inTag = false;
                foreach (char c in content)
                {
                    if (c == '<') inTag = true;
                    if (inTag)
                    {
                        if (c == '>') inTag = false;
                        continue;
                    }
                    if (c < 0x20 || c == 0xA0 || char.IsSurrogate(c) || missing.Contains(c)) continue;
                    if (!fonts.Any(f => { var table = Get(f, "characterLookupTable") as System.Collections.IDictionary; return table != null && table.Contains((uint)c); }))
                        missing.Add(c);
                }
                if (missing.Count == 0) continue;
                withMissing++;
                Debug.Log("CROW_TEXT_MISSING " + Path(text.transform) + " active=" + text.gameObject.activeInHierarchy + " font=" + ((UnityEngine.Object)font).name +
                          " missing=" + string.Join(" ", missing.Select(c => c + "(U+" + ((int)c).ToString("X4") + ")").ToArray()));
            }
            Debug.Log("CROW_TEXT_MISSING " + withMissing + " of " + texts.Length + " texts lack characters");
        }

        static void AddFontChain(object font, List<object> fonts)
        {
            if (font == null || fonts.Contains(font)) return;
            fonts.Add(font);
            var fallbacks = Get(font, "fallbackFontAssetTable") as System.Collections.IEnumerable;
            if (fallbacks != null)
                foreach (object fallback in fallbacks)
                    if (fallback != null && !fallback.Equals(null)) AddFontChain(fallback, fonts);
        }

        // With -crow-dump-text: every active canvas element in draw order, to find what covers a text.
        static void DumpCanvas()
        {
            var renderers = Resources.FindObjectsOfTypeAll<CanvasRenderer>()
                .Where(r => r.gameObject.scene.IsValid() && r.gameObject.activeInHierarchy)
                .OrderBy(r => r.absoluteDepth).ToArray();
            Debug.Log("CROW_CANVAS " + renderers.Length + " active elements");
            foreach (CanvasRenderer renderer in renderers)
            {
                var line = new StringBuilder("CROW_CANVAS depth ").Append(renderer.absoluteDepth).Append(' ').Append(Path(renderer.transform));
                Behaviour graphic = renderer.GetComponents<Behaviour>().FirstOrDefault(b => b != null && b.GetType().GetProperty("mainTexture") != null);
                line.Append(" graphic=").Append(graphic == null ? "none" : graphic.GetType().Name + (graphic.enabled ? "" : " (disabled)"));
                if (graphic != null)
                {
                    line.Append(" color=").Append(Get(graphic, "color"));
                    var texture = Get(graphic, "mainTexture") as Texture;
                    line.Append(" texture=").Append(texture == null ? "null" : texture.name);
                    var drawn = renderer.materialCount > 0 ? renderer.GetMaterial(0) : null;
                    line.Append(" material=").Append(drawn == null ? "null" : drawn.name + "/" + drawn.shader.name);
                }
                var box = renderer.transform as RectTransform;
                if (box != null)
                {
                    var corners = new Vector3[4];
                    box.GetWorldCorners(corners);
                    line.Append(" screen=").Append(corners[0].ToString("F0")).Append('-').Append(corners[2].ToString("F0"));
                }
                line.Append(" cull ").Append(renderer.cull).Append(" alpha ").Append(renderer.GetAlpha());
                float groups = 1f;
                foreach (CanvasGroup group in renderer.GetComponentsInParent<CanvasGroup>(true)) groups *= group.alpha;
                line.Append(" groups ").Append(groups);
                Debug.Log(line.ToString());
            }
        }

        // -crow-text-shader <name>: every TextMeshPro text's material drawn with that shader instead,
        // to tell a shader that draws nothing from text that has no geometry.
        static void SwapTextShader(string name)
        {
            Shader shader = Shader.Find(name);
            Type textType = Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro");
            if (shader == null || textType == null) return;
            foreach (UnityEngine.Object text in Resources.FindObjectsOfTypeAll(textType))
            {
                var material = Get(text, "fontSharedMaterial") as Material;
                if (material != null && material.shader != shader) material.shader = shader;
                var canvasRenderer = ((Component)text).GetComponent<CanvasRenderer>();
                for (int i = 0; canvasRenderer != null && i < canvasRenderer.materialCount; i++)
                {
                    Material drawn = canvasRenderer.GetMaterial(i);
                    if (drawn != null && drawn.shader != shader) drawn.shader = shader;
                }
            }
        }

        // -crow-language: the game sets I2's language from its settings save at startup, so this sets
        // LocalizationManager.CurrentLanguage (by reflection) whenever it differs, every half second.
        static void KeepLanguage()
        {
            if (currentLanguage == null)
            {
                Type manager = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("I2.Loc.LocalizationManager", false)).FirstOrDefault(t => t != null);
                currentLanguage = manager == null ? null : manager.GetProperty("CurrentLanguage", BindingFlags.Static | BindingFlags.Public);
                if (currentLanguage == null)
                {
                    Debug.Log("CROW_DIAG no I2.Loc.LocalizationManager.CurrentLanguage");
                    language = null;
                    return;
                }
            }
            try
            {
                if (Equals(currentLanguage.GetValue(null, null), language)) return;
                Debug.Log("CROW_DIAG language " + currentLanguage.GetValue(null, null) + " -> " + language);
                currentLanguage.SetValue(null, language, null);
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_DIAG language: " + exception);
                language = null;
            }
        }

        static object Get(object target, string name)
        {
            try
            {
                Type type = target.GetType();
                PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null) return property.GetValue(target, null);
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return field == null ? null : field.GetValue(target);
            }
            catch (Exception exception)
            {
                return "<" + exception.GetType().Name + ">";
            }
        }

        static string Count(object list)
        {
            var collection = list as System.Collections.ICollection;
            return collection == null ? "null" : collection.Count.ToString();
        }

        static string Path(Transform transform)
        {
            return transform.parent == null ? transform.name : Path(transform.parent) + "/" + transform.name;
        }

        // -crow-input: Unity's joystick buttons (any joystick, then joysticks 1-2) as they go down,
        // and the keys and mouse buttons Unity reports, to tell input that never reaches Unity from
        // input that Rewired doesn't map.
        static void LogInput()
        {
            if (!Input.anyKeyDown) return;
            var down = new List<string>();
            for (KeyCode key = KeyCode.Backspace; key <= KeyCode.Joystick2Button19; key++)
                if (Enum.IsDefined(typeof(KeyCode), key) && Input.GetKeyDown(key)) down.Add(key.ToString());
            Debug.Log("CROW_INPUT down " + string.Join(" ", down.ToArray()));
        }

        // Unity's joystick names and Rewired's state (by reflection: CrowCompat doesn't reference Rewired).
        static void ReportInput()
        {
            var line = new StringBuilder("CROW_INPUT unity joysticks=[").Append(string.Join("|", Input.GetJoystickNames())).Append(']');
            Type reInput = Type.GetType("Rewired.ReInput, Rewired_Core");
            if (reInput == null)
            {
                Debug.Log(line.Append(" no Rewired.ReInput").ToString());
                return;
            }
            object ready = GetStatic(reInput, "isReady");
            line.Append(" rewired ready=").Append(ready);
            if (Equals(ready, true))
            {
                object controllers = GetStatic(reInput, "controllers");
                line.Append(" joystickCount=").Append(controllers == null ? "?" : Get(controllers, "joystickCount"));
                var joysticks = controllers == null ? null : Get(controllers, "Joysticks") as System.Collections.IEnumerable;
                if (joysticks != null)
                    foreach (object joystick in joysticks)
                        line.Append(" [").Append(Get(joystick, "name")).Append(" / ").Append(Get(joystick, "hardwareName"))
                            .Append(" buttons ").Append(Count(Get(joystick, "Buttons"))).Append(']');
                object keyboard = controllers == null ? null : Get(controllers, "Keyboard");
                line.Append(" keyboard=").Append(keyboard == null ? "null" : Get(keyboard, "enabled"));
                object players = GetStatic(reInput, "players");
                var all = players == null ? null : Get(players, "AllPlayers") as System.Collections.IEnumerable;
                if (all != null)
                    foreach (object player in all)
                    {
                        object playerControllers = Get(player, "controllers");
                        line.Append(" player ").Append(Get(player, "name")).Append(" joysticks=")
                            .Append(playerControllers == null ? "?" : Get(playerControllers, "joystickCount"))
                            .Append(" hasKeyboard=").Append(playerControllers == null ? "?" : Get(playerControllers, "hasKeyboard"));
                    }
            }
            Debug.Log(line.ToString());
        }

        static object GetStatic(Type type, string name)
        {
            try
            {
                PropertyInfo property = type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                return property == null ? null : property.GetValue(null, null);
            }
            catch (Exception e)
            {
                return "<" + e.GetType().Name + ">";
            }
        }

        void Beat(float now)
        {
            int gc = GC.CollectionCount(0);
            int visible = 0, renderers = 0, lit = 0;
            var lights = new Dictionary<LightType, int>();
            var active = new List<Light>();
            foreach (Light light in FindObjectsOfType<Light>())
            {
                if (!light.enabled || light.intensity <= 0f || !light.gameObject.activeInHierarchy) continue;
                active.Add(light);
                lit++;
                int count;
                lights.TryGetValue(light.type, out count);
                lights[light.type] = count + 1;
            }
            // Estimated draw calls, before the engine's batching: each visible renderer's materials
            // in the base pass, and an additive pass per point or spot light whose range reaches its
            // bounds, up to the quality level's pixel lights (the brightest directional light is in
            // the base pass).
            int drawBase = 0, drawAdd = 0, pixelLights = QualitySettings.pixelLightCount;
            foreach (Renderer renderer in FindObjectsOfType<Renderer>())
            {
                if (!renderer.enabled) continue;
                renderers++;
                if (!renderer.isVisible) continue;
                visible++;
                int materials = renderer.sharedMaterials.Length;
                drawBase += materials;
                if (renderer is ParticleSystemRenderer || renderer is SpriteRenderer) continue;
                Bounds bounds = renderer.bounds;
                int reaching = 0;
                foreach (Light light in active)
                {
                    if (light.type == LightType.Directional) continue;
                    if (bounds.SqrDistance(light.transform.position) <= light.range * light.range) reaching++;
                }
                drawAdd += Math.Min(reaching, pixelLights) * materials;
            }
            var shadowed = new Dictionary<LightType, int>();
            foreach (Light light in active)
            {
                if (light.shadows == LightShadows.None) continue;
                int count;
                shadowed.TryGetValue(light.type, out count);
                shadowed[light.type] = count + 1;
            }
            var line = new StringBuilder("HEARTBEAT");
            line.Append(" t=").Append((now - started).ToString("F1"));
            line.Append(" scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
            line.Append(" fps=").Append((frames / (now - lastBeat)).ToString("F1"));
            line.Append(" ms=").Append((frameSum / Math.Max(frames, 1)).ToString("F2"));
            line.Append(" worst=").Append(worst.ToString("F1"));
            line.Append(" gc=").Append(gc - collections);
            line.Append(" heapMB=").Append((GC.GetTotalMemory(false) / 1048576.0).ToString("F1"));
            line.Append(" renderers=").Append(renderers).Append(" visible=").Append(visible);
            line.Append(" lights=").Append(lit);
            foreach (KeyValuePair<LightType, int> pair in lights) line.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
            line.Append(" drawBase=").Append(drawBase).Append(" drawAdd=").Append(drawAdd);
            foreach (KeyValuePair<LightType, int> pair in shadowed) line.Append(" shadow").Append(pair.Key).Append('=').Append(pair.Value);
            Debug.Log(line.ToString());
            lastBeat = now;
            frames = 0;
            frameSum = 0;
            worst = 0f;
            collections = gc;
        }
    }
}
