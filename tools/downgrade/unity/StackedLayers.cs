using System.Collections.Generic;
using System.Linq;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// See-through layers drawn more than once (31 Oct, POOLFIGHT). Pool's liquid has three edge rings: one mesh and one
// see-through material (white, alpha 0.14) at the same place, scaled 3.032, 3.034 and 3.091. Each is a lit, blended
// layer over the pool, which fills the screen in the boss fight: the three cost the GPU about 8 ms of its 39 (hiding
// two of them, 4 ms). Blending one colour k times over the same pixels is the same as blending it once with alpha
// 1 - (1 - a)^k, so here renderers with the same mesh and one see-through material, under the same parent at the same
// local position and rotation, with scales within StackScale of the largest, become one: the largest keeps drawing,
// with a copy of the material whose colour has that alpha, and the others are switched off (their objects stay, for
// the scripts that name them). The only difference is at the edges: the largest's band beyond the others (2% of
// Pool's rings) has the stack's alpha, not one layer's. From CROW_STACK_FOLD (common.BUILD_SWITCHES): on or off.
class CrowStackedLayers : IProcessSceneWithReport
{
    const float StackScale = 0.025f, Same = 1e-3f;

    public int callbackOrder
    {
        get { return 40; }
    }

    public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, BuildReport report)
    {
        if (report == null || !UnityEditor.BuildPipeline.isBuildingPlayer) return;  // entering play mode
        if (BuildUtil.Switch("CROW_STACK_FOLD") != "on") return;
        var layers = scene.GetRootGameObjects()
            .SelectMany(r => r.GetComponentsInChildren<MeshRenderer>(true))
            .Where(r => r.enabled && r.sharedMaterials.Length == 1 && r.sharedMaterial != null && r.sharedMaterial.renderQueue > 2500 &&
                        r.sharedMaterial.HasProperty("_Color") && r.GetComponent<MeshFilter>() != null &&
                        r.GetComponent<MeshFilter>().sharedMesh != null)
            .ToList();
        int folded = 0, off = 0;
        foreach (var group in layers.GroupBy(r => new { mesh = r.GetComponent<MeshFilter>().sharedMesh, material = r.sharedMaterial, parent = r.transform.parent }))
        {
            var left = group.ToList();
            while (left.Count > 1)
            {
                MeshRenderer first = left[0];
                var stack = left.Where(r => Close(r.transform.localPosition, first.transform.localPosition) &&
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

    static bool Close(Vector3 a, Vector3 b)
    {
        return (a - b).sqrMagnitude < Same * Same;
    }

    // Each axis of a within StackScale of b's, with the same signs.
    static bool Within(Vector3 a, Vector3 b)
    {
        for (int i = 0; i < 3; i++)
            if (Mathf.Sign(a[i]) != Mathf.Sign(b[i]) || Mathf.Abs(a[i] - b[i]) > StackScale * Mathf.Abs(b[i])) return false;
        return true;
    }
}
