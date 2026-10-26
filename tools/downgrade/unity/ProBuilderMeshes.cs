using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// The player prefab's ProBuilder meshes, built as the build writes a scene instead of at run time.
// The export kept no mesh for the prefab's 839 ProBuilderMesh components (the game's build has
// them; the scenes have no ProBuilderMesh, ProBuilder strips them from scenes when it builds), so
// each ProBuilderMesh.Awake built its mesh from its shape the first time its object switched on:
// the first inventory open took 0.8 s on the PS Vita (421 meshes), the examine screens and the guns
// built theirs on first use, and the components kept their shapes in the managed heap (10 MB on the
// Windows player), which every garbage collection marks. Here ProBuilder's own ToMesh and Refresh
// (what Awake runs) build each mesh in the scene the build writes (CrowMara, the prefab's instance),
// with the renderer's materials as ToMesh leaves them and the MeshCollider's mesh as Refresh sets
// it. Meshes with the same data share one. They go to the GPU only, but those of a MeshCollider.
// Nothing in the game changes a ProBuilderMesh at run time (CombinedMaterial.ApplyToProBuilder, the
// only caller of its API, is an editor tool's), so then the ProBuilderMesh and ProBuilderShape
// components are left out. From CROW_PROBUILDER (common.BUILD_SWITCHES): on (the default with the PS
// Vita's scene data), bake (the meshes built and the components kept: Awake finds the mesh and builds
// nothing; the meshes stay readable, as Awake reads uv2), off. The project's prefab doesn't change.
class CrowProBuilderMeshes : UnityEditor.Build.IProcessSceneWithReport
{
    static Type proBuilderMesh, proBuilderShape;
    static MethodInfo toMesh, refresh;
    static PropertyInfo vertexCount, faceCount, preserveMesh;
    static object refreshAll;

    public int callbackOrder
    {
        get { return -10; }  // before CrowMergeScenes
    }

    public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, BuildReport report)
    {
        if (report == null || !BuildPipeline.isBuildingPlayer) return;  // entering play mode
        string mode = BuildUtil.Switch("CROW_PROBUILDER");
        if (mode == "off") return;
        if (proBuilderMesh == null)
        {
            proBuilderMesh = Type.GetType("UnityEngine.ProBuilder.ProBuilderMesh, Unity.ProBuilder");
            if (proBuilderMesh == null) return;  // a project without ProBuilder
            proBuilderShape = Type.GetType("UnityEngine.ProBuilder.Shapes.ProBuilderShape, Unity.ProBuilder");
            toMesh = proBuilderMesh.GetMethod("ToMesh", new[] { typeof(MeshTopology) });
            Type mask = Type.GetType("UnityEngine.ProBuilder.RefreshMask, Unity.ProBuilder");
            refresh = mask == null ? null : proBuilderMesh.GetMethod("Refresh", new[] { mask });
            refreshAll = mask == null ? null : Enum.Parse(mask, "All");
            vertexCount = proBuilderMesh.GetProperty("vertexCount");
            faceCount = proBuilderMesh.GetProperty("faceCount");
            preserveMesh = proBuilderMesh.GetProperty("preserveMeshAssetOnDestroy");
            if (toMesh == null || refresh == null || vertexCount == null || faceCount == null || preserveMesh == null)
                throw new InvalidOperationException("Unity.ProBuilder lacks the ProBuilderMesh API the build uses");
        }
        var components = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren(proBuilderMesh, true)).ToList();
        if (components.Count == 0) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var shared = new Dictionary<string, Mesh>();
        var collided = new HashSet<Mesh>();
        int built = 0, reused = 0, vertices = 0, failed = 0;
        foreach (Component component in components)
        {
            var filter = component.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null) continue;  // a mesh the export kept
            if ((int)vertexCount.GetValue(component, null) == 0 || (int)faceCount.GetValue(component, null) == 0) continue;  // Awake builds none
            try
            {
                toMesh.Invoke(component, new object[] { MeshTopology.Triangles });
                refresh.Invoke(component, new[] { refreshAll });
            }
            catch (Exception exception)
            {
                if (failed++ == 0) Debug.LogError("DOWNGRADE_PROBUILDER " + component.name + ": " + (exception.InnerException ?? exception));
                continue;
            }
            filter = component.GetComponent<MeshFilter>();
            Mesh mesh = filter.sharedMesh;
            var collider = component.GetComponent<MeshCollider>();
            string key = Key(mesh);
            Mesh first;
            if (shared.TryGetValue(key, out first))
            {
                filter.sharedMesh = first;
                if (collider != null && collider.sharedMesh == mesh) collider.sharedMesh = first;
                UnityEngine.Object.DestroyImmediate(mesh);
                mesh = first;
                reused++;
            }
            else
            {
                shared[key] = mesh;
                mesh.name = component.name;
                built++;
                vertices += mesh.vertexCount;
            }
            if (collider != null && collider.sharedMesh == mesh) collided.Add(mesh);
        }
        // Awake reads the mesh's uv2 when the components stay, so then the meshes stay readable.
        int unreadable = 0;
        if (mode == "on")
            foreach (Mesh mesh in shared.Values.Where(m => !collided.Contains(m)))
                if (BuildUtil.MarkUnreadable(mesh)) unreadable++;
        int dropped = 0;
        if (mode == "on" && failed == 0)
        {
            foreach (Component component in components)
            {
                preserveMesh.SetValue(component, true, null);  // its OnDestroy in the editor destroys the mesh otherwise
                UnityEngine.Object.DestroyImmediate(component);
                dropped++;
            }
            if (proBuilderShape != null)
                foreach (Component shape in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren(proBuilderShape, true)).ToList())
                {
                    UnityEngine.Object.DestroyImmediate(shape);
                    dropped++;
                }
        }
        Debug.Log("DOWNGRADE_PROBUILDER " + scene.name + " mode=" + mode + " components=" + components.Count + " meshes=" + built +
                  " shared=" + reused + " vertices=" + vertices + " unreadable=" + unreadable + " collider=" + collided.Count +
                  " dropped=" + dropped + " failed=" + failed + " ms=" + clock.ElapsedMilliseconds);
    }

    // The mesh's data: vertex channels, sub-meshes and their indices.
    static string Key(Mesh mesh)
    {
        var data = new System.IO.MemoryStream();
        var writer = new System.IO.BinaryWriter(data);
        foreach (Vector3 v in mesh.vertices) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
        writer.Write(-1);
        foreach (Vector3 v in mesh.normals) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
        writer.Write(-1);
        foreach (Vector4 v in mesh.tangents) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); writer.Write(v.w); }
        writer.Write(-1);
        foreach (Color32 c in mesh.colors32) { writer.Write(c.r); writer.Write(c.g); writer.Write(c.b); writer.Write(c.a); }
        for (int channel = 0; channel < 4; channel++)
        {
            var uvs = new List<Vector4>();
            mesh.GetUVs(channel, uvs);
            writer.Write(-1);
            foreach (Vector4 v in uvs) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); writer.Write(v.w); }
        }
        writer.Write(mesh.subMeshCount);
        for (int i = 0; i < mesh.subMeshCount; i++)
        {
            writer.Write((int)mesh.GetTopology(i));
            foreach (int index in mesh.GetIndices(i)) writer.Write(index);
            writer.Write(-1);
        }
        writer.Flush();
        using (var md5 = System.Security.Cryptography.MD5.Create())
            return Convert.ToBase64String(md5.ComputeHash(data.ToArray())) + data.Length;
    }
}
