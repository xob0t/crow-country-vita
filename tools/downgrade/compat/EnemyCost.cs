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
// Gone: -crow-corpses (a dead enemy's scripts off once it lay still: about 0.3 ms before the 19-22 Oct cuts, never
// a default) and the cost experiments of 19 Oct (eaudio=, erender=, etrig=, ekin=, eprobes=, erefl=, edebrissleep=,
// eslice=); ekin='s result is in docs/status-log.md (Enemy physics, 19 Oct).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class EnemyCost
    {
        static bool dynamicVelocity;
        static float scanAt = -1f;

        // From Diag.Install (every player, flags or not).
        internal static void Install(string[] args)
        {
            string mode = CrowFlags.Value(args, "-crow-audio-velocity");
            dynamicVelocity = mode == null ? Application.platform == RuntimePlatform.PSP2 : mode == "dynamic";
            if (!dynamicVelocity) return;
            CrowEvents.Loaded("EnemyCost.sceneLoaded", (scene, loadMode) => scanAt = Time.realtimeSinceStartup + 1f);
            CrowEvents.Canvases("EnemyCost.Frame", Frame);
            Debug.Log("CROW_ENEMY audio velocity Dynamic");
        }

        static void Frame()
        {
            if (scanAt < 0f || Time.realtimeSinceStartup < scanAt) return;
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
