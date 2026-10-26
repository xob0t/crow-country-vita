using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// The map's scrolling on whole cells of UIPixelate's grid, and its scroll arrows' canvas (-crow-map-pan,
// -crow-map-arrows; Perf.cs installs it and takes its -crow-cycle keys).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    // The map (the player's "UI Canvas Map OFF": one overlay canvas, about 700 images with SFB's
    // UIPixelate mesh effect and 120 texts below the scrolled "map scroll me y0 / y-393") scrolls
    // vertically by MoveTowardsUI at 400 units a second while up or down is held. UIPixelate snaps each
    // image's pixel pattern to a 416x234 grid on the screen: its uv1 holds the image's offset from the
    // grid (its lower left corner's world position modulo a cell) and its size in cells, and its Update
    // dirties the image's mesh whenever its transform has changed. A scroll moves every image's
    // transform, so every image was rebuilt (OnPopulateMesh, ModifyMesh, the uv1 shim per vertex) at
    // every frame of a scroll: PostLateUpdate.PlayerUpdateCanvases 42-44 ms a frame on the PS Vita
    // (Fairy Forest, every map page known), the frame 80-83 ms. With the rule on, after the canvases'
    // update of each frame the scroller is put on the lattice of whole grid cells (in each axis) through
    // the position it had when the map was first shown (every image builds its mesh then, as the canvas
    // switches on, and again whenever it is switched on), so each image's offset from the grid, and with
    // it its mesh, stays what it was; and while the map is shown, UIPixelate.Update of the images below
    // the scroller returns at once (it starts with Skip, rewriter PatchUIPixelate: an instance ID looked
    // up, about 3 us; reading and clearing each moved transform's hasChanged instead cost about 100 us an
    // image on the PS Vita). Scrolling: the frame 82.6 -> 41.4 ms, the canvases 43.8 -> 7.2 ms, the
    // same as the map standing still. The scroll moves in steps of one grid cell (about 2 screen pixels)
    // and stops within half a cell of where the game's stops; the picture is the same. What an image's
    // Update also caught, a change of its own scale or of the camera's size while the map is shown, waits
    // for its next switch-on (none of the map's images scale while shown, but for the rare lure ring's
    // pulse; size changes rebuild through uGUI itself). CROW_MAP logs the scroller once found and, as the
    // map closes, the moves and the rebuilds left out; -crow-map-log every 5 s while it is shown.
    public static class MapPan
    {
        static bool on, hooked;
        static GameObject canvas;
        static RectTransform scroller;
        static HashSet<int> images;  // the instance IDs of the UIPixelates below the scroller
        static Type pixelate;
        static System.Reflection.FieldInfo toWidth, toHeight;
        static Vector2 origin, last, cell;
        static bool open;
        static float nextFind;
        static int moves, snapped, skipped;

        public static void Install(string[] args)
        {
            probe = CrowFlags.Has(args, "-crow-map-log");
            if (CrowFlags.On(args, "-crow-map-arrows", "canvas")) SetArrows(true);
            if (CrowFlags.On(args, "-crow-map-pan")) Set(true);
        }

        // The frame's tick and the loads, once for both switches.
        static void Hook()
        {
            if (hooked) return;
            hooked = true;
            CrowEvents.Canvases("MapPan.Tick", Tick);
            CrowEvents.Loaded("MapPan.sceneLoaded", (scene, mode) => nextFind = 0f);
        }

        internal static void Set(bool value)
        {
            on = value;
            open = false;
            Hook();
            Debug.Log("CROW_MAP pan " + (on ? "on" : "game"));
        }

        static Transform Named(Transform root, string prefix, int depth)
        {
            if (root.name.StartsWith(prefix, StringComparison.Ordinal)) return root;
            if (depth == 0) return null;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = Named(root.GetChild(i), prefix, depth - 1);
                if (found != null) return found;
            }
            return null;
        }

        static void Find()
        {
            canvas = null;
            scroller = null;
            images = null;
            GameObject holder = GameObject.Find(MaraScene.Canvases);
            if (holder == null) return;
            Transform map = null;
            for (int i = 0; i < holder.transform.childCount; i++)
                if (holder.transform.GetChild(i).name == "UI Canvas Map OFF") map = holder.transform.GetChild(i);
            if (map == null) return;
            if (pixelate == null)
            {
                foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    pixelate = assembly.GetType("com.sfbgames.general.materials.UIPixelate");
                    if (pixelate != null) break;
                }
                if (pixelate == null)
                {
                    Debug.Log("CROW_MAP no UIPixelate type: off");
                    on = false;
                    return;
                }
                const System.Reflection.BindingFlags Field = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                toWidth = pixelate.GetField("_screenToPixelatedWidth", Field);
                toHeight = pixelate.GetField("_screenToPixelatedHeight", Field);
            }
            Transform found = Named(map, "map scroll me", 6);
            if (found == null || !(found is RectTransform)) return;
            canvas = map.gameObject;
            scroller = (RectTransform)found;
            images = new HashSet<int>(scroller.GetComponentsInChildren(pixelate, true).Select(c => c.GetInstanceID()));
            arrows = Named(map, "scroll arrows", 8);
            Debug.Log("CROW_MAP scroller " + scroller.name + ": " + images.Count + " pixelated images; arrows " + (arrows == null ? "none" : arrows.name));
            Arrows();
        }

        // -crow-map-arrows canvas|game: the map's scroll arrows (their FSM bobs them up and down, a
        // DOTween loop that never stops while the map is shown) under a canvas of their own (the PS
        // Vita's default), so their motion rebuilds the batches of 10 objects instead of the whole map's
        // canvas at every frame; the nested canvas takes the map canvas's shader channels (UIPixelate
        // reads uv1 and uv2) and draws where the arrows did, in hierarchy order. game: as the game has
        // them. -crow-cycle takes maparrows=.
        static bool arrowsOn;
        static Transform arrows;
        static Canvas arrowsCanvas;

        internal static void SetArrows(bool value)
        {
            arrowsOn = value;
            Hook();
            Arrows();
            Debug.Log("CROW_MAP arrows " + (arrowsOn ? "canvas" : "game"));
        }

        static void Arrows()
        {
            if (arrows == null || canvas == null) return;
            if (arrowsOn && arrowsCanvas == null && arrows.GetComponent<Canvas>() == null)
            {
                Canvas root = canvas.GetComponent<Canvas>();
                arrowsCanvas = arrows.gameObject.AddComponent<Canvas>();
                if (root != null) arrowsCanvas.additionalShaderChannels = root.additionalShaderChannels;
            }
            else if (!arrowsOn && arrowsCanvas != null)
            {
                UnityEngine.Object.Destroy(arrowsCanvas);
                arrowsCanvas = null;
            }
        }

        // The grid cell in the scroller's parent's units, from an image that has built its mesh.
        static bool Cell()
        {
            if (toWidth == null || toHeight == null) return false;
            Component built = scroller.GetComponentsInChildren(pixelate, false).FirstOrDefault(c => (float)toHeight.GetValue(c) > 0f);
            if (built == null) return false;
            Vector3 scale = scroller.parent == null ? Vector3.one : scroller.parent.lossyScale;
            float w = (float)toWidth.GetValue(built), h = (float)toHeight.GetValue(built);
            if (w <= 0f || scale.x <= 0f || scale.y <= 0f) return false;
            cell = new Vector2(1f / w / scale.x, 1f / h / scale.y);
            return true;
        }

        static float Snap(float value, float from, float step)
        {
            return from + Mathf.Round((value - from) / step) * step;
        }

        // After the canvases' update (their meshes built for this frame's dirt), before they draw.
        static void Tick()
        {
            if (!on && !open && !arrowsOn) return;
            if (scroller == null || canvas == null)
            {
                open = false;
                if (Time.realtimeSinceStartup < nextFind) return;
                nextFind = Time.realtimeSinceStartup + 2f;
                Find();
                if (scroller == null) return;
            }
            if (!on && !open) return;
            if (!on || !canvas.activeInHierarchy)
            {
                if (open && moves > 0) Debug.Log("CROW_MAP closed: " + moves + " moves, " + snapped + " snapped, " + skipped + " rebuilds left out, cell " + cell.x.ToString("0.###", CultureInfo.InvariantCulture) + "x" + cell.y.ToString("0.###", CultureInfo.InvariantCulture));
                open = false;
                return;
            }
            Vector2 at = scroller.anchoredPosition;
            if (!open)
            {
                // The first frame shown: every image built its mesh where the scroller is now.
                if (!Cell()) return;
                open = true;
                origin = last = at;
                moves = snapped = skipped = lastSkipped = lastMoves = 0;
                return;
            }
            Place();
            Log();
        }

        // The scroller on the lattice, if it moved since the last look.
        static void Place()
        {
            Vector2 at = scroller.anchoredPosition;
            if (at == last) return;
            moves++;
            var put = new Vector2(Snap(at.x, origin.x, cell.x), Snap(at.y, origin.y, cell.y));
            if (put != at)
            {
                scroller.anchoredPosition = put;
                snapped++;
            }
            last = put;
        }

        // From the start of every UIPixelate.Update (rewriter PatchUIPixelate): true skips the rest, which
        // would dirty the image's mesh because its transform changed.
        public static bool Skip(Component effect)
        {
            if (!open) return false;
            long start = probe ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            calls++;
            bool skip = images.Contains(effect.GetInstanceID());
            if (skip) skipped++;
            if (probe) ticks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            return skip;
        }

        // -crow-map-log: every 5 s while the map is open, the calls of Skip, their time, rebuilds left
        // out, moves and the scroller's place (CROW_MAP_LOG).
        static bool probe;
        static long ticks, calls;
        static float nextLog;
        static int lastSkipped, lastMoves;

        static void Log()
        {
            float now = Time.realtimeSinceStartup;
            if (!probe || now < nextLog) return;
            nextLog = now + 5f;
            Debug.Log("CROW_MAP_LOG calls=" + calls + " skipMs=" + (ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.0", CultureInfo.InvariantCulture) +
                      " skipped=" + (skipped - lastSkipped) + " moves=" + (moves - lastMoves) + " frames=" + Time.frameCount + " y=" + scroller.anchoredPosition.y.ToString("0.00", CultureInfo.InvariantCulture));
            calls = ticks = 0;
            lastSkipped = skipped;
            lastMoves = moves;
        }
    }
}
