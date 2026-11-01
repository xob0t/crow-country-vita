using System.Collections.Generic;
using System.Linq;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Black backings behind the depth-layered walls (1 Nov, BACKING). asset_rules.vita_lit_bake draws every opaque
// CrowVita/Lit material a depth layer back (CrowLit.shader's CrowDepthLayerZ, up to about 8 mm at the far plane) so
// the decals and the game's black end caps laid over it win, and leaves the game's other shaders where they are.
// Some of those lie behind a lit surface instead: the Dungeon's walls have a black UnlitVertColourOpaque layer
// (a MeshCombineStudio cell) 0.5-3.5 mm behind them, facing the same way, which the original hides by that half
// millimetre; with the walls a layer back it won, and the room's walls were pure black, the pocket light's circle
// too. Here, after the scenes' merges and combines, every triangle of an opaque material that isn't CrowVita/Lit
// lying behind an opaque depth-layered CrowVita/Lit triangle by up to Near, facing the same way (its corners on that
// triangle's back side, its middle over it), is moved Back further back along its own normal, on copies of its
// vertices in a copy of its mesh: the renderer draws the copy; the triangles in front of lit surfaces (end caps)
// stay as they are. From CROW_BACKING (common.BUILD_SWITCHES): on or off.
class CrowBackingFaces : IProcessSceneWithReport
{
    const float Near = 0.02f, Back = 0.03f, Cell = 1f, Facing = 0.9f;

    public int callbackOrder
    {
        get { return 45; }  // after the merges, MeshCombineStudio's bake and strip, and StackedLayers
    }

    struct Face
    {
        public Vector3 a, b, c, normal;
    }

    public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, BuildReport report)
    {
        if (report == null || !UnityEditor.BuildPipeline.isBuildingPlayer) return;  // entering play mode
        if (BuildUtil.Switch("CROW_BACKING") != "on") return;
        var renderers = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MeshRenderer>(true))
            .Where(r => r.GetComponent<MeshFilter>() != null && r.GetComponent<MeshFilter>().sharedMesh != null).ToList();
        // The depth-layered lit faces, by Cell cells of their middles.
        var lit = new Dictionary<Vector3Int, List<Face>>();
        foreach (MeshRenderer renderer in renderers)
        {
            Mesh mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
            Matrix4x4 world = renderer.transform.localToWorldMatrix;
            Vector3[] vertices = null;
            for (int s = 0; s < mesh.subMeshCount && s < renderer.sharedMaterials.Length; s++)
            {
                if (!Layered(renderer.sharedMaterials[s])) continue;
                if (vertices == null) vertices = mesh.vertices.Select(v => world.MultiplyPoint3x4(v)).ToArray();
                int[] triangles = mesh.GetTriangles(s);
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    var face = new Face { a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]] };
                    face.normal = Vector3.Cross(face.b - face.a, face.c - face.a);
                    if (face.normal.sqrMagnitude < 1e-12f) continue;
                    face.normal.Normalize();
                    foreach (Vector3Int key in Cells(face))
                    {
                        List<Face> list;
                        if (!lit.TryGetValue(key, out list)) lit[key] = list = new List<Face>();
                        list.Add(face);
                    }
                }
            }
        }
        if (lit.Count == 0) return;
        int meshes = 0, moved = 0;
        var byMaterial = new Dictionary<string, int>();
        foreach (MeshRenderer renderer in renderers)
        {
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter.sharedMesh;
            Material[] materials = renderer.sharedMaterials;
            var subs = new List<int>();
            for (int s = 0; s < mesh.subMeshCount && s < materials.Length; s++)
                if (Plain(materials[s])) subs.Add(s);
            if (subs.Count == 0) continue;
            Matrix4x4 world = renderer.transform.localToWorldMatrix;
            Vector3[] local = mesh.vertices;
            Vector3[] vertices = local.Select(v => world.MultiplyPoint3x4(v)).ToArray();
            var behind = new Dictionary<int, List<int>>();  // submesh: the index offsets of its triangles to move
            foreach (int s in subs)
            {
                int[] triangles = mesh.GetTriangles(s);
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    var face = new Face { a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]] };
                    face.normal = Vector3.Cross(face.b - face.a, face.c - face.a);
                    if (face.normal.sqrMagnitude < 1e-12f) continue;
                    face.normal.Normalize();
                    if (!Behind(lit, face)) continue;
                    List<int> list;
                    if (!behind.TryGetValue(s, out list)) behind[s] = list = new List<int>();
                    list.Add(t);
                    int n;
                    string key = materials[s].name + "/" + materials[s].shader.name;
                    byMaterial[key] = (byMaterial.TryGetValue(key, out n) ? n : 0) + 1;
                }
            }
            if (behind.Count == 0) continue;
            // A copy of the mesh with the moved triangles on vertices of their own.
            var copy = Object.Instantiate(mesh);
            copy.name = mesh.name;
            var positions = new List<Vector3>(local);
            var copies = new List<int>();  // the source vertex of each added one
            var rewritten = new Dictionary<int, int[]>();
            Matrix4x4 toLocal = renderer.transform.worldToLocalMatrix;
            foreach (KeyValuePair<int, List<int>> pair in behind)
            {
                int[] triangles = mesh.GetTriangles(pair.Key);
                foreach (int t in pair.Value)
                {
                    Vector3 a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]];
                    Vector3 offset = toLocal.MultiplyVector(-Vector3.Cross(b - a, c - a).normalized * Back);
                    for (int k = 0; k < 3; k++)
                    {
                        copies.Add(triangles[t + k]);
                        positions.Add(local[triangles[t + k]] + offset);
                        triangles[t + k] = positions.Count - 1;
                    }
                    moved++;
                }
                rewritten[pair.Key] = triangles;
            }
            // The vertices first, then the triangles that index the added ones.
            Extend(copy, mesh, positions, copies);
            foreach (KeyValuePair<int, int[]> pair in rewritten) copy.SetTriangles(pair.Value, pair.Key);
            copy.RecalculateBounds();
            filter.sharedMesh = copy;
            meshes++;
        }
        if (moved > 0)
            Debug.Log("DOWNGRADE_BACKING " + scene.name + " meshes=" + meshes + " triangles=" + moved + " " +
                      string.Join(" ", byMaterial.OrderByDescending(kv => kv.Value).Take(8).Select(kv => kv.Key.Replace(' ', '_') + "=" + kv.Value).ToArray()));
    }

    // An opaque CrowVita/Lit material drawn a depth layer back.
    static bool Layered(Material material)
    {
        return material != null && material.shader != null && material.shader.name == "CrowVita/Lit" && material.renderQueue <= 2450 &&
               material.HasProperty("_CrowDepthLayer") && material.GetFloat("_CrowDepthLayer") < 0f;
    }

    // An opaque material of one of the game's unlit shaders (the black backings and end caps: SFB's
    // UnlitVertColourOpaque, Unity's Unlit/*), which keeps its depth. Not every shader that isn't CrowVita/Lit: the
    // first build of this moved 107,000 triangles in 1,656 meshes, the package 47 MB larger.
    static bool Plain(Material material)
    {
        return material != null && material.shader != null && material.renderQueue <= 2450 &&
               material.shader.name.IndexOf("Unlit", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static IEnumerable<Vector3Int> Cells(Face face)
    {
        Vector3 min = Vector3.Min(face.a, Vector3.Min(face.b, face.c)), max = Vector3.Max(face.a, Vector3.Max(face.b, face.c));
        for (int x = Mathf.FloorToInt(min.x / Cell); x <= Mathf.FloorToInt(max.x / Cell); x++)
            for (int y = Mathf.FloorToInt(min.y / Cell); y <= Mathf.FloorToInt(max.y / Cell); y++)
                for (int z = Mathf.FloorToInt(min.z / Cell); z <= Mathf.FloorToInt(max.z / Cell); z++)
                    yield return new Vector3Int(x, y, z);
    }

    // Behind a lit face by up to Near, facing its way, its middle over it.
    static bool Behind(Dictionary<Vector3Int, List<Face>> lit, Face face)
    {
        Vector3 middle = (face.a + face.b + face.c) / 3f;
        List<Face> list;
        if (!lit.TryGetValue(new Vector3Int(Mathf.FloorToInt(middle.x / Cell), Mathf.FloorToInt(middle.y / Cell), Mathf.FloorToInt(middle.z / Cell)), out list))
            return false;
        foreach (Face over in list)
        {
            if (Vector3.Dot(over.normal, face.normal) < Facing) continue;
            float da = Vector3.Dot(over.normal, face.a - over.a), db = Vector3.Dot(over.normal, face.b - over.a), dc = Vector3.Dot(over.normal, face.c - over.a);
            if (da > 0f || db > 0f || dc > 0f || da < -Near || db < -Near || dc < -Near) continue;
            if (Inside(over, middle)) return true;
        }
        return false;
    }

    // The point projected on the face's plane lies inside it.
    static bool Inside(Face face, Vector3 point)
    {
        float ab = Vector3.Dot(Vector3.Cross(face.b - face.a, point - face.a), face.normal);
        float bc = Vector3.Dot(Vector3.Cross(face.c - face.b, point - face.b), face.normal);
        float ca = Vector3.Dot(Vector3.Cross(face.a - face.c, point - face.c), face.normal);
        return ab >= 0f && bc >= 0f && ca >= 0f || ab <= 0f && bc <= 0f && ca <= 0f;
    }

    // The copy's vertex channels extended by the added vertices (copies of their sources, moved positions).
    static void Extend(Mesh copy, Mesh source, List<Vector3> positions, List<int> copies)
    {
        int count = source.vertexCount;
        var normals = new List<Vector3>();
        source.GetNormals(normals);
        var tangents = new List<Vector4>();
        source.GetTangents(tangents);
        var colors = new List<Color32>();
        source.GetColors(colors);
        var uvs = new List<Vector4>[4];
        for (int channel = 0; channel < 4; channel++)
        {
            uvs[channel] = new List<Vector4>();
            source.GetUVs(channel, uvs[channel]);
        }
        if (positions.Count > 65535) copy.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        copy.SetVertices(positions);
        if (normals.Count == count) copy.SetNormals(normals.Concat(copies.Select(i => normals[i])).ToList());
        if (tangents.Count == count) copy.SetTangents(tangents.Concat(copies.Select(i => tangents[i])).ToList());
        if (colors.Count == count) copy.SetColors(colors.Concat(copies.Select(i => colors[i])).ToList());
        for (int channel = 0; channel < 4; channel++)
            if (uvs[channel].Count == count) copy.SetUVs(channel, uvs[channel].Concat(copies.Select(i => uvs[channel][i])).ToList());
    }
}
