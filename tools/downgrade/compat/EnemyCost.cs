using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// What the game's enemies cost per frame, and the PS Vita's cuts of it. Every enemy is an "Enemy <Type>"
// GameObject with a Rigidbody (EnemyNames), all of it PlayMaker.
// PS Vita defaults:
//   -crow-audio-velocity dynamic|game
//                     every AudioSource's velocityUpdateMode Dynamic (the PS Vita's default; game keeps
//                     Auto): an Auto source under a rigidbody takes its velocity (for Doppler) in every
//                     physics step (FixedUpdate.AudioFixedUpdate), and the enemies have 7-15 sources
//                     each; Dynamic takes it once a frame from the source's movement. Station Square's
//                     fight before the 19-22 Oct cuts: AudioFixedUpdate 3.8 -> 2.5 ms a frame with 6 enemies
//                     awake, 32 -> 7 with 18.
//                     Set on the loaded scenes' sources (and the player's) a second after each load, and on
//                     the AudioListener: it is under Mara's rigidbody, so Auto updated it per physics step
//                     while her own sources, Dynamic, moved per frame, and the frames with no step or two
//                     gave her own sounds a Doppler shift (a 1 kHz tone on her body: steps of up to 1.9%
//                     as she moved, 0 with both per step); with both per frame they move together again.
//   -crow-corpses off|game
//                     a dead enemy's scripts off once it lies still (off; game, the default, keeps them:
//                     on the PS Vita it saved ~0.3 ms once the Blob trail's exceptions were gone, before
//                     the 19-22 Oct cuts). A corpse keeps its FSMs running for the rest of the area (a dead
//                     Grinner 11: leg twitches that no longer move anything, its "player in front?" check, moan and
//                     pain, 4 PlayMakerFixedUpdate proxies a physics step; a dead Blob its look-at), and
//                     stays visible. Every half second: an enemy whose Pause/Cull FSM has been in "KO"
//                     for 8 s (and whose spawn/kill FSM no longer says "currently alive"), whose enabled
//                     renderers kept their place, rotation and scale over the last check, with no sound playing
//                     (but silent ones: a dead Grinner's moan and pain loops play on at volume 0, and are
//                     stopped) and no particles alive, gets its PlayMaker FSMs (but
//                     ExplorationMode?, which removes enemies when that mode is chosen), PlayMaker's
//                     proxies and the Blob trail's scripts (BlobTrailSpawn, BlobTrailPiece,
//                     TransformLock) switched off. Its renderers, colliders and bodies stay as they are.
// The cost experiments of 19 Oct (eaudio=, erender=, etrig=, ekin=, eprobes=, erefl=, edebrissleep=, eslice=) are
// gone; ekin='s result is in docs/status-log.md (Enemy physics, 19 Oct).
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
            CrowEvents.Loaded("EnemyCost.sceneLoaded", (scene, loadMode) =>
            {
                scanAt = Time.realtimeSinceStartup + 1f;
                corpseScanAt = Time.realtimeSinceStartup + 3f;
            });
            CrowEvents.Canvases("EnemyCost.Frame", Frame);
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
            GameObject player = MaraScene.FindPlayer();
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
                fsmType = GameTypes.PlayMakerFsm;
                if (fsmType == null) return;
                activeState = fsmType.GetProperty("ActiveStateName");
            }
            Component cull = null, spawnKill = null;
            foreach (Component fsm in enemy.GetComponentsInChildren(fsmType, true))
            {
                string owner = fsm.gameObject.name;
                if (EnemyNames.IsCull(owner)) cull = fsm;
                if (owner == EnemyNames.SpawnKill) spawnKill = fsm;
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
                        if (EnemyNames.Named(body.name)) found.Add(body.gameObject);
            }
            return found;
        }

        // Death debris, roughly: a dynamic body that isn't the player's, an enemy's root or a bullet.
        internal static bool Debris(Rigidbody body)
        {
            if (body == null || body.isKinematic || !body.gameObject.activeInHierarchy) return false;
            string name = body.name;
            if (EnemyNames.Named(name) || FixedStep.IsBullet(name)) return false;
            return !MaraScene.IsPlayerRoot(body.transform.root.gameObject);
        }
    }
}
