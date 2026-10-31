using System.Collections.Generic;
using System.Linq;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

// See-through layers drawn more than once (31 Oct, POOLFIGHT). Pool's liquid has three edge rings: one mesh and one
// see-through material (white, alpha 0.14) at the same place, scaled 3.032, 3.034 and 3.091. Each is a lit, blended
// layer over the pool, which fills the screen in the boss fight: the three cost the GPU about 8 ms of its 39 (hiding
// two of them, 4 ms). Blending one colour k times over the same pixels is the same as blending it once with alpha
// 1 - (1 - a)^k when it blends SrcAlpha, OneMinusSrcAlpha (the Standard shader's Fade mode). So here active renderers
// with the same mesh and one such material, under the same parent at the same local rotation and position (within
// Same of their size: Pool's rings are 7 mm apart in height, 3 m across), with scales within StackScale of the
// largest, become one: the largest keeps drawing, with a copy of the material whose colour has that alpha, and the
// others are switched off (their objects stay). A layer a behaviour of the scene (PlayMaker FSMs and their templates
// included) references or names, or its material, and one under an Animator or Animation, stays as it is: a script
// or a clip could switch or fade it. The only difference is at the edges: the largest's band beyond the others (2%
// of Pool's rings) has the stack's alpha, not one layer's. Not in the scenes that are no area. From CROW_STACK_FOLD
// (common.BUILD_SWITCHES): on or off.
class CrowStackedLayers : IProcessSceneWithReport
{
    const float StackScale = 0.025f, Same = 0.01f;

    public int callbackOrder
    {
        get { return 40; }
    }

    public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, BuildReport report)
    {
        if (report == null || !UnityEditor.BuildPipeline.isBuildingPlayer) return;  // entering play mode
        if (BuildUtil.Switch("CROW_STACK_FOLD") != "on" || BuildUtil.SkippedScenes.Contains(scene.name)) return;
        var layers = scene.GetRootGameObjects()
            .SelectMany(r => r.GetComponentsInChildren<MeshRenderer>(true))
            .Where(r => r.enabled && r.gameObject.activeInHierarchy && r.sharedMaterials.Length == 1 && Blended(r.sharedMaterial) &&
                        r.sharedMaterial.HasProperty("_Color") && r.GetComponent<MeshFilter>() != null &&
                        r.GetComponent<MeshFilter>().sharedMesh != null &&
                        !r.GetComponentsInParent<Behaviour>(true).Any(b => b is Animator || b is Animation))
            .ToList();
        if (layers.Count < 2) return;
        var referenced = new HashSet<Object>();
        var names = new HashSet<string>();
        var walked = new HashSet<Object>();
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour != null) BuildUtil.Walk(behaviour, referenced, names, true, walked);  // null: a missing script
        layers.RemoveAll(r => referenced.Contains(r) || referenced.Contains(r.gameObject) || referenced.Contains(r.transform) ||
                              referenced.Contains(r.GetComponent<MeshFilter>()) || referenced.Contains(r.sharedMaterial) ||
                              names.Contains(r.name));
        int folded = 0, off = 0;
        foreach (var group in layers.GroupBy(r => new { mesh = r.GetComponent<MeshFilter>().sharedMesh, material = r.sharedMaterial, parent = r.transform.parent }))
        {
            var left = group.ToList();
            while (left.Count > 1)
            {
                MeshRenderer first = left[0];
                float size = Mathf.Max(Mathf.Abs(first.transform.localScale.x), Mathf.Abs(first.transform.localScale.y), Mathf.Abs(first.transform.localScale.z));
                var stack = left.Where(r => (r.transform.localPosition - first.transform.localPosition).magnitude <= Same * size &&
                                            Quaternion.Angle(r.transform.localRotation, first.transform.localRotation) < 0.1f).ToList();
                left.RemoveAll(stack.Contains);
                MeshRenderer largest = stack.OrderByDescending(r => r.transform.localScale.sqrMagnitude).First();
                stack = stack.Where(r => Within(r.transform.localScale, largest.transform.localScale)).ToList();
                if (stack.Count < 2) continue;
                var material = new Material(largest.sharedMaterial) { name = largest.sharedMaterial.name + " x" + stack.Count };
                Color color = material.color;
                color.a = 1f - Mathf.Pow(1f - color.a, stack.Count);
                material.color = color;
                largest.sharedMaterial = material;
                foreach (MeshRenderer other in stack.Where(r => r != largest))
                {
                    other.enabled = false;
                    off++;
                }
                folded++;
            }
        }
        if (folded > 0) Debug.Log("DOWNGRADE_STACK " + scene.name + " folded=" + folded + " off=" + off);
    }

    static bool Blended(Material material)
    {
        return material != null && material.HasProperty("_SrcBlend") && material.HasProperty("_DstBlend") &&
               material.GetFloat("_SrcBlend") == (float)BlendMode.SrcAlpha && material.GetFloat("_DstBlend") == (float)BlendMode.OneMinusSrcAlpha;
    }

    // Each axis of a within StackScale of b's, with the same signs.
    static bool Within(Vector3 a, Vector3 b)
    {
        for (int i = 0; i < 3; i++)
            if (Mathf.Sign(a[i]) != Mathf.Sign(b[i]) || Mathf.Abs(a[i] - b[i]) > StackScale * Mathf.Abs(b[i])) return false;
        return true;
    }
}
