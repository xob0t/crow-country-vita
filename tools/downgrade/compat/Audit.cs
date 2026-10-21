using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// The fight frame audit: what a fight frame holds and draws, counted at run time. It only reads (and
// clears Transform.hasChanged, which no engine system uses); nothing it does changes what runs.
//   -crow-audit <s>          s seconds after the fight starts (CROW_FIGHT_START; without -crow-fight, s
//                            seconds after each scene load), over -crow-audit-frames <n> frames (10):
//                            CROW_AUDIT_ENEMY per enemy type (instances, awake; transforms total, active,
//                            the hierarchy each one dirties; renderers all/enabled/visible, vertices,
//                            triangles and draws of the enabled ones, materials, shaders, the merge's
//                            renderers; colliders by body (none: a static collider), compound children;
//                            bodies, Animators, Animations, FSMs total/enabled/running/every-frame, audio
//                            sources, lights, particles), CROW_AUDIT_ENEMY_FRAME (per frame: transforms
//                            changed, the roots that moved, transforms whose local pose changed, the
//                            colliders that moved, static ones and compound children among them,
//                            renderers moved), CROW_AUDIT_WRITERS (who changes local poses inside the
//                            enemies: Decor.cs' MotionReport attribution, per frame), CROW_AUDIT_KINDS
//                            (transforms by their components, active and inactive), CROW_AUDIT_CHILDREN
//                            (the root's children by size), CROW_AUDIT_INACTIVE (the biggest inactive
//                            subtrees), CROW_AUDIT_FSM (each FSM of one awake enemy of each type: active
//                            state, its actions, every-frame actions, what its states' actions do),
//                            CROW_AUDIT_SHADER (the enemies' draws by shader); for the whole scene
//                            CROW_AUDIT_FRAME (changed transforms, hierarchies, moved renderers by owner),
//                            CROW_AUDIT_CAMERA (each camera render in the frames: target, mask, clears),
//                            CROW_AUDIT_DRAW (the main camera's frustum: renderers in view by owner,
//                            draws as submeshes x forward passes, estimated additive light passes,
//                            triangles, vertices, transparent draws, the screen covered by bounds,
//                            shadow casters per shadowed light), CROW_AUDIT_DRAW_SHADER, CROW_AUDIT_LIGHT,
//                            CROW_AUDIT_TEXTURE (the textures in view by format and mipmaps), CROW_AUDIT_UI
//                            (canvases and their renderers), CROW_AUDIT_GLOBALS (the depth and shadow
//                            textures the camera's render set)
//   -crow-audit-every <s>    again every s seconds
//   -crow-renderdoc <s>      Windows players started under RenderDoc (renderdoccmd capture): a capture of
//                            one frame s seconds after the fight starts (or after the load), through
//                            RenderDoc's in-application API (CROW_RENDERDOC); -crow-renderdoc-count <n>
//                            more, 2 s apart
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    static class Audit
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        static float after = -1f, every = -1f, captureAfter = -1f;
        static int frames = 10, captures = 1, captured;
        static float loadedAt = -1f, nextAudit = -1f, nextCapture = -1f;
        static int sample = -1;

        internal static void Install(string[] args)
        {
            after = CrowFlags.Number(args, "-crow-audit", -1f);
            every = CrowFlags.Number(args, "-crow-audit-every", -1f);
            frames = Math.Max(2, (int)CrowFlags.Number(args, "-crow-audit-frames", 10f));
            captureAfter = CrowFlags.Number(args, "-crow-renderdoc", -1f);
            captures = Math.Max(1, (int)CrowFlags.Number(args, "-crow-renderdoc-count", 1f));
            if (after < 0f && captureAfter < 0f) return;
            CrowEvents.Loaded("Audit.sceneLoaded", (scene, mode) =>
            {
                loadedAt = Time.realtimeSinceStartup;
                if (!Fight.On && after >= 0f) nextAudit = loadedAt + after;
                if (!Fight.On && captureAfter >= 0f && captured == 0) nextCapture = loadedAt + captureAfter;
            });
            CrowEvents.PreCull("Audit.OnPreCull", OnPreCull);
            CrowEvents.PostRender("Audit.OnPostRender", OnPostRender);
            CrowEvents.BeforeRender("Audit.Tick", Tick);
            Debug.Log("CROW_AUDIT on: after " + after + " s, every " + every + " s, " + frames + " frames; renderdoc " + captureAfter);
        }

        static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (Fight.On)
            {
                float start = Fight.StartedAt;
                if (start < 0f) return;
                if (nextAudit < 0f && after >= 0f && sample < 0 && auditsDone == 0) nextAudit = start + after;
                if (nextCapture < 0f && captureAfter >= 0f && captured == 0) nextCapture = start + captureAfter;
            }
            if (nextCapture > 0f && now >= nextCapture)
            {
                RenderDoc.Trigger();
                captured++;
                nextCapture = captured < captures ? now + 2f : -1f;
            }
            try
            {
                if (sample < 0)
                {
                    if (nextAudit < 0f || now < nextAudit) return;
                    nextAudit = every > 0f ? now + every : -1f;
                    Begin();
                    return;
                }
                Measure();
                if (++sample < frames) return;
                Report();
                sample = -1;
                auditsDone++;
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_AUDIT failed: " + exception);
                sample = -1;
                auditsDone++;
            }
        }

        static int auditsDone;

        // ------------------------------------------------------------------------------ the census

        sealed class Enemy
        {
            public Transform Root;
            public string Type, Cull;
            public bool Awake;
            public Transform[] Transforms;
            public Vector3[] Position, Scale;
            public Quaternion[] Rotation;
            public Collider[] Colliders;
            public Vector3[] ColliderPosition;
            public Quaternion[] ColliderRotation;
            public Renderer[] Renderers;
            public readonly Dictionary<Transform, int> Moves = new Dictionary<Transform, int>();
        }

        sealed class TypeStats
        {
            public int Changed, RootMoved, LocalMoved, CollidersMoved, StaticMoved, CompoundMoved, BodyMoved, RenderersMoved, AwakeChanged;
            public readonly Dictionary<string, int> Writers = new Dictionary<string, int>();
            public readonly Dictionary<string, string> WriterExample = new Dictionary<string, string>();
        }

        static readonly List<Enemy> enemies = new List<Enemy>();
        static Dictionary<string, TypeStats> types;
        static Transform[] all;
        static Renderer[] renderers;
        static Dictionary<Transform, int> enemyOf;
        static int sceneChanged, sceneEnemyChanged, scenePlayerChanged, sceneHierarchies, sceneHierarchyTransforms;
        static int movedEnemyRenderers, movedPlayerRenderers, movedOtherRenderers;
        static readonly Dictionary<string, int> hierarchyMoves = new Dictionary<string, int>();
        static int cameraRenders;
        static readonly Dictionary<string, int> cameraCounts = new Dictionary<string, int>();
        static bool depthTexture, shadowTexture;

        static Type fsmType;
        static PropertyInfo activeStateName, fsmProperty, fsmName, activeState, statesProperty, actionsProperty, actionEnabled, stateName, finished, fsmList;

        static bool ReflectFsm()
        {
            if (fsmType != null) return true;
            fsmType = Type.GetType("PlayMakerFSM, PlayMaker");
            if (fsmType == null) return false;
            activeStateName = fsmType.GetProperty("ActiveStateName");
            fsmProperty = fsmType.GetProperty("Fsm");
            fsmList = fsmType.GetProperty("FsmList", BindingFlags.Public | BindingFlags.Static);
            Type fsm = fsmProperty.PropertyType;
            fsmName = fsm.GetProperty("Name");
            activeState = fsm.GetProperty("ActiveState");
            statesProperty = fsm.GetProperty("States");
            finished = fsm.GetProperty("Finished");
            actionsProperty = activeState.PropertyType.GetProperty("Actions");
            stateName = activeState.PropertyType.GetProperty("Name");
            actionEnabled = Type.GetType("HutongGames.PlayMaker.FsmStateAction, PlayMaker").GetProperty("Enabled");
            return true;
        }

        static string CullState(Transform root)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != "Pause/Cull" && t.name != "Pause/Cull?") continue;
                Component fsm = t.GetComponent(fsmType);
                if (fsm == null) return "-";
                return (string)activeStateName.GetValue(fsm, null) ?? "-";
            }
            return "-";
        }

        static void Begin()
        {
            ReflectFsm();
            MotionReport.InsideEnemies = true;
            MotionReport.PrepareDrivers();
            enemies.Clear();
            types = new Dictionary<string, TypeStats>();
            enemyOf = new Dictionary<Transform, int>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (Rigidbody body in root.GetComponentsInChildren<Rigidbody>(true))
                    {
                        if (!body.name.StartsWith("Enemy ", StringComparison.Ordinal) || !body.gameObject.activeInHierarchy) continue;
                        Transform r = body.transform;
                        if (enemyOf.ContainsKey(r)) continue;
                        bool nested = false;
                        for (Transform p = r.parent; p != null; p = p.parent)
                            if (p.name.StartsWith("Enemy ", StringComparison.Ordinal) && p.GetComponent<Rigidbody>() != null) nested = true;
                        if (nested) continue;
                        string type = body.name.Substring(6);
                        int paren = type.IndexOf(" (", StringComparison.Ordinal);
                        if (paren > 0) type = type.Substring(0, paren);
                        var enemy = new Enemy { Root = r, Type = type.Replace(' ', '_') };
                        enemy.Cull = CullState(r);
                        enemy.Awake = enemy.Cull == "visible";
                        enemy.Transforms = r.GetComponentsInChildren<Transform>(true);
                        int n = enemy.Transforms.Length;
                        enemy.Position = new Vector3[n];
                        enemy.Scale = new Vector3[n];
                        enemy.Rotation = new Quaternion[n];
                        for (int k = 0; k < n; k++)
                        {
                            Transform t = enemy.Transforms[k];
                            enemy.Position[k] = t.localPosition;
                            enemy.Rotation[k] = t.localRotation;
                            enemy.Scale[k] = t.localScale;
                            enemyOf[t] = enemies.Count;
                        }
                        enemy.Colliders = r.GetComponentsInChildren<Collider>(true);
                        enemy.ColliderPosition = new Vector3[enemy.Colliders.Length];
                        enemy.ColliderRotation = new Quaternion[enemy.Colliders.Length];
                        for (int k = 0; k < enemy.Colliders.Length; k++) RelativePose(enemy.Colliders[k], out enemy.ColliderPosition[k], out enemy.ColliderRotation[k]);
                        enemy.Renderers = r.GetComponentsInChildren<Renderer>(true);
                        enemies.Add(enemy);
                        if (!types.ContainsKey(enemy.Type)) types[enemy.Type] = new TypeStats();
                    }
            }
            all = Resources.FindObjectsOfTypeAll<Transform>().Where(t => t != null && t.gameObject.scene.IsValid()).ToArray();
            foreach (Transform t in all) t.hasChanged = false;
            renderers = all.Select(t => t.GetComponent<Renderer>()).Where(r => r != null).ToArray();
            sceneChanged = sceneEnemyChanged = scenePlayerChanged = sceneHierarchies = sceneHierarchyTransforms = 0;
            movedEnemyRenderers = movedPlayerRenderers = movedOtherRenderers = 0;
            hierarchyMoves.Clear();
            depthTexture = shadowTexture = false;  // this audit's frames
            cameraRenders = 0;
            cameraCounts.Clear();
            sample = 0;
            Debug.Log("CROW_AUDIT begin scene=" + SceneManager.GetActiveScene().name.Replace(' ', '_') + " enemies=" + enemies.Count + " transforms=" + all.Length +
                      " renderers=" + renderers.Length + " t=" + Time.realtimeSinceStartup.ToString("0.0", Inv));
        }

        static void RelativePose(Collider collider, out Vector3 position, out Quaternion rotation)
        {
            Rigidbody body = collider.attachedRigidbody;
            Transform t = collider.transform;
            if (body == null || body.transform == t)
            {
                position = t.position;
                rotation = t.rotation;
                return;
            }
            position = body.transform.InverseTransformPoint(t.position);
            rotation = Quaternion.Inverse(body.transform.rotation) * t.rotation;
        }

        static bool IsPlayer(Transform t)
        {
            return t.gameObject.scene.name == "DontDestroyOnLoad" || t.gameObject.scene.name == MaraScene.Scene;
        }

        static void Measure()
        {
            // Read every flag first, then clear: a parent's change sets its children's flags too.
            var roots = new HashSet<Transform>();
            foreach (Transform t in all)
            {
                if (t == null || !t.hasChanged) continue;
                sceneChanged++;
                if (enemyOf.ContainsKey(t)) sceneEnemyChanged++;
                else if (IsPlayer(t)) scenePlayerChanged++;
                Transform root = t.root;
                if (roots.Add(root))
                {
                    sceneHierarchyTransforms += root.hierarchyCount;
                    string key = root.name.Replace(' ', '_') + "(" + root.hierarchyCount + ")";
                    int seen;
                    hierarchyMoves.TryGetValue(key, out seen);
                    hierarchyMoves[key] = seen + 1;
                }
            }
            sceneHierarchies += roots.Count;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy || !renderer.transform.hasChanged) continue;
                if (enemyOf.ContainsKey(renderer.transform)) movedEnemyRenderers++;
                else if (IsPlayer(renderer.transform)) movedPlayerRenderers++;
                else movedOtherRenderers++;
            }
            foreach (Enemy enemy in enemies)
            {
                if (enemy.Root == null) continue;
                TypeStats stats = types[enemy.Type];
                int changed = 0;
                if (enemy.Root.hasChanged) stats.RootMoved++;
                for (int k = 0; k < enemy.Transforms.Length; k++)
                {
                    Transform t = enemy.Transforms[k];
                    if (t == null) continue;
                    if (t.hasChanged) changed++;
                    if (t.localPosition == enemy.Position[k] && t.localRotation == enemy.Rotation[k] && t.localScale == enemy.Scale[k]) continue;
                    enemy.Position[k] = t.localPosition;
                    enemy.Rotation[k] = t.localRotation;
                    enemy.Scale[k] = t.localScale;
                    int moves;
                    enemy.Moves.TryGetValue(t, out moves);
                    enemy.Moves[t] = moves + 1;
                    if (k == 0) continue;  // the root: RootMoved
                    stats.LocalMoved++;
                    string detail;
                    string label = MotionReport.LabelOf(t, out detail);
                    int n;
                    stats.Writers.TryGetValue(label, out n);
                    stats.Writers[label] = n + 1;
                    if (!stats.WriterExample.ContainsKey(label)) stats.WriterExample[label] = Perf.Path(t, 3) + (detail == null ? "" : " " + detail.Replace(' ', '_'));
                }
                stats.Changed += changed;
                if (enemy.Awake) stats.AwakeChanged += changed;
                for (int k = 0; k < enemy.Colliders.Length; k++)
                {
                    Collider collider = enemy.Colliders[k];
                    if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                    if (!collider.transform.hasChanged) continue;
                    stats.CollidersMoved++;
                    Rigidbody body = collider.attachedRigidbody;
                    Vector3 position;
                    Quaternion rotation;
                    RelativePose(collider, out position, out rotation);
                    bool relative = position != enemy.ColliderPosition[k] || rotation != enemy.ColliderRotation[k];
                    enemy.ColliderPosition[k] = position;
                    enemy.ColliderRotation[k] = rotation;
                    if (body == null) stats.StaticMoved++;
                    else if (body.transform != collider.transform && relative) stats.CompoundMoved++;
                    else stats.BodyMoved++;
                }
                foreach (Renderer renderer in enemy.Renderers)
                    if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy && renderer.transform.hasChanged) stats.RenderersMoved++;
            }
            foreach (Transform t in all)
                if (t != null) t.hasChanged = false;
        }

        static void OnPreCull(Camera camera)
        {
            if (sample < 0) return;
            cameraRenders++;
            RenderTexture target = camera.targetTexture;
            string key = camera.name.Replace(' ', '_') + " target=" + (target == null ? "screen" : target.width + "x" + target.height + (target.depth > 0 ? "d" + target.depth : "")) +
                         " mask=0x" + camera.cullingMask.ToString("x") + " clear=" + camera.clearFlags + " depth=" + camera.depth.ToString("0.#", Inv) +
                         " path=" + camera.actualRenderingPath + " occl=" + (camera.useOcclusionCulling ? 1 : 0) + " depthTex=" + camera.depthTextureMode +
                         " far=" + camera.farClipPlane.ToString("0.#", Inv) + " effects=" + string.Join("+", camera.GetComponents<MonoBehaviour>()
                             .Where(b => b != null && b.enabled && b.GetType().GetMethod("OnRenderImage", Fields) != null).Select(b => b.GetType().Name).ToArray());
            int n;
            cameraCounts.TryGetValue(key, out n);
            cameraCounts[key] = n + 1;
        }

        static void OnPostRender(Camera camera)
        {
            if (sample < 0 || camera != Camera.main) return;
            depthTexture |= Shader.GetGlobalTexture("_CameraDepthTexture") != null;
            shadowTexture |= Shader.GetGlobalTexture("_ShadowMapTexture") != null;
        }

        // ------------------------------------------------------------------------------ the report

        static string F(float value)
        {
            return value.ToString("0.#", Inv);
        }

        static string PerFrame(int count)
        {
            return (count / (float)frames).ToString("0.#", Inv);
        }

        static void Report()
        {
            string scene = SceneManager.GetActiveScene().name.Replace(' ', '_');
            Debug.Log("CROW_AUDIT_FRAME scene=" + scene + " frames=" + frames + " transforms=" + all.Length + " changed=" + PerFrame(sceneChanged) +
                      " enemy=" + PerFrame(sceneEnemyChanged) + " player=" + PerFrame(scenePlayerChanged) +
                      " other=" + PerFrame(sceneChanged - sceneEnemyChanged - scenePlayerChanged) + " hierarchies=" + PerFrame(sceneHierarchies) +
                      " hierarchyTransforms=" + PerFrame(sceneHierarchyTransforms) + " renderersMoved=" + PerFrame(movedEnemyRenderers + movedPlayerRenderers + movedOtherRenderers) +
                      " (enemy " + PerFrame(movedEnemyRenderers) + ", player " + PerFrame(movedPlayerRenderers) + ", other " + PerFrame(movedOtherRenderers) + ")" +
                      " cameraRenders=" + PerFrame(cameraRenders));
            Debug.Log("CROW_AUDIT_HIERARCHIES " + string.Join(" ", hierarchyMoves.OrderByDescending(p => p.Value).ThenByDescending(p => p.Key.Length)
                .Take(20).Select(p => p.Key + "x" + PerFrame(p.Value)).ToArray()));
            foreach (KeyValuePair<string, int> pair in cameraCounts.OrderByDescending(p => p.Value))
                Debug.Log("CROW_AUDIT_CAMERA " + pair.Key + " renders=" + PerFrame(pair.Value));
            Debug.Log("CROW_AUDIT_GLOBALS depthTexture=" + (depthTexture ? 1 : 0) + " shadowMapTexture=" + (shadowTexture ? 1 : 0) +
                      " pixelLights=" + QualitySettings.pixelLightCount + " shadows=" + QualitySettings.shadows + " shadowRes=" + QualitySettings.shadowResolution +
                      " shadowDistance=" + F(QualitySettings.shadowDistance) + " cascades=" + QualitySettings.shadowCascades + " projection=" + QualitySettings.shadowProjection +
                      " aa=" + QualitySettings.antiAliasing + " aniso=" + QualitySettings.anisotropicFiltering + " lod=" + F(QualitySettings.lodBias) +
                      " texLimit=" + QualitySettings.masterTextureLimit + " vsync=" + QualitySettings.vSyncCount + " fog=" + (RenderSettings.fog ? RenderSettings.fogMode.ToString() : "off") +
                      " screen=" + Screen.width + "x" + Screen.height + " gfx=" + SystemInfo.graphicsDeviceType + " jobs=" + SystemInfo.graphicsMultiThreaded);
            EnemyReport();
            DrawReport();
            UiReport();
        }

        static void EnemyReport()
        {
            foreach (IGrouping<string, Enemy> group in enemies.Where(e => e.Root != null).GroupBy(e => e.Type).OrderBy(g => g.Key))
            {
                Enemy one = group.FirstOrDefault(e => e.Awake) ?? group.First();
                TypeStats stats = types[group.Key];
                Transform root = one.Root;
                int active = one.Transforms.Count(t => t != null && t.gameObject.activeInHierarchy);
                var enabled = one.Renderers.Where(r => r != null && r.enabled && r.gameObject.activeInHierarchy).ToArray();
                long vertices = 0, triangles = 0;
                int draws = 0, skinned = 0, merged = 0, casters = 0, probes = 0, reflections = 0;
                var materials = new HashSet<Material>();
                var shaders = new Dictionary<string, int>();
                var meshes = new Dictionary<string, long[]>();
                foreach (Renderer renderer in enabled)
                {
                    if (renderer is SkinnedMeshRenderer) skinned++;
                    if (renderer.GetComponent("MergedRenderer") != null || renderer.name.IndexOf("Combined", StringComparison.OrdinalIgnoreCase) >= 0) merged++;
                    if (renderer.shadowCastingMode != ShadowCastingMode.Off) casters++;
                    if (renderer.lightProbeUsage != LightProbeUsage.Off) probes++;
                    if (renderer.reflectionProbeUsage != ReflectionProbeUsage.Off) reflections++;
                    Mesh mesh = MeshOf(renderer);
                    Material[] shared = renderer.sharedMaterials;
                    draws += shared.Length;
                    if (mesh != null)
                    {
                        vertices += mesh.vertexCount;
                        long own = 0;
                        for (int s = 0; s < shared.Length; s++) own += Triangles(mesh, Math.Min(s, mesh.subMeshCount - 1));
                        triangles += own;
                        string key = mesh.name.Replace(' ', '_') + (renderer.GetComponent("MergedRenderer") != null ? "(merged)" : "");
                        long[] seen;
                        if (!meshes.TryGetValue(key, out seen)) meshes[key] = seen = new long[3];
                        seen[0]++;
                        seen[1] = own;
                        seen[2] = shared.Length;
                    }
                    foreach (Material material in shared)
                    {
                        if (material == null) continue;
                        materials.Add(material);
                        string shader = material.shader == null ? "-" : material.shader.name.Replace(' ', '_');
                        int n;
                        shaders.TryGetValue(shader, out n);
                        shaders[shader] = n + 1;
                    }
                }
                Collider[] colliders = one.Colliders.Where(c => c != null).ToArray();
                Collider[] on = colliders.Where(c => c.enabled && c.gameObject.activeInHierarchy).ToArray();
                int noBody = on.Count(c => c.attachedRigidbody == null);
                int kinematic = on.Count(c => c.attachedRigidbody != null && c.attachedRigidbody.isKinematic);
                int dynamic = on.Count(c => c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic);
                int compound = on.Count(c => c.attachedRigidbody != null && c.attachedRigidbody.transform != c.transform);
                Rigidbody[] bodies = root.GetComponentsInChildren<Rigidbody>(true);
                Animator[] animators = root.GetComponentsInChildren<Animator>(true);
                Animation[] animations = root.GetComponentsInChildren<Animation>(true);
                Component[] fsms = fsmType == null ? new Component[0] : root.GetComponentsInChildren(fsmType, true);
                var running = new HashSet<object>();
                var list = fsmList == null ? null : fsmList.GetValue(null, null) as IList;
                if (list != null) foreach (object f in list) running.Add(f);
                int fsmOn = 0, fsmRunning = 0, fsmEvery = 0;
                foreach (Component fsm in fsms)
                {
                    var behaviour = fsm as Behaviour;
                    if (behaviour == null || !behaviour.isActiveAndEnabled) continue;
                    fsmOn++;
                    object machine = fsmProperty.GetValue(fsm, null);
                    if (machine == null || (bool)finished.GetValue(machine, null) || activeState.GetValue(machine, null) == null) continue;
                    if (!running.Contains(fsm)) continue;
                    fsmRunning++;
                    if (EveryFrame(activeState.GetValue(machine, null)) > 0) fsmEvery++;
                }
                AudioSource[] sources = root.GetComponentsInChildren<AudioSource>(true);
                Light[] lights = root.GetComponentsInChildren<Light>(true);
                ParticleSystem[] particles = root.GetComponentsInChildren<ParticleSystem>(true);
                Transform top = root;
                int depth = 0;
                while (top.parent != null)
                {
                    top = top.parent;
                    depth++;
                }
                Debug.Log("CROW_AUDIT_ENEMY type=" + group.Key + " n=" + group.Count() + " awake=" + group.Count(e => e.Awake) + " one=" + (one.Awake ? "awake" : one.Cull.Replace(' ', '_')) +
                          " transforms=" + one.Transforms.Length + " active=" + active + " inactive=" + (one.Transforms.Length - active) +
                          " hierarchy=" + root.hierarchyCount + " root=" + top.name.Replace(' ', '_') + " depth=" + depth +
                          " renderers=" + one.Renderers.Length + "/" + enabled.Length + "/" + enabled.Count(r => r.isVisible) + " skinned=" + skinned +
                          " verts=" + vertices + " tris=" + triangles + " draws=" + draws + " materials=" + materials.Count + " shaders=" + shaders.Count +
                          " merged=" + merged + " casters=" + casters + " lightProbes=" + probes + " reflProbes=" + reflections +
                          " colliders=" + colliders.Length + "/" + on.Length + " triggers=" + on.Count(c => c.isTrigger) + " noBody=" + noBody + " kinBody=" + kinematic +
                          " dynBody=" + dynamic + " compound=" + compound + " kinds=" + string.Join(",", on.GroupBy(c => c.GetType().Name.Replace("Collider", ""))
                              .Select(g => g.Key + ":" + g.Count()).ToArray()) +
                          " bodies=" + bodies.Length + "/" + bodies.Count(b => b.gameObject.activeInHierarchy) + " kinematic=" + bodies.Count(b => b.gameObject.activeInHierarchy && b.isKinematic) +
                          " rootBody=" + (root.GetComponent<Rigidbody>().isKinematic ? "kin" : "dyn") + " interp=" + root.GetComponent<Rigidbody>().interpolation +
                          " animators=" + animators.Length + "/" + animators.Count(a => a.isActiveAndEnabled) + (animators.Length > 0 ? ":" + string.Join("+", animators.Select(a => a.cullingMode.ToString()).Distinct().ToArray()) : "") +
                          " animations=" + animations.Length + "/" + animations.Count(a => a.isActiveAndEnabled && a.isPlaying) + (animations.Length > 0 ? ":" + string.Join("+", animations.Select(a => a.cullingType.ToString()).Distinct().ToArray()) : "") +
                          " fsms=" + fsms.Length + "/" + fsmOn + "/" + fsmRunning + "/" + fsmEvery +
                          " audio=" + sources.Length + "/" + sources.Count(s => s.isActiveAndEnabled) + "/" + sources.Count(s => s.isActiveAndEnabled && s.isPlaying) +
                          " lights=" + lights.Length + "/" + lights.Count(l => l.isActiveAndEnabled) + " particles=" + particles.Length + "/" + particles.Count(p => p.gameObject.activeInHierarchy && p.isPlaying) +
                          " behaviours=" + root.GetComponentsInChildren<MonoBehaviour>(true).Length + "/" + root.GetComponentsInChildren<MonoBehaviour>().Count(b => b.enabled));
                Debug.Log("CROW_AUDIT_ENEMY_FRAME type=" + group.Key + " n=" + group.Count() + " changed=" + PerFrame(stats.Changed) + " awakeChanged=" + PerFrame(stats.AwakeChanged) +
                          " rootsMoved=" + PerFrame(stats.RootMoved) + " localMoved=" + PerFrame(stats.LocalMoved) + " collidersMoved=" + PerFrame(stats.CollidersMoved) +
                          " static=" + PerFrame(stats.StaticMoved) + " compound=" + PerFrame(stats.CompoundMoved) + " withBody=" + PerFrame(stats.BodyMoved) +
                          " renderersMoved=" + PerFrame(stats.RenderersMoved) + " states=" + string.Join(",", group.Select(e => e.Cull.Replace(' ', '_')).ToArray()));
                foreach (KeyValuePair<string, int> writer in stats.Writers.OrderByDescending(p => p.Value).Take(12))
                    Debug.Log("CROW_AUDIT_WRITERS type=" + group.Key + " " + writer.Key + " " + PerFrame(writer.Value) + " ex=" + stats.WriterExample[writer.Key]);
                Kinds(group.Key, one);
                Needed(group.Key, one);
                Children(group.Key, root);
                Shaders(group.Key, shaders);
                Debug.Log("CROW_AUDIT_MESHES type=" + group.Key + " " + string.Join(" ", meshes.OrderByDescending(p => p.Value[0] * p.Value[1]).Take(16)
                    .Select(p => p.Key + ":" + p.Value[1] + "tris/" + p.Value[2] + "sub x" + p.Value[0]).ToArray()));
                Machines(group.Key, fsms, running);
                Behaviours(group.Key, root);
            }
        }

        static Mesh MeshOf(Renderer renderer)
        {
            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned != null) return skinned.sharedMesh;
            var filter = renderer.GetComponent<MeshFilter>();
            return filter == null ? null : filter.sharedMesh;
        }

        static long Triangles(Mesh mesh, int submesh)
        {
            if (submesh < 0) return 0;
            try
            {
                return mesh.GetTopology(submesh) == MeshTopology.Triangles ? (long)mesh.GetIndexCount(submesh) / 3 : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        static string Key(Transform t)
        {
            var names = t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name == "PlayMakerFSM" ? "FSM" : c.GetType().Name)
                .Distinct().OrderBy(n => n).ToArray();
            if (names.Length == 0) return t.childCount == 0 ? "bare-leaf" : "bare-group";
            return string.Join("+", names);
        }

        // What a flattened enemy would keep: transforms with a component that does something (a renderer,
        // collider, body, FSM, audio source, light, particle system, or a script other than the build-time
        // random picks and MeshCombineStudio's markers), those that moved in the audit's frames, and those
        // any action of any state of the enemy's FSMs names (they may be moved, switched or looked up).
        // Everything else is a pivot whose pose could be folded into its children at build time.
        static readonly HashSet<string> Inert = new HashSet<string> {
            "RandomRotation", "RandomScale", "RandomPosition", "RandomMaterial", "ManualRandomiser", "MCSDynamicObject", "MaterialCombiner",
            "MeshFilter", "RandomNumberEnabled", "PrimitivePlusMaterial", "MeshCombinerData", "CachedComponents" };
        static Type ownerDefaultType, fsmGameObjectType;
        static MethodInfo ownerTarget;
        static PropertyInfo gameObjectValue;

        static HashSet<Transform> Named(Transform root)
        {
            var named = new HashSet<Transform>();
            if (fsmType == null) return named;
            if (ownerDefaultType == null)
            {
                ownerDefaultType = Type.GetType("HutongGames.PlayMaker.FsmOwnerDefault, PlayMaker");
                fsmGameObjectType = Type.GetType("HutongGames.PlayMaker.FsmGameObject, PlayMaker");
                ownerTarget = fsmProperty.PropertyType.GetMethod("GetOwnerDefaultTarget");
                gameObjectValue = fsmGameObjectType.GetProperty("Value");
            }
            foreach (Component fsm in root.GetComponentsInChildren(fsmType, true))
            {
                object machine = fsmProperty.GetValue(fsm, null);
                var states = machine == null ? null : statesProperty.GetValue(machine, null) as Array;
                if (states == null) continue;
                foreach (object state in states)
                {
                    var actions = actionsProperty.GetValue(state, null) as Array;
                    if (actions == null) continue;
                    foreach (object action in actions)
                    {
                        if (action == null) continue;
                        foreach (FieldInfo field in action.GetType().GetFields(Fields))
                        {
                            object value = field.GetValue(action);
                            if (value == null) continue;
                            GameObject target = null;
                            try
                            {
                                if (field.FieldType == ownerDefaultType) target = ownerTarget.Invoke(machine, new[] { value }) as GameObject;
                                else if (field.FieldType == fsmGameObjectType) target = gameObjectValue.GetValue(value, null) as GameObject;
                                else if (field.FieldType.IsArray && field.FieldType.GetElementType() == fsmGameObjectType)
                                    foreach (object item in (Array)value)
                                    {
                                        var g = item == null ? null : gameObjectValue.GetValue(item, null) as GameObject;
                                        if (g != null) named.Add(g.transform);
                                    }
                            }
                            catch (Exception)
                            {
                                continue;
                            }
                            if (target != null) named.Add(target.transform);
                        }
                    }
                }
            }
            return named;
        }

        static void Needed(string type, Enemy one)
        {
            HashSet<Transform> named = Named(one.Root);
            int active = 0, needed = 0, neededActive = 0, byComponent = 0, byMove = 0, byName = 0, pivotsActive = 0;
            foreach (Transform t in one.Transforms)
            {
                if (t == null) continue;
                bool on = t.gameObject.activeInHierarchy;
                if (on) active++;
                bool component = t.GetComponents<Component>().Any(c => c != null && !(c is Transform) && !Inert.Contains(c.GetType().Name));
                int moves;
                bool moved = one.Moves.TryGetValue(t, out moves) && moves > 0;
                bool isNamed = named.Contains(t);
                if (t == one.Root || component || moved || isNamed)
                {
                    needed++;
                    if (on) neededActive++;
                    if (component) byComponent++;
                    else if (moved) byMove++;
                    else byName++;
                }
                else if (on) pivotsActive++;
            }
            Debug.Log("CROW_AUDIT_NEEDED type=" + type + " transforms=" + one.Transforms.Length + " active=" + active + " needed=" + needed + " neededActive=" + neededActive +
                      " (component " + byComponent + ", moved " + byMove + ", named by an FSM " + byName + ") pivotsActive=" + pivotsActive + " namedByFsms=" + named.Count);
        }

        static void Kinds(string type, Enemy one)
        {
            var active = new Dictionary<string, int>();
            var inactive = new Dictionary<string, int>();
            foreach (Transform t in one.Transforms)
            {
                if (t == null) continue;
                Dictionary<string, int> into = t.gameObject.activeInHierarchy ? active : inactive;
                string key = Key(t);
                int n;
                into.TryGetValue(key, out n);
                into[key] = n + 1;
            }
            Debug.Log("CROW_AUDIT_KINDS type=" + type + " active: " + string.Join(" ", active.OrderByDescending(p => p.Value).Take(16).Select(p => p.Key + "=" + p.Value).ToArray()));
            Debug.Log("CROW_AUDIT_KINDS type=" + type + " inactive: " + string.Join(" ", inactive.OrderByDescending(p => p.Value).Take(16).Select(p => p.Key + "=" + p.Value).ToArray()));
            // The topmost inactive subtrees: what an enemy carries switched off (death debris, effects).
            var tops = new List<KeyValuePair<Transform, int>>();
            foreach (Transform t in one.Transforms)
                if (t != null && !t.gameObject.activeSelf && (t.parent == null || t.parent.gameObject.activeInHierarchy))
                    tops.Add(new KeyValuePair<Transform, int>(t, t.GetComponentsInChildren<Transform>(true).Length));
            Debug.Log("CROW_AUDIT_INACTIVE type=" + type + " subtrees=" + tops.Count + " transforms=" + tops.Sum(p => p.Value) + " biggest: " +
                      string.Join(" ", tops.OrderByDescending(p => p.Value).Take(10).Select(p => Perf.Path(p.Key, 3) + "=" + p.Value).ToArray()));
        }

        static void Children(string type, Transform root)
        {
            var text = new StringBuilder("CROW_AUDIT_CHILDREN type=" + type);
            foreach (Transform child in root)
            {
                Transform[] below = child.GetComponentsInChildren<Transform>(true);
                text.Append(' ').Append(child.name.Replace(' ', '_')).Append('=').Append(below.Length).Append(child.gameObject.activeSelf ? "" : "(off)")
                    .Append("[r").Append(child.GetComponentsInChildren<Renderer>().Length).Append(",c").Append(child.GetComponentsInChildren<Collider>().Length)
                    .Append(",f").Append(fsmType == null ? 0 : child.GetComponentsInChildren(fsmType).Length).Append(']');
            }
            Debug.Log(text.ToString());
            // One level deeper below the biggest child: where the transforms are.
            Transform biggest = null;
            int size = 0;
            foreach (Transform child in root)
            {
                int n = child.GetComponentsInChildren<Transform>(true).Length;
                if (n > size)
                {
                    size = n;
                    biggest = child;
                }
            }
            if (biggest == null) return;
            text = new StringBuilder("CROW_AUDIT_CHILDREN type=" + type + " in " + biggest.name.Replace(' ', '_') + ":");
            foreach (Transform child in biggest)
                text.Append(' ').Append(child.name.Replace(' ', '_')).Append('=').Append(child.GetComponentsInChildren<Transform>(true).Length).Append(child.gameObject.activeSelf ? "" : "(off)");
            Debug.Log(text.ToString());
        }

        static void Shaders(string type, Dictionary<string, int> shaders)
        {
            Debug.Log("CROW_AUDIT_SHADER type=" + type + " " + string.Join(" ", shaders.OrderByDescending(p => p.Value).Select(p => p.Key + "=" + p.Value).ToArray()));
        }

        static void Behaviours(string type, Transform root)
        {
            var counts = root.GetComponentsInChildren<MonoBehaviour>(true).Where(b => b != null).GroupBy(b => b.GetType().Name)
                .Select(g => g.Key + "=" + g.Count() + "/" + g.Count(b => b.isActiveAndEnabled)).ToArray();
            Debug.Log("CROW_AUDIT_BEHAVIOURS type=" + type + " " + string.Join(" ", counts));
        }

        // An action that runs every frame: an everyFrame (or everyframe/updateEveryFrame) field set, or a
        // kind that always does (tweens, waits, smooth moves).
        static int EveryFrame(object state)
        {
            var actions = state == null ? null : actionsProperty.GetValue(state, null) as Array;
            if (actions == null) return 0;
            int count = 0;
            foreach (object action in actions)
            {
                if (action == null || !(bool)actionEnabled.GetValue(action, null)) continue;
                if (Every(action)) count++;
            }
            return count;
        }

        static readonly Dictionary<Type, FieldInfo> everyFields = new Dictionary<Type, FieldInfo>();

        static bool Every(object action)
        {
            Type type = action.GetType();
            FieldInfo field;
            if (!everyFields.TryGetValue(type, out field))
            {
                field = type.GetField("everyFrame", Fields) ?? type.GetField("everyframe", Fields) ?? type.GetField("updateEveryFrame", Fields);
                if (field != null && field.FieldType != typeof(bool)) field = null;
                everyFields[type] = field;
            }
            if (field != null) return (bool)field.GetValue(action);
            string name = type.Name;
            return name.StartsWith("Smooth", StringComparison.Ordinal) || name == "Wait" || name.StartsWith("DOTween", StringComparison.Ordinal) ||
                   name.IndexOf("Tween", StringComparison.Ordinal) >= 0;
        }

        static readonly System.Text.RegularExpressions.Regex Looks = new System.Text.RegularExpressions.Regex(
            "Rotat|Position|Look|Translate|Scale|Tween|Smooth|Spin|Euler|Shake|Punch|Material|Colou?r|Renderer|Alpha|Fade|Flicker|Light|Particle|Audio|Sound|Animat|Blend|Texture|Visib|Activate");
        static readonly System.Text.RegularExpressions.Regex Logic = new System.Text.RegularExpressions.Regex(
            "Event|Compare|Bool|Int|Float|Wait|Random|Distance|Raycast|Fsm|Get|Set|Collision|Trigger|Force|Velocity|Kinematic|Variable|Array|String");

        static void Machines(string type, Component[] fsms, HashSet<object> running)
        {
            int looksOnly = 0, lines = 0;
            var lookNames = new List<string>();
            foreach (Component fsm in fsms)
            {
                var behaviour = fsm as Behaviour;
                if (behaviour == null) continue;
                object machine = fsmProperty.GetValue(fsm, null);
                if (machine == null) continue;
                object state = activeState.GetValue(machine, null);
                var actions = state == null ? null : actionsProperty.GetValue(state, null) as Array;
                var names = new List<string>();
                int every = 0;
                if (actions != null)
                    foreach (object action in actions)
                    {
                        if (action == null || !(bool)actionEnabled.GetValue(action, null)) continue;
                        bool e = Every(action);
                        if (e) every++;
                        names.Add(action.GetType().Name + (e ? "*" : ""));
                    }
                // What all of its states do: looks only when every action of every state moves or shows
                // something (or waits, or sends a plain event onwards).
                var states = statesProperty.GetValue(machine, null) as Array;
                bool looks = true;
                int total = 0;
                if (states != null)
                    foreach (object s in states)
                    {
                        var all = actionsProperty.GetValue(s, null) as Array;
                        if (all == null) continue;
                        foreach (object action in all)
                        {
                            if (action == null) continue;
                            total++;
                            string name = action.GetType().Name;
                            if (Looks.IsMatch(name) && !name.StartsWith("Get", StringComparison.Ordinal) && name.IndexOf("Force", StringComparison.Ordinal) < 0 &&
                                name.IndexOf("Kinematic", StringComparison.Ordinal) < 0)
                                continue;
                            if (name == "Wait" || name == "SendEvent" || name == "NextFrameEvent" || name == "RandomWait" || name == "FinishFSM") continue;
                            looks = false;
                        }
                    }
                bool isRunning = behaviour.isActiveAndEnabled && running.Contains(fsm) && state != null && !(bool)finished.GetValue(machine, null);
                if (looks && total > 0)
                {
                    looksOnly++;
                    if (isRunning) lookNames.Add(fsm.gameObject.name.Replace(' ', '_'));
                }
                if (!isRunning || lines >= 60) continue;
                lines++;
                Debug.Log("CROW_AUDIT_FSM type=" + type + " " + Perf.Path(fsm.transform, 2) + "/" + ((string)fsmName.GetValue(machine, null) ?? "").Replace(' ', '_') +
                          " state=" + ((string)stateName.GetValue(state, null) ?? "").Replace(' ', '_') + " every=" + every + " looks=" + (looks && total > 0 ? 1 : 0) +
                          " actions=" + string.Join(",", names.ToArray()));
            }
            Debug.Log("CROW_AUDIT_FSM type=" + type + " looksOnly=" + looksOnly + " running-looks=" + lookNames.Count + ": " + string.Join(",", lookNames.Distinct().Take(30).ToArray()));
        }

        // ------------------------------------------------------------------------------ draws

        sealed class Tally
        {
            public int Renderers, Draws, AddDraws, Casters, Transparent, Particles;
            public long Tris, Verts;
            public float Cover, CoverTransparent;
        }

        static void DrawReport()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                Debug.Log("CROW_AUDIT_DRAW no main camera");
                return;
            }
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
            Light[] lights = UnityEngine.Object.FindObjectsOfType<Light>().Where(l => l.isActiveAndEnabled && l.intensity > 0f).ToArray();
            int pixelLights = QualitySettings.pixelLightCount;
            var tallies = new Dictionary<string, Tally> { { "player", new Tally() }, { "other", new Tally() } };
            var shaderDraws = new Dictionary<string, int[]>();
            var materials = new HashSet<Material>();
            var textures = new HashSet<Texture>();
            int visibleAnywhere = 0, frustumNotVisible = 0, visibleOutside = 0, total = 0;
            var shadowLights = lights.Where(l => l.shadows != LightShadows.None).ToArray();
            var casterCounts = new int[shadowLights.Length];
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                total++;
                bool visible = renderer.isVisible;
                if (visible) visibleAnywhere++;
                Bounds bounds = renderer.bounds;
                for (int i = 0; i < shadowLights.Length; i++)
                    if (renderer.shadowCastingMode != ShadowCastingMode.Off && Casts(shadowLights[i], bounds, camera)) casterCounts[i]++;
                bool inView = (camera.cullingMask & (1 << renderer.gameObject.layer)) != 0 && GeometryUtility.TestPlanesAABB(planes, bounds) &&
                              renderer.shadowCastingMode != ShadowCastingMode.ShadowsOnly;
                if (!inView)
                {
                    if (visible) visibleOutside++;
                    continue;
                }
                if (!visible) frustumNotVisible++;
                int enemyIndex;
                string owner = enemyOf != null && enemyOf.TryGetValue(renderer.transform, out enemyIndex) ? "enemy:" + enemies[enemyIndex].Type :
                               IsPlayer(renderer.transform) ? "player" : "other";
                Tally tally;
                if (!tallies.TryGetValue(owner, out tally)) tallies[owner] = tally = new Tally();
                tally.Renderers++;
                if (renderer is ParticleSystemRenderer)
                {
                    tally.Particles++;
                    tally.Draws++;
                    tally.Transparent++;
                    tally.CoverTransparent += Cover(camera, bounds);
                    continue;
                }
                Mesh mesh = MeshOf(renderer);
                Material[] shared = renderer.sharedMaterials;
                int important = lights.Count(l => l.renderMode == LightRenderMode.ForcePixel && Touches(l, bounds));
                int candidates = lights.Count(l => l.renderMode != LightRenderMode.ForceVertex && Touches(l, bounds));
                int perPixel = Math.Max(important, Math.Min(pixelLights, candidates));
                if (mesh != null) tally.Verts += mesh.vertexCount;
                float cover = Cover(camera, bounds);
                for (int s = 0; s < shared.Length; s++)
                {
                    Material material = shared[s];
                    if (material == null) continue;
                    materials.Add(material);
                    if (material.mainTexture != null) textures.Add(material.mainTexture);
                    int forward = 0, add = 0;
                    for (int p = 0; p < material.passCount; p++)
                    {
                        string pass = (material.GetPassName(p) ?? "").ToUpperInvariant();
                        if (pass == "SHADOWCASTER" || pass == "META" || pass == "DEFERRED" || pass == "PREPASSBASE" || pass == "PREPASSFINAL" || pass == "MOTIONVECTORS") continue;
                        if (pass == "FORWARD_DELTA" || pass.Contains("FORWARDADD") || pass == "FORWARD_ADD") add++;
                        else forward++;
                    }
                    int adds = add > 0 ? Math.Max(0, perPixel - 1) * add : 0;
                    long tris = mesh == null ? 0 : Triangles(mesh, Math.Min(s, mesh.subMeshCount - 1));
                    tally.Draws += forward;
                    tally.AddDraws += adds;
                    tally.Tris += tris * (forward + adds);
                    bool transparent = material.renderQueue >= 2500;
                    if (transparent)
                    {
                        tally.Transparent += forward;
                        tally.CoverTransparent += cover;
                    }
                    else tally.Cover += cover;
                    string shader = material.shader == null ? "-" : material.shader.name.Replace(' ', '_');
                    int[] counts;
                    if (!shaderDraws.TryGetValue(shader, out counts)) shaderDraws[shader] = counts = new int[4];
                    counts[0] += forward;
                    counts[1] += adds;
                    counts[2] += (int)Math.Min(int.MaxValue, tris * (forward + adds));
                    counts[3] += owner.StartsWith("enemy", StringComparison.Ordinal) ? forward + adds : 0;
                }
                if (renderer.shadowCastingMode != ShadowCastingMode.Off) tally.Casters++;
            }
            Debug.Log("CROW_AUDIT_DRAW camera=" + camera.name.Replace(' ', '_') + " renderersOn=" + total + " visibleAnywhere=" + visibleAnywhere +
                      " inFrustumButNotVisible=" + frustumNotVisible + " visibleOutsideFrustum=" + visibleOutside + " materialsInView=" + materials.Count +
                      " shadersInView=" + shaderDraws.Count + " lights=" + lights.Length + " pixelLights=" + pixelLights);
            var enemyTotal = new Tally();
            foreach (KeyValuePair<string, Tally> pair in tallies.Where(p => p.Key.StartsWith("enemy", StringComparison.Ordinal)))
            {
                Tally t = pair.Value;
                enemyTotal.Renderers += t.Renderers;
                enemyTotal.Draws += t.Draws;
                enemyTotal.AddDraws += t.AddDraws;
                enemyTotal.Transparent += t.Transparent;
                enemyTotal.Particles += t.Particles;
                enemyTotal.Tris += t.Tris;
                enemyTotal.Verts += t.Verts;
                enemyTotal.Casters += t.Casters;
                enemyTotal.Cover += t.Cover;
                enemyTotal.CoverTransparent += t.CoverTransparent;
            }
            tallies["enemies"] = enemyTotal;
            foreach (KeyValuePair<string, Tally> pair in tallies.OrderBy(p => p.Key))
            {
                Tally t = pair.Value;
                Debug.Log("CROW_AUDIT_DRAW owner=" + pair.Key + " renderers=" + t.Renderers + " draws=" + t.Draws + " addDraws=" + t.AddDraws + " transparent=" + t.Transparent +
                          " particles=" + t.Particles + " tris=" + t.Tris + " verts=" + t.Verts + " casters=" + t.Casters + " cover=" + t.Cover.ToString("0.00", Inv) +
                          " coverTransparent=" + t.CoverTransparent.ToString("0.00", Inv));
            }
            foreach (KeyValuePair<string, int[]> pair in shaderDraws.OrderByDescending(p => p.Value[0] + p.Value[1]))
                Debug.Log("CROW_AUDIT_DRAW_SHADER " + pair.Key + " draws=" + pair.Value[0] + " addDraws=" + pair.Value[1] + " tris=" + pair.Value[2] + " enemyDraws=" + pair.Value[3]);
            for (int i = 0; i < shadowLights.Length; i++)
                Debug.Log("CROW_AUDIT_DRAW shadowLight=" + Perf.Path(shadowLights[i].transform, 2) + " type=" + shadowLights[i].type + " casters=" + casterCounts[i] +
                          (shadowLights[i].type == LightType.Point ? " (x6 faces at most)" : shadowLights[i].type == LightType.Directional ? " (x" + QualitySettings.shadowCascades + " cascades)" : ""));
            foreach (Light light in lights.OrderBy(l => l.type).ThenByDescending(l => l.range))
            {
                bool view = light.type == LightType.Directional || GeometryUtility.TestPlanesAABB(planes, new Bounds(light.transform.position, Vector3.one * light.range * 2f));
                Debug.Log("CROW_AUDIT_LIGHT " + Perf.Path(light.transform, 3) + " type=" + light.type + " mode=" + light.renderMode + " shadows=" + light.shadows +
                          " range=" + F(light.range) + " intensity=" + light.intensity.ToString("0.##", Inv) + " mask=0x" + light.cullingMask.ToString("x") + " inView=" + (view ? 1 : 0) +
                          " owner=" + (enemyOf != null && enemyOf.ContainsKey(light.transform) ? "enemy" : IsPlayer(light.transform) ? "player" : "other"));
            }
            var formats = textures.OfType<Texture2D>().GroupBy(t => t.format + (t.mipmapCount > 1 ? "+mips" : "") + ":" + t.filterMode)
                .Select(g => g.Key + "=" + g.Count() + "(" + (g.Sum(t => (long)t.width * t.height) / 1024) + "Kpx,max" + g.Max(t => Math.Max(t.width, t.height)) + ")").ToArray();
            Debug.Log("CROW_AUDIT_TEXTURE inView=" + textures.Count + " " + string.Join(" ", formats));
        }

        static bool Touches(Light light, Bounds bounds)
        {
            if (light.type == LightType.Directional) return true;
            if (light.type != LightType.Point && light.type != LightType.Spot) return false;
            return bounds.SqrDistance(light.transform.position) <= light.range * light.range;
        }

        static bool Casts(Light light, Bounds bounds, Camera camera)
        {
            if (light.type == LightType.Directional) return bounds.SqrDistance(camera.transform.position) <= QualitySettings.shadowDistance * QualitySettings.shadowDistance;
            return Touches(light, bounds);
        }

        // The share of the camera's picture the bounds cover on screen (a box around their corners).
        static float Cover(Camera camera, Bounds bounds)
        {
            Vector3 c = bounds.center, e = bounds.extents;
            float minX = 1f, minY = 1f, maxX = 0f, maxY = 0f;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(c.x + ((i & 1) != 0 ? e.x : -e.x), c.y + ((i & 2) != 0 ? e.y : -e.y), c.z + ((i & 4) != 0 ? e.z : -e.z));
                Vector3 v = camera.WorldToViewportPoint(corner);
                if (v.z <= 0f) return 1f;  // behind the camera: take it as the whole picture
                minX = Math.Min(minX, v.x);
                minY = Math.Min(minY, v.y);
                maxX = Math.Max(maxX, v.x);
                maxY = Math.Max(maxY, v.y);
            }
            minX = Mathf.Clamp01(minX);
            maxX = Mathf.Clamp01(maxX);
            minY = Mathf.Clamp01(minY);
            maxY = Mathf.Clamp01(maxY);
            return Math.Max(0f, maxX - minX) * Math.Max(0f, maxY - minY);
        }

        static void UiReport()
        {
            Canvas[] canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            var text = new StringBuilder("CROW_AUDIT_UI canvases=" + canvases.Length + " roots=" + canvases.Count(c => c.isRootCanvas && c.isActiveAndEnabled));
            foreach (Canvas canvas in canvases.Where(c => c.isRootCanvas && c.isActiveAndEnabled))
            {
                int shown = canvas.GetComponentsInChildren<CanvasRenderer>().Count(r => !r.cull && r.GetComponent<Behaviour>() != null);
                if (shown == 0) continue;
                text.Append(' ').Append(canvas.name.Replace(' ', '_')).Append(':').Append(canvas.renderMode).Append('=').Append(shown);
            }
            Debug.Log(text.ToString());
        }
    }

    // RenderDoc's in-application API (renderdoc_app.h, RENDERDOC_API_1_1_2), when the player runs under
    // renderdoccmd: renderdoc.dll is then loaded in the process.
    static class RenderDoc
    {
        [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetModuleHandleA(string name);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetApi(int version, out IntPtr api);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Void();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint Count();

        const int TriggerCaptureSlot = 15, GetNumCapturesSlot = 13;
        static IntPtr api;

        internal static void Trigger()
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer)
            {
                Debug.Log("CROW_RENDERDOC only on Windows players");
                return;
            }
            try
            {
                if (api == IntPtr.Zero)
                {
                    IntPtr module = GetModuleHandleA("renderdoc.dll");
                    if (module == IntPtr.Zero)
                    {
                        Debug.Log("CROW_RENDERDOC renderdoc.dll is not loaded (start the player with renderdoccmd capture)");
                        return;
                    }
                    var get = (GetApi)Marshal.GetDelegateForFunctionPointer(GetProcAddress(module, "RENDERDOC_GetAPI"), typeof(GetApi));
                    if (get(10102, out api) != 1 || api == IntPtr.Zero)
                    {
                        Debug.Log("CROW_RENDERDOC no API 1.1.2");
                        api = IntPtr.Zero;
                        return;
                    }
                }
                var trigger = (Void)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(api, TriggerCaptureSlot * IntPtr.Size), typeof(Void));
                trigger();
                var count = (Count)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(api, GetNumCapturesSlot * IntPtr.Size), typeof(Count));
                Debug.Log("CROW_RENDERDOC capture triggered at t=" + Time.realtimeSinceStartup.ToString("0.0", CultureInfo.InvariantCulture) + " frame=" + Time.frameCount +
                          " captures so far " + count());
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_RENDERDOC failed: " + exception.GetType().Name + " " + exception.Message);
            }
        }
    }
}
