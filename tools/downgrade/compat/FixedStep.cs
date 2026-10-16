using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// The physics step. The game steps physics every 0.02 s (Time.fixedDeltaTime), so a 30 fps frame runs
// 1.67 steps and a 54 ms fight frame 2.7, each with its physics, the FSMs' FixedUpdate actions and the
// audio's fixed update. -crow-fixed-dt <s> (or <fps> above 1: 30, 25; game for 0.02) steps physics that
// often instead, with the game kept as it moves at 0.02 (the PS Vita's default: 30, one step a frame;
// Station Square's fight 54.1 -> 49.2 ms a frame, Mara's walk 0.990 m/s at both):
//   - Impulses applied in every step (PlayMaker AddForce with forceMode Impulse or VelocityChange and
//     everyFrame, run in FixedUpdate: Mara's walk, a Blob's move) are scaled so a body under them reaches
//     the speed it reaches at 0.02, ground friction included (the rewriter sends AddForce's Rigidbody
//     calls here, AddForce).
//   - Each rigidbody's drag and angular drag are set so a velocity decays as fast per second as with the
//     game's drag at 0.02 (PhysX damps by 1 - drag * step each step), at each scene load, for the player,
//     and every second for bodies not seen before (Adjust).
//   - The bullets (handgun, shotgun, magnum, flame bullets: 18 m/s, 0.6 m a step at 1/30) detect
//     collisions continuously, so they don't pass through an enemy's small colliders
//     (-crow-bullet-ccd off leaves them discrete).
// One-off impulses, set velocities (the Grinners' walk), gravity and forces are already step-independent.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class FixedStep
    {
        public const float GameStep = 0.02f;
        static float step = GameStep;
        static bool on, compensate = true, bulletCcd = true;
        // Per body: the game's drag and angular drag, before Adjust.
        static readonly Dictionary<Rigidbody, Vector2> gameDrag = new Dictionary<Rigidbody, Vector2>();
        static float nextScan = -1f;
        static bool fullScan;

        // -crow-cycle / -crow-fight-cycle / -crow-ab: fixeddt=<s or fps> (game for 0.02), as -crow-fixed-dt.
        internal static bool Change(string key, string value)
        {
            if (key != "fixeddt") return false;
            float number;
            if (value == "game") number = GameStep;
            else if (!float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) || number <= 0f)
                return true;
            Hook();
            Set(number > 1f ? 1f / number : number);
            return true;
        }

        static bool hooked;

        static void Hook()
        {
            if (hooked) return;
            hooked = true;
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                nextScan = Time.realtimeSinceStartup + 0.5f;
                fullScan = true;
            };
            Canvas.willRenderCanvases += Frame;
        }

        // From Diag.Install (every player, flags or not).
        internal static void Install(string[] args)
        {
            int flag = Array.IndexOf(args, "-crow-fixed-dt");
            string value = flag >= 0 && flag + 1 < args.Length ? args[flag + 1] : null;
            int ccd = Array.IndexOf(args, "-crow-bullet-ccd");
            bulletCcd = !(ccd >= 0 && ccd + 1 < args.Length && args[ccd + 1] == "off");
            // The PS Vita's default: 30 steps a second, one a frame at its 30 fps.
            if (value == null && Application.platform == RuntimePlatform.PSP2) value = "30";
            float number;
            if (value == null || value == "game" || !float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out number) || number <= 0f) return;
            Hook();
            Set(number > 1f ? 1f / number : number);
        }

        // The step from now on (also Fight's physics test).
        internal static void Set(float seconds, bool keepGame = true)
        {
            step = seconds;
            compensate = keepGame;
            on = Mathf.Abs(step - GameStep) > 1e-6f && keepGame;
            Time.fixedDeltaTime = step;
            // The bodies seen so far get their drags for the new step, from the game's.
            var known = new List<Rigidbody>(gameDrag.Keys);
            foreach (Rigidbody body in known)
            {
                if (body == null)
                {
                    gameDrag.Remove(body);
                    continue;
                }
                Vector2 game = gameDrag[body];
                body.drag = on ? Drag(game.x) : game.x;
                body.angularDrag = on ? Drag(game.y) : game.y;
            }
            if (on && Time.timeSinceLevelLoad > 1f) fullScan = true;
            Debug.Log("CROW_FIXED step " + step.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + " s" + (on ? " (game kept as at 0.02)" : compensate ? "" : " (uncompensated)"));
        }

        static void Frame()
        {
            if (Time.fixedDeltaTime != step) Time.fixedDeltaTime = step;  // a game script setting it back
            float now = Time.realtimeSinceStartup;
            if ((now < nextScan && !fullScan) || !on) return;
            nextScan = now + 1f;
            if (fullScan)
            {
                fullScan = false;
                // After a load: every body, inactive ones too (templates the game clones later).
                int count = 0;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded) continue;
                    foreach (GameObject root in scene.GetRootGameObjects())
                        foreach (Rigidbody body in root.GetComponentsInChildren<Rigidbody>(true)) count += Adjust(body);
                }
                GameObject player = GameObject.Find("CharacterCameraPrefab(Clone)");
                if (player != null)
                    foreach (GameObject root in player.scene.GetRootGameObjects())
                        foreach (Rigidbody body in root.GetComponentsInChildren<Rigidbody>(true)) count += Adjust(body);
                Debug.Log("CROW_FIXED adjusted " + count + " bodies");
            }
            foreach (Rigidbody body in UnityEngine.Object.FindObjectsOfType<Rigidbody>()) Adjust(body);
        }

        // A body's drags for the step (once per body); bullets continuous. 1 when it was new.
        internal static int Adjust(Rigidbody body)
        {
            if (!on || body == null || gameDrag.ContainsKey(body)) return 0;
            gameDrag[body] = new Vector2(body.drag, body.angularDrag);
            body.drag = Drag(body.drag);
            body.angularDrag = Drag(body.angularDrag);
            if (bulletCcd && step > GameStep && body.name.EndsWith("bullet", StringComparison.OrdinalIgnoreCase) && !body.isKinematic)
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            return 1;
        }

        // The drag that damps as much per second at this step as the game's at 0.02: each step multiplies
        // a velocity by 1 - drag * step.
        static float Drag(float drag)
        {
            float keep = 1f - drag * GameStep;
            if (drag <= 0f || keep <= 0f) return drag;
            return (1f - Mathf.Pow(keep, step / GameStep)) / step;
        }

        // How much more each step's impulse must be for the same speed: under an impulse J every step and
        // damping k a step, the speed settles at J/m * k / (1 - k).
        // A body walking on the ground also loses mu * g * step of speed a step to friction (Mara: 0.4 *
        // 30 * 0.02 = 0.24 m/s against a walk of about 1 m/s), which a longer step doesn't scale like
        // the impulse: with friction the speed settles at (J/m - mu g h) * k / (1 - k), and the gain
        // makes that equal at both steps. Grounded: a ray from the centre of mass down meets a collider
        // within the body's own reach (its colliders' bounds) and 5 cm.
        static float Gain(Rigidbody body, Vector3 force, ForceMode mode)
        {
            Vector2 game;
            float drag = gameDrag.TryGetValue(body, out game) ? game.x : body.drag;
            float keep = 1f - drag * GameStep;
            if (drag <= 0f || keep <= 0f || keep >= 1f) return step / GameStep;
            float now = Mathf.Pow(keep, step / GameStep);
            float plain = keep / (1f - keep) / (now / (1f - now));
            float mu = GroundFriction(body);
            if (mu <= 0f) return plain;
            float impulse = force.magnitude / (mode == ForceMode.Impulse ? body.mass : 1f);  // speed it adds a step at 0.02
            float g = -Physics.gravity.y;
            float speed = (impulse - mu * g * GameStep) * keep / (1f - keep);
            if (impulse <= 0f || speed <= 0f) return plain;  // friction holds it at 0.02 too
            return (speed * (1f - now) / now + mu * g * step) / impulse;
        }

        static readonly Dictionary<Rigidbody, Collider> ownCollider = new Dictionary<Rigidbody, Collider>();

        // The combined dynamic friction between the body's (first solid) collider and the ground under it;
        // 0 when it isn't on the ground.
        static float GroundFriction(Rigidbody body)
        {
            Collider own;
            if (!ownCollider.TryGetValue(body, out own))
            {
                own = null;
                foreach (Collider collider in body.GetComponentsInChildren<Collider>(false))
                    if (!collider.isTrigger && collider.enabled && collider.attachedRigidbody == body)
                    {
                        own = collider;
                        break;
                    }
                ownCollider[body] = own;
            }
            if (own == null || !own.enabled) return 0f;
            Vector3 centre = body.worldCenterOfMass;
            float reach = centre.y - own.bounds.min.y + 0.05f;
            RaycastHit ground;
            if (!Physics.Raycast(centre, Vector3.down, out ground, reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return 0f;
            return Combine(own.sharedMaterial, ground.collider.sharedMaterial);
        }

        // Unity's friction combine: the mode of higher priority wins (Maximum, Multiply, Minimum, Average);
        // no material is 0.6 and Average.
        static float Combine(PhysicMaterial a, PhysicMaterial b)
        {
            float fa = a == null ? 0.6f : a.dynamicFriction, fb = b == null ? 0.6f : b.dynamicFriction;
            PhysicMaterialCombine ca = a == null ? PhysicMaterialCombine.Average : a.frictionCombine;
            PhysicMaterialCombine cb = b == null ? PhysicMaterialCombine.Average : b.frictionCombine;
            PhysicMaterialCombine mode = (PhysicMaterialCombine)Math.Max((int)Priority(ca), (int)Priority(cb));
            mode = FromPriority((int)mode);
            switch (mode)
            {
                case PhysicMaterialCombine.Maximum: return Mathf.Max(fa, fb);
                case PhysicMaterialCombine.Multiply: return fa * fb;
                case PhysicMaterialCombine.Minimum: return Mathf.Min(fa, fb);
                default: return (fa + fb) / 2f;
            }
        }

        static int Priority(PhysicMaterialCombine combine)
        {
            switch (combine)
            {
                case PhysicMaterialCombine.Maximum: return 3;
                case PhysicMaterialCombine.Multiply: return 2;
                case PhysicMaterialCombine.Minimum: return 1;
                default: return 0;
            }
        }

        static PhysicMaterialCombine FromPriority(int priority)
        {
            return priority == 3 ? PhysicMaterialCombine.Maximum : priority == 2 ? PhysicMaterialCombine.Multiply :
                priority == 1 ? PhysicMaterialCombine.Minimum : PhysicMaterialCombine.Average;
        }

        static bool Repeated(ForceMode mode, bool everyFrame)
        {
            return on && everyFrame && Time.inFixedTimeStep && (mode == ForceMode.Impulse || mode == ForceMode.VelocityChange);
        }

        // PlayMaker AddForce's Rigidbody calls (rewriter PatchAddForce), with the action's everyFrame.
        public static void AddForce(Rigidbody body, Vector3 force, ForceMode mode, bool everyFrame)
        {
            if (Repeated(mode, everyFrame)) force *= Gain(body, force, mode);
            body.AddForce(force, mode);
        }

        public static void AddRelativeForce(Rigidbody body, Vector3 force, ForceMode mode, bool everyFrame)
        {
            if (Repeated(mode, everyFrame)) force *= Gain(body, force, mode);
            body.AddRelativeForce(force, mode);
        }

        public static void AddForceAtPosition(Rigidbody body, Vector3 force, Vector3 position, ForceMode mode, bool everyFrame)
        {
            if (Repeated(mode, everyFrame)) force *= Gain(body, force, mode);
            body.AddForceAtPosition(force, position, mode);
        }
    }
}
