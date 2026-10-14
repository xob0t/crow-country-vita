using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.U2D;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// Unity 2020-2022 types and members that 2018.2 lacks, by the naming convention in Compat2018.cs.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public enum UnityEngine_FindObjectsInactive { Exclude = 0, Include = 1 }
    public enum UnityEngine_FindObjectsSortMode { None = 0, InstanceID = 1 }
    public enum UnityEngine_Rendering_SinglePassStereoMode { None = 0, SideBySide = 1, Instancing = 2, Multiview = 3 }

    public static class UnityEngine_Object
    {
        public static T FindFirstObjectByType<T>() where T : Object
        {
            return Object.FindObjectOfType<T>();
        }

        public static T[] FindObjectsOfType<T>(bool includeInactive) where T : Object
        {
            return includeInactive ? SceneObjects<T>() : Object.FindObjectsOfType<T>();
        }

        public static T[] FindObjectsByType<T>(UnityEngine_FindObjectsInactive inactive, UnityEngine_FindObjectsSortMode sort) where T : Object
        {
            T[] found = FindObjectsOfType<T>(inactive == UnityEngine_FindObjectsInactive.Include);
            if (sort == UnityEngine_FindObjectsSortMode.InstanceID)
                Array.Sort(found, (a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
            return found;
        }

        // FindObjectsOfTypeAll also returns assets and hidden objects; keep loaded scene objects.
        static T[] SceneObjects<T>() where T : Object
        {
            return Resources.FindObjectsOfTypeAll<T>().Where(o =>
            {
                if ((o.hideFlags & (HideFlags.NotEditable | HideFlags.HideAndDontSave)) != 0) return false;
                var component = o as Component;
                var gameObject = component != null ? component.gameObject : o as GameObject;
                return gameObject == null || gameObject.scene.IsValid();
            }).ToArray();
        }
    }

    public static class UnityEngine_RenderTexture
    {
        public static RenderTexture ctor(int width, int height, int depth, RenderTextureFormat format, int mipCount)
        {
            var texture = new RenderTexture(width, height, depth, format);
            if (mipCount > 1) texture.useMipMap = true;
            return texture;
        }
    }

    public static class UnityEngine_RenderTextureDescriptor
    {
        public static void set_mipCount(ref RenderTextureDescriptor descriptor, int value)
        {
            descriptor.useMipMap = value > 1;
        }
    }

    public static class UnityEngine_Texture
    {
        // Unity 2018 has no readable flag; reading a non-readable texture throws.
        public static bool get_isReadable(Texture texture)
        {
            var texture2D = texture as Texture2D;
            if (texture2D == null) return false;
            try { texture2D.GetPixel(0, 0); return true; }
            catch (UnityException) { return false; }
        }
    }

    public static class UnityEngine_ParticleSystem
    {
        public static void SetParticles(ParticleSystem system, ParticleSystem.Particle[] particles, int size, int offset)
        {
            if (offset == 0) { system.SetParticles(particles, size); return; }
            var current = new ParticleSystem.Particle[Math.Max(system.particleCount, offset + size)];
            int count = system.GetParticles(current);
            Array.Copy(particles, 0, current, offset, size);
            system.SetParticles(current, Math.Max(count, offset + size));
        }
    }

    public static class UnityEngine_U2D_SpriteAtlasManager
    {
        static readonly Dictionary<Action<string, Action<SpriteAtlas>>, SpriteAtlasManager.RequestAtlasCallback> callbacks =
            new Dictionary<Action<string, Action<SpriteAtlas>>, SpriteAtlasManager.RequestAtlasCallback>();

        public static void add_atlasRequested(Action<string, Action<SpriteAtlas>> handler)
        {
            if (handler == null || callbacks.ContainsKey(handler)) return;
            SpriteAtlasManager.RequestAtlasCallback callback = (tag, action) => handler(tag, action);
            callbacks[handler] = callback;
            SpriteAtlasManager.atlasRequested += callback;
        }

        public static void remove_atlasRequested(Action<string, Action<SpriteAtlas>> handler)
        {
            SpriteAtlasManager.RequestAtlasCallback callback;
            if (handler == null || !callbacks.TryGetValue(handler, out callback)) return;
            SpriteAtlasManager.atlasRequested -= callback;
            callbacks.Remove(handler);
        }
    }

    public static class UnityEngine_Experimental_Rendering_GraphicsFormatUtility
    {
        public static GraphicsFormat GetGraphicsFormat(RenderTextureFormat format, RenderTextureReadWrite readWrite)
        {
            bool sRGB = readWrite == RenderTextureReadWrite.sRGB ||
                (readWrite == RenderTextureReadWrite.Default && QualitySettings.activeColorSpace == ColorSpace.Linear);
            return GraphicsFormatUtility.GetGraphicsFormat(format, sRGB);
        }
    }

    public static class UnityEngine_Rendering_CommandBuffer
    {
        public static void SetSinglePassStereo(CommandBuffer buffer, UnityEngine_Rendering_SinglePassStereoMode mode) { }
    }

    public static class UnityEngine_Rendering_GraphicsSettings
    {
        public static RenderPipelineAsset get_currentRenderPipeline()
        {
            return GraphicsSettings.renderPipelineAsset;
        }
    }

    public static class UnityEngine_SystemInfo
    {
        public static bool get_usesLoadStoreActions() { return false; }
    }
    // .NET Standard 2.1 overloads the 2018.2 class libraries lack.
    public static class System_String
    {
        public static string[] Split(string text, char separator, StringSplitOptions options)
        {
            return text.Split(new[] { separator }, options);
        }

        public static string[] Split(string text, string separator, StringSplitOptions options)
        {
            return text.Split(new[] { separator ?? "" }, options);
        }

        public static string Trim(string text, char trimChar) { return text.Trim(trimChar); }
        public static bool Contains(string text, char value) { return text.IndexOf(value) >= 0; }
    }

    public static class UnityEngine_Application
    {
        public static void Quit(int exitCode) { Application.Quit(); }
    }

    public static class UnityEngine_AssetBundle
    {
        public static void set_memoryBudgetKB(uint value) { }
    }

    public static class UnityEngine_Component
    {
        public static bool TryGetComponent<T>(Component component, out T found)
        {
            found = component.GetComponent<T>();
            return found != null && !found.Equals(null);
        }
    }

    public static class UnityEngine_GameObject
    {
        public static bool TryGetComponent<T>(GameObject gameObject, out T found)
        {
            found = gameObject.GetComponent<T>();
            return found != null && !found.Equals(null);
        }
    }

    // A shader keyword by name: 2018.2 has only global and material keywords by name.
    public struct UnityEngine_Rendering_LocalKeyword
    {
        readonly string keyword;
        public UnityEngine_Rendering_LocalKeyword(Shader shader, string name) { keyword = name; }
        public string name { get { return keyword; } }
        internal string Name { get { return keyword; } }
    }

    public static class UnityEngine_Material
    {
        public static void SetKeyword(Material material, ref UnityEngine_Rendering_LocalKeyword keyword, bool value)
        {
            if (value) material.EnableKeyword(keyword.Name);
            else material.DisableKeyword(keyword.Name);
        }

        // TMP tells materials apart by this; the instance id does that for the materials it compares.
        public static int ComputeCRC(Material material) { return material.GetInstanceID(); }
    }

    public enum UnityEngine_Rendering_VertexAttribute
    {
        Position = 0, Normal = 1, Tangent = 2, Color = 3, TexCoord0 = 4, TexCoord1 = 5, TexCoord2 = 6, TexCoord3 = 7,
        TexCoord4 = 8, TexCoord5 = 9, TexCoord6 = 10, TexCoord7 = 11, BlendWeight = 12, BlendIndices = 13,
    }

    public static class UnityEngine_Mesh
    {
        // 2018.2 uploads a mesh when its data is set; nothing is left to mark.
        public static void MarkModified(Mesh mesh) { }

        public static bool HasVertexAttribute(Mesh mesh, UnityEngine_Rendering_VertexAttribute attribute)
        {
            switch (attribute)
            {
                case UnityEngine_Rendering_VertexAttribute.Position: return mesh.vertexCount > 0;
                case UnityEngine_Rendering_VertexAttribute.Normal: return mesh.normals.Length > 0;
                case UnityEngine_Rendering_VertexAttribute.Tangent: return mesh.tangents.Length > 0;
                case UnityEngine_Rendering_VertexAttribute.Color: return mesh.colors32.Length > 0;
                case UnityEngine_Rendering_VertexAttribute.TexCoord0: return mesh.uv.Length > 0;
                case UnityEngine_Rendering_VertexAttribute.TexCoord1: return mesh.uv2.Length > 0;
                case UnityEngine_Rendering_VertexAttribute.TexCoord2: return mesh.uv3.Length > 0;
                case UnityEngine_Rendering_VertexAttribute.TexCoord3: return mesh.uv4.Length > 0;
                case UnityEngine_Rendering_VertexAttribute.BlendWeight:
                case UnityEngine_Rendering_VertexAttribute.BlendIndices: return mesh.boneWeights.Length > 0;
                default: return false;
            }
        }

        public static void SetIndices(Mesh mesh, List<int> indices, MeshTopology topology, int submesh, bool calculateBounds, int baseVertex)
        {
            mesh.SetIndices(indices.ToArray(), topology, submesh, calculateBounds, baseVertex);
        }
    }

    public static class UnityEngine_QualitySettings
    {
        public static RenderPipelineAsset get_renderPipeline() { return null; }
        public static void set_renderPipeline(RenderPipelineAsset value) { }
    }

    public static class UnityEngine_Experimental_Rendering_RenderPipelineAsset
    {
        public static Material get_defaultMaterial(RenderPipelineAsset asset) { return asset.GetDefaultMaterial(); }
    }

    public static class UnityEngine_TouchScreenKeyboard
    {
        public static bool get_isInPlaceEditingAllowed() { return false; }
    }

    public static class UnityEngine_UI_RectMask2D
    {
        // Softness and padding came with Unity 2019; the mask clips at its rectangle.
        public static void set_padding(RectMask2D mask, Vector4 value) { }
    }

    public static class UnityEngine_WaitForSecondsRealtime
    {
        // 2018.2 keeps the time to wait until in a private field; 2020 made the duration settable.
        static readonly System.Reflection.FieldInfo until = typeof(WaitForSecondsRealtime).GetField("waitTime",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        public static void set_waitTime(WaitForSecondsRealtime wait, float value)
        {
            until.SetValue(wait, Time.realtimeSinceStartup + value);
        }
    }

    public static class UnityEngine_UI_VertexHelper
    {
        // SFB's uGUI sets every vertex's uv1 to a Vector4 (UIPixelate's pixel grid). 2018.2 keeps
        // uv1 as a Vector2, so zw also go in the tangent, which the UIPixelate shader reads instead.
        static UIVertex vertex;

        public static void SetUV1(VertexHelper helper, Vector4 uv1)
        {
            for (int i = 0; i < helper.currentVertCount; i++)
            {
                helper.PopulateUIVertex(ref vertex, i);
                vertex.uv1 = new Vector2(uv1.x, uv1.y);
                vertex.tangent = uv1;
                helper.SetUIVertex(vertex, i);
            }
        }
    }

    // Unity.Profiling markers (TextMeshPro's): the 2018.2 player has no profiler to report to.
    public struct Unity_Profiling_ProfilerMarker
    {
        public Unity_Profiling_ProfilerMarker(string name) { }
        public void Begin() { }
        public void Begin(Object contextUnityObject) { }
        public void End() { }
        public AutoScope Auto() { return new AutoScope(); }

        public struct AutoScope : IDisposable
        {
            public void Dispose() { }
        }
    }

    // Rewired's raw input forwarding, for Windows only.
    public static class UnityEngine_Windows_Input
    {
        public static void ForwardRawInput(IntPtr rawInputHeaderIndices, IntPtr rawInputDataIndices, uint indicesCount, IntPtr rawInputData, uint rawInputDataSize) { }
    }
}
