using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// What the game's enemies cost per frame, and the PS Vita's cuts of it. Every enemy is an "Enemy <Type>"
// GameObject with a Rigidbody, all of it PlayMaker (Fight.cs has the anatomy).
// PS Vita defaults:
//   -crow-audio-velocity dynamic|game
//                     every AudioSource's velocityUpdateMode Dynamic (the PS Vita's default; game keeps
//                     Auto): an Auto source under a rigidbody takes its velocity (for Doppler) in every
//                     physics step (FixedUpdate.AudioFixedUpdate), and the enemies have 7-15 sources
//                     each; Dynamic takes it once a frame from the source's movement. Station Square's
//                     fight: AudioFixedUpdate 3.8 -> 2.5 ms a frame with 6 enemies awake, 32 -> 7 with 18.
//                     Set on the loaded scenes' sources (and the player's) a second after each load, and on
//                     the AudioListener: it is under Mara's rigidbody, so Auto updated it per physics step
//                     while her own sources, Dynamic, moved per frame, and the frames with no step or two
//                     gave her own sounds a Doppler shift (a 1 kHz tone on her body: steps of up to 1.9%
//                     as she moved, 0 with both per step); with both per frame they move together again.
//   -crow-corpses off|game
//                     a dead enemy's scripts off once it lies still (off; game, the default, keeps them:
//                     on the PS Vita it saved ~0.3 ms once the Blob trail's exceptions were gone). A corpse keeps its FSMs running for the rest of the area (a dead Grinner 11:
//                     leg twitches that no longer move anything, its "player in front?" check, moan and
//                     pain, 4 PlayMakerFixedUpdate proxies a physics step; a dead Blob its look-at), and
//                     stays visible. Every half second: an enemy whose Pause/Cull FSM has been in "KO"
//                     for 8 s (and whose spawn/kill FSM no longer says "currently alive"), whose enabled renderers kept their place, rotation and scale over the last
//                     check, with no sound playing (but silent ones: a dead Grinner's moan and pain loops
//                     play on at volume 0, and are stopped) and no particles alive, gets its PlayMaker FSMs (but
//                     ExplorationMode?, which removes enemies when that mode is chosen), PlayMaker's
//                     proxies and the Blob trail's scripts (BlobTrailSpawn, BlobTrailPiece,
//                     TransformLock) switched off. Its renderers, colliders and bodies stay as they are.
// Experiments, as -crow-ab / -crow-cycle / -crow-fight-cycle changes (Quality.Change), each reversible
// (they act on the enemies alive when applied; for measurements, not play):
//   eaudio=off|on     the enemies' enabled AudioSources off, and back on
//   erender=off|on    the enemies' enabled renderers off, and back on
//   etrig=off|on      the enemies' enabled trigger colliders off, and back on
//   ekin=on|off       the enemies' non-kinematic root bodies kinematic, and back (they stop moving)
//   eprobes=off|on    the enemies' renderers' light probe use off (and back to what it was)
//   erefl=off|on      the same for reflection probes (both log the scene's probe counts)
//   edebrissleep=on|off every dynamic body but the player's, the live enemies' roots and the bullets (the
//                     death debris: Grinner spikes and shards, Blob chunks, Spindle limbs) gets a sleep
//                     threshold of 0.5 instead of its own, so a settled piece sleeps; and back
//   eslice=on|off     awake enemies' FSMs (but Pause/Cull, spawn/kill, the damage FSMs and any with delayed
//                     events) update every other frame, half the enemies on even frames, half on odd
//                     (Fsm.ManualUpdate; no time correction yet: a measurement of what slicing saves)
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class EnemyCost
    {
        static bool dynamicVelocity, stopCorpses;
        static float scanAt = -1f, corpseScanAt = -1f, nextCorpseCheck;

        // From Diag.Install (every player, flags or not).
        internal static void Install(string[] args)
        {
            string mode = CrowFlags.Value(args, "-crow-audio-velocity");
            dynamicVelocity = mode == null ? Application.platform == RuntimePlatform.PSP2 : mode == "dynamic";
            stopCorpses = CrowFlags.Value(args, "-crow-corpses") == "off";
            if (!dynamicVelocity && !stopCorpses) return;
            SceneManager.sceneLoaded += (scene, loadMode) =>
            {
                scanAt = Time.realtimeSinceStartup + 1f;
                corpseScanAt = Time.realtimeSinceStartup + 3f;
            };
            Canvas.willRenderCanvases += Frame;
            Debug.Log("CROW_ENEMY audio velocity " + (dynamicVelocity ? "Dynamic" : "game") + ", corpses " + (stopCorpses ? "stopped" : "game"));
        }

        static void Frame()
        {
            if (stopCorpses) Corpses();
            if (!dynamicVelocity || scanAt < 0f || Time.realtimeSinceStartup < scanAt) return;
            scanAt = -1f;
            int count = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded) count += Dynamic(scene.GetRootGameObjects());
            }
            GameObject player = GameObject.Find("CharacterCameraPrefab(Clone)");
            if (player != null) count += Dynamic(player.scene.GetRootGameObjects());
            int listeners = 0;
            foreach (AudioListener listener in UnityEngine.Object.FindObjectsOfType<AudioListener>())
                if (listener.velocityUpdateMode != AudioVelocityUpdateMode.Dynamic)
                {
                    listener.velocityUpdateMode = AudioVelocityUpdateMode.Dynamic;
                    listeners++;
                }
            Debug.Log("CROW_ENEMY audio velocity Dynamic on " + count + " sources, " + listeners + " listeners");
        }

        static int Dynamic(GameObject[] roots)
        {
            int count = 0;
            foreach (GameObject root in roots)
                foreach (AudioSource source in root.GetComponentsInChildren<AudioSource>(true))
                    if (source.velocityUpdateMode != AudioVelocityUpdateMode.Dynamic)
                    {
                        source.velocityUpdateMode = AudioVelocityUpdateMode.Dynamic;
                        count++;
                    }
            return count;
        }

        // A new copy of an enemy (Fight's stress mode): the same defaults.
        internal static void Prepare(GameObject copy)
        {
            if (dynamicVelocity) Dynamic(new[] { copy });
            if (stopCorpses) Watch(copy);
        }

        // -crow-corpses: the enemies watched for death, found 3 s after each scene load.
        class Watched
        {
            public GameObject Root;
            public Component Cull, SpawnKill;
            public float DeadSince = -1f;
            public Matrix4x4[] Poses;
            public Renderer[] Renderers;
            public bool Stopped;
        }

        static readonly List<Watched> watched = new List<Watched>();
        static Type fsmType;
        static System.Reflection.PropertyInfo activeState;

        static void Watch(GameObject enemy)
        {
            if (fsmType == null)
            {
                fsmType = Type.GetType("PlayMakerFSM, PlayMaker");
                if (fsmType == null) return;
                activeState = fsmType.GetProperty("ActiveStateName");
            }
            Component cull = null, spawnKill = null;
            foreach (Component fsm in enemy.GetComponentsInChildren(fsmType, true))
            {
                string owner = fsm.gameObject.name;
                if (owner == "Pause/Cull" || owner == "Pause/Cull?") cull = fsm;
                if (owner == "spawn/kill") spawnKill = fsm;
            }
            if (cull != null) watched.Add(new Watched { Root = enemy, Cull = cull, SpawnKill = spawnKill });
        }

        static void Corpses()
        {
            float now = Time.realtimeSinceStartup;
            if (corpseScanAt >= 0f && now >= corpseScanAt)
            {
                corpseScanAt = -1f;
                watched.RemoveAll(w => w.Root == null);
                foreach (GameObject enemy in Enemies())
                    if (!watched.Exists(w => w.Root == enemy)) Watch(enemy);
            }
            if (now < nextCorpseCheck) return;
            nextCorpseCheck = now + 0.5f;
            foreach (Watched corpse in watched)
            {
                if (corpse.Stopped || corpse.Root == null || corpse.Cull == null) continue;
                // Dead: Pause/Cull in "KO" (which Mara's KO may set too) and spawn/kill no longer
                // "currently alive" (or switched off with the model, as a dead Grinner's is).
                bool alive = corpse.SpawnKill != null && ((Behaviour)corpse.SpawnKill).isActiveAndEnabled &&
                             (string)activeState.GetValue(corpse.SpawnKill, null) == "currently alive";
                if (alive || !corpse.Root.activeInHierarchy || !((Behaviour)corpse.Cull).isActiveAndEnabled ||
                    (string)activeState.GetValue(corpse.Cull, null) != "KO")
                {
                    corpse.DeadSince = -1f;
                    continue;
                }
                if (corpse.DeadSince < 0f) corpse.DeadSince = now;
                if (now - corpse.DeadSince < 8f) continue;
                if (!Still(corpse)) continue;
                Stop(corpse);
            }
        }

        // Whether the corpse's enabled renderers kept their poses since the last look (taken anew
        // each time), with no sound playing and no particles alive.
        static bool Still(Watched corpse)
        {
            // Silent ones don't count: a dead Grinner's moan and pain loops play on at volume 0, the
            // FSM having faded them out.
            foreach (AudioSource source in corpse.Root.GetComponentsInChildren<AudioSource>(false))
                if (source.enabled && source.isPlaying && source.volume > 0.001f) return false;
            foreach (ParticleSystem system in corpse.Root.GetComponentsInChildren<ParticleSystem>(false))
                if (system.IsAlive(false)) return false;
            var renderers = new List<Renderer>();
            foreach (Renderer renderer in corpse.Root.GetComponentsInChildren<Renderer>(false))
                if (renderer.enabled) renderers.Add(renderer);
            var poses = new Matrix4x4[renderers.Count];
            for (int i = 0; i < poses.Length; i++) poses[i] = renderers[i].transform.localToWorldMatrix;
            bool same = corpse.Renderers != null && corpse.Renderers.Length == renderers.Count;
            for (int i = 0; same && i < poses.Length; i++)
                same = corpse.Renderers[i] == renderers[i] && corpse.Poses[i] == poses[i];
            corpse.Renderers = renderers.ToArray();
            corpse.Poses = poses;
            return same;
        }

        static readonly string[] CorpseScripts = { "BlobTrailSpawn", "BlobTrailPiece", "TransformLock" };

        static void Stop(Watched corpse)
        {
            corpse.Stopped = true;
            int fsms = 0, others = 0;
            foreach (MonoBehaviour behaviour in corpse.Root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null || !behaviour.enabled) continue;
                Type type = behaviour.GetType();
                if (type == fsmType)
                {
                    if (behaviour.gameObject.name == "ExplorationMode?") continue;
                    behaviour.enabled = false;
                    fsms++;
                }
                else if (type.Name.StartsWith("PlayMaker", StringComparison.Ordinal) || Array.IndexOf(CorpseScripts, type.Name) >= 0)
                {
                    behaviour.enabled = false;
                    others++;
                }
            }
            // The silent loops left playing: with the FSMs off nothing can fade them in again.
            int silenced = 0;
            foreach (AudioSource source in corpse.Root.GetComponentsInChildren<AudioSource>(false))
                if (source.enabled && source.isPlaying && source.volume <= 0.001f)
                {
                    source.Stop();
                    silenced++;
                }
            Debug.Log("CROW_ENEMY corpse " + corpse.Root.name + ": " + fsms + " FSMs and " + others + " scripts off, " + silenced + " silent loops stopped");
        }

        // The enemies in the loaded scenes (and Fight's copies), found again at each use.
        static List<GameObject> Enemies()
        {
            var found = new List<GameObject>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (Rigidbody body in root.GetComponentsInChildren<Rigidbody>(true))
                        if (body.name.StartsWith("Enemy ", StringComparison.Ordinal)) found.Add(body.gameObject);
            }
            return found;
        }

        static readonly Dictionary<string, List<Behaviour>> offBehaviours = new Dictionary<string, List<Behaviour>>();
        static readonly List<Renderer> offRenderers = new List<Renderer>();
        static readonly List<Collider> offTriggers = new List<Collider>();

        // Quality.Change's last resort: true when the key is one of these.
        internal static bool Change(string key, string value)
        {
            if (EnemyRebuild.Change(key, value)) return true;  // eatlas=, eflatten=, edebrisout= (EnemyRebuild.cs)
            switch (key)
            {
                case "eaudio":
                    Toggle(key, value, enemy => enemy.GetComponentsInChildren<AudioSource>(false));
                    return true;
                case "erender":
                    if (value == "off")
                    {
                        foreach (GameObject enemy in Enemies())
                            foreach (Renderer renderer in enemy.GetComponentsInChildren<Renderer>(false))
                                if (renderer.enabled)
                                {
                                    renderer.enabled = false;
                                    offRenderers.Add(renderer);
                                }
                    }
                    else
                    {
                        foreach (Renderer renderer in offRenderers)
                            if (renderer != null) renderer.enabled = true;
                        offRenderers.Clear();
                    }
                    Debug.Log("CROW_ENEMY erender=" + value + " (" + offRenderers.Count + " off)");
                    return true;
                case "etrig":
                    if (value == "off")
                    {
                        foreach (GameObject enemy in Enemies())
                            foreach (Collider collider in enemy.GetComponentsInChildren<Collider>(false))
                                if (collider.enabled && collider.isTrigger)
                                {
                                    collider.enabled = false;
                                    offTriggers.Add(collider);
                                }
                    }
                    else
                    {
                        foreach (Collider collider in offTriggers)
                            if (collider != null) collider.enabled = true;
                        offTriggers.Clear();
                    }
                    Debug.Log("CROW_ENEMY etrig=" + value + " (" + offTriggers.Count + " off)");
                    return true;
                case "ekin":
                    if (value == "on")
                    {
                        foreach (GameObject enemy in Enemies())
                        {
                            var body = enemy.GetComponent<Rigidbody>();
                            if (body == null || body.isKinematic || !enemy.activeInHierarchy) continue;
                            body.isKinematic = true;
                            madeKinematic.Add(body);
                        }
                    }
                    else
                    {
                        foreach (Rigidbody body in madeKinematic)
                            if (body != null) body.isKinematic = false;
                        madeKinematic.Clear();
                    }
                    Debug.Log("CROW_ENEMY ekin=" + value + " (" + madeKinematic.Count + " kinematic)");
                    return true;
                case "eprobes":
                case "erefl":
                    Probes(key == "eprobes", value == "off");
                    return true;
                case "edebrissleep":
                    if (value == "on")
                    {
                        foreach (Rigidbody body in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
                        {
                            if (!Debris(body) || sleepSaved.ContainsKey(body)) continue;
                            sleepSaved[body] = body.sleepThreshold;
                            body.sleepThreshold = 0.5f;
                        }
                    }
                    else
                    {
                        foreach (KeyValuePair<Rigidbody, float> pair in sleepSaved)
                            if (pair.Key != null) pair.Key.sleepThreshold = pair.Value;
                        sleepSaved.Clear();
                    }
                    Debug.Log("CROW_ENEMY edebrissleep=" + value + " (" + sleepSaved.Count + " bodies)");
                    return true;
                case "eslice":
                    slicing = value == "on";
                    if (!slicing) Unslice();
                    else if (!sliceHooked)
                    {
                        sliceHooked = true;
                        Canvas.willRenderCanvases += SliceFrame;
                    }
                    Debug.Log("CROW_ENEMY eslice=" + value);
                    return true;
            }
            return false;
        }

        static readonly List<Rigidbody> madeKinematic = new List<Rigidbody>();

        // eslice: per enemy, its sliceable FSMs' ManualUpdate setters and DelayedEvents lists.
        static bool slicing, sliceHooked;
        static float sliceScanAt;
        class Sliced
        {
            public GameObject Enemy;
            public Component Cull;
            public List<Action<bool>> Manual = new List<Action<bool>>();
            public List<System.Collections.ICollection> Delayed = new List<System.Collections.ICollection>();
            public int Parity;
        }
        static readonly List<Sliced> sliced = new List<Sliced>();
        static readonly string[] NeverSliced = { "Pause/Cull", "Pause/Cull?", "spawn/kill", "ExplorationMode?", "shoot me" };

        static void SliceFrame()
        {
            if (!slicing) return;
            if (Time.realtimeSinceStartup >= sliceScanAt)
            {
                sliceScanAt = Time.realtimeSinceStartup + 2f;
                SliceScan();
            }
            int frame = Time.frameCount + 1;  // set now for the next frame's Update
            foreach (Sliced enemy in sliced)
            {
                if (enemy.Enemy == null) continue;
                bool awake = enemy.Enemy.activeInHierarchy && enemy.Cull != null &&
                             (string)activeStateProperty.GetValue(enemy.Cull, null) == "visible";
                bool skip = awake && (frame & 1) == enemy.Parity;
                for (int i = 0; i < enemy.Manual.Count; i++)
                    enemy.Manual[i](skip && (enemy.Delayed[i] == null || enemy.Delayed[i].Count == 0));
            }
        }

        static System.Reflection.PropertyInfo activeStateProperty;

        static void SliceScan()
        {
            Type fsmType = Type.GetType("PlayMakerFSM, PlayMaker");
            if (fsmType == null) return;
            activeStateProperty = fsmType.GetProperty("ActiveStateName");
            System.Reflection.PropertyInfo fsmProperty = fsmType.GetProperty("Fsm");
            var known = new HashSet<GameObject>();
            foreach (Sliced s in sliced) known.Add(s.Enemy);
            foreach (GameObject enemy in Enemies())
            {
                if (known.Contains(enemy)) continue;
                var entry = new Sliced { Enemy = enemy, Parity = sliced.Count & 1 };
                foreach (Component fsm in enemy.GetComponentsInChildren(fsmType, true))
                {
                    string owner = fsm.gameObject.name;
                    if (owner == "Pause/Cull" || owner == "Pause/Cull?") entry.Cull = fsm;
                    if (Array.IndexOf(NeverSliced, owner) >= 0 || owner.StartsWith("damage / kill", StringComparison.Ordinal)) continue;
                    object inner = fsmProperty.GetValue(fsm, null);
                    System.Reflection.PropertyInfo manual = inner.GetType().GetProperty("ManualUpdate");
                    System.Reflection.PropertyInfo delayed = inner.GetType().GetProperty("DelayedEvents");
                    if (manual == null) continue;
                    entry.Manual.Add((Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>), inner, manual.GetSetMethod()));
                    entry.Delayed.Add(delayed == null ? null : delayed.GetValue(inner, null) as System.Collections.ICollection);
                }
                sliced.Add(entry);
            }
        }

        static void Unslice()
        {
            foreach (Sliced enemy in sliced)
                foreach (Action<bool> manual in enemy.Manual) manual(false);
        }
        static readonly Dictionary<Rigidbody, float> sleepSaved = new Dictionary<Rigidbody, float>();

        // Death debris, roughly: a dynamic body that isn't the player's, an enemy's root or a bullet.
        internal static bool Debris(Rigidbody body)
        {
            if (body == null || body.isKinematic || !body.gameObject.activeInHierarchy) return false;
            string name = body.name;
            if (name.StartsWith("Enemy ", StringComparison.Ordinal) || name.EndsWith("bullet", StringComparison.OrdinalIgnoreCase)) return false;
            return body.transform.root.name.IndexOf("CharacterCameraPrefab", StringComparison.Ordinal) < 0;
        }

        static readonly Dictionary<Renderer, int> probeUsage = new Dictionary<Renderer, int>(), reflectionUsage = new Dictionary<Renderer, int>();

        static void Probes(bool light, bool off)
        {
            Dictionary<Renderer, int> saved = light ? probeUsage : reflectionUsage;
            int changed = 0;
            if (off)
            {
                foreach (GameObject enemy in Enemies())
                    foreach (Renderer renderer in enemy.GetComponentsInChildren<Renderer>(true))
                    {
                        int usage = light ? (int)renderer.lightProbeUsage : (int)renderer.reflectionProbeUsage;
                        if (usage == 0 || saved.ContainsKey(renderer)) continue;
                        saved[renderer] = usage;
                        if (light) renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                        else renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                        changed++;
                    }
            }
            else
            {
                foreach (KeyValuePair<Renderer, int> pair in saved)
                {
                    if (pair.Key == null) continue;
                    if (light) pair.Key.lightProbeUsage = (UnityEngine.Rendering.LightProbeUsage)pair.Value;
                    else pair.Key.reflectionProbeUsage = (UnityEngine.Rendering.ReflectionProbeUsage)pair.Value;
                    changed++;
                }
                saved.Clear();
            }
            LightProbes probes = LightmapSettings.lightProbes;
            Debug.Log("CROW_ENEMY " + (light ? "eprobes" : "erefl") + "=" + (off ? "off" : "on") + " on " + changed + " renderers; light probes in the scene: " +
                      (probes == null ? 0 : probes.count) + ", reflection probes: " + UnityEngine.Object.FindObjectsOfType<ReflectionProbe>().Length);
        }

        static void Toggle(string key, string value, Func<GameObject, Behaviour[]> find)
        {
            List<Behaviour> off;
            if (!offBehaviours.TryGetValue(key, out off)) offBehaviours[key] = off = new List<Behaviour>();
            if (value == "off")
            {
                foreach (GameObject enemy in Enemies())
                    foreach (Behaviour behaviour in find(enemy))
                        if (behaviour.enabled)
                        {
                            behaviour.enabled = false;
                            off.Add(behaviour);
                        }
            }
            else
            {
                foreach (Behaviour behaviour in off)
                    if (behaviour != null) behaviour.enabled = true;
                off.Clear();
            }
            Debug.Log("CROW_ENEMY " + key + "=" + value + " (" + off.Count + " off)");
        }
    }
}
