using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// Addressables, which the game loads its player prefab and its scenes through: the rewriter ships
// neither Unity.Addressables nor Unity.ResourceManager (pipeline.ROOTS), so the game's calls come
// here by the naming convention in Compat2018.cs. The assets the game's catalog names are in an
// AddressablesTable the build lists in the player's preloaded assets (DowngradeBuild), so they load
// at startup with dependencies the build worked out. (From Resources, the PS Vita player spent 40 s
// in one frame on the player prefab's dependencies, ResourceManager::FindDependencies.) Other keys
// load from Resources/Addressables/<key>; scenes are in the build settings and load by name. A key
// that is in no catalog (the game asks for a BGM prefab its catalog lacks) fails as it does in the
// PC game: the handle completes with a null result.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class UnityEngine_AddressableAssets_Addressables
    {
        // The folder under Resources that holds the catalog's assets.
        public const string Folder = "Addressables/";

        static readonly UnityEngine_ResourceManagement_ResourceManager resourceManager = new UnityEngine_ResourceManagement_ResourceManager();

        public static UnityEngine_ResourceManagement_ResourceManager ResourceManager { get { return resourceManager; } }

        public static UnityEngine_ResourceManagement_AsyncOperations_AsyncOperationHandle<T> LoadAssetAsync<T>(object key)
        {
            // The player prefab, which a build with the CrowMara scene leaves out (Loading.cs MaraScene),
            // is done at once with no asset: MaraLoader takes the scene's instance.
            UnityEngine.Object preloaded = null;
            if (MaraScene.Serves(key) || AddressablesTable.Assets.TryGetValue(key.ToString(), out preloaded))
            {
                var loaded = new Operation<T>();
                loaded.Watch(null, () => (T)(object)preloaded);
                return new UnityEngine_ResourceManagement_AsyncOperations_AsyncOperationHandle<T>(loaded);
            }
            string path = Folder + key;
            var operation = new Operation<T>();
            operation.request = Resources.LoadAsync(path, typeof(T));
            operation.load = () => (T)(object)Resources.Load(path, typeof(T));
            operation.Watch(operation.request, () =>
            {
                UnityEngine.Object asset = operation.request.asset;
                if (asset == null) Debug.LogError("Addressables: no asset " + key + " in Resources/" + path);
                return asset == null ? default(T) : (T)(object)asset;
            });
            return new UnityEngine_ResourceManagement_AsyncOperations_AsyncOperationHandle<T>(operation);
        }

        public static UnityEngine_ResourceManagement_AsyncOperations_AsyncOperationHandle<UnityEngine_ResourceManagement_ResourceProviders_SceneInstance> LoadSceneAsync(
            object key, LoadSceneMode loadMode, bool activateOnLoad, int priority)
        {
            string name = key.ToString();
            var operation = new Operation<UnityEngine_ResourceManagement_ResourceProviders_SceneInstance>();
            AsyncOperation load = SceneManager.LoadSceneAsync(name, loadMode);
            load.allowSceneActivation = activateOnLoad;
            if (priority > 0) load.priority = priority;
            operation.Watch(load, () =>
                new UnityEngine_ResourceManagement_ResourceProviders_SceneInstance(SceneManager.GetSceneByName(name), load));
            return new UnityEngine_ResourceManagement_AsyncOperations_AsyncOperationHandle<UnityEngine_ResourceManagement_ResourceProviders_SceneInstance>(operation);
        }

        // One load: done when Unity's operation is, then its result and its callbacks.
        internal sealed class Operation<T>
        {
            public ResourceRequest request;
            public Func<T> load;  // the asset at once, for WaitForCompletion
            public bool done;
            public T result;
            int references = 1;
            Func<T> finish;
            readonly List<Action<UnityEngine_ResourceManagement_AsyncOperations_AsyncOperationHandle<T>>> completed =
                new List<Action<UnityEngine_ResourceManagement_AsyncOperations_AsyncOperationHandle<T>>>();

            public void Watch(AsyncOperation operation, Func<T> finish)
            {
                this.finish = finish;
                if (operation == null || operation.isDone) Finish();
                else operation.completed += _ => Finish();
            }

            public void Finish()
            {
                if (done) return;
                result = finish();
                done = true;
                var handle = new UnityEngine_ResourceManagement_AsyncOperations_AsyncOperationHandle<T>(this);
                foreach (Action<UnityEngine_ResourceManagement_AsyncOperations_AsyncOperationHandle<T>> callback in completed.ToArray()) callback(handle);
                completed.Clear();
            }

            public T Wait()
            {
                if (!done && load != null)
                {
                    result = load();
                    finish = () => result;
                    Finish();
                }
                return result;
            }

            public void OnCompleted(Action<UnityEngine_ResourceManagement_AsyncOperations_AsyncOperationHandle<T>> callback)
            {
                if (callback == null) return;
                if (done) callback(new UnityEngine_ResourceManagement_AsyncOperations_AsyncOperationHandle<T>(this));
                else completed.Add(callback);
            }

            public void RemoveCompleted(Action<UnityEngine_ResourceManagement_AsyncOperations_AsyncOperationHandle<T>> callback)
            {
                completed.Remove(callback);
            }

            public void Acquire() { references++; }
        }
    }

    // The Addressables assets, keyed as the game's catalog keys them; the build creates one table and
    // lists it in the player's preloaded assets, which load before the first scene.
    public class AddressablesTable : ScriptableObject
    {
        public string[] keys = new string[0];
        public UnityEngine.Object[] assets = new UnityEngine.Object[0];

        internal static readonly Dictionary<string, UnityEngine.Object> Assets = new Dictionary<string, UnityEngine.Object>();

        void OnEnable()
        {
            for (int i = 0; i < keys.Length && i < assets.Length; i++)
                if (assets[i] != null) Assets[keys[i]] = assets[i];
        }
    }

    public struct UnityEngine_ResourceManagement_AsyncOperations_AsyncOperationHandle<TObject>
    {
        readonly UnityEngine_AddressableAssets_Addressables.Operation<TObject> operation;

        internal UnityEngine_ResourceManagement_AsyncOperations_AsyncOperationHandle(UnityEngine_AddressableAssets_Addressables.Operation<TObject> operation)
        {
            this.operation = operation;
        }

        public bool IsValid() { return operation != null; }
        public bool IsDone { get { return operation == null || operation.done; } }
        public TObject Result { get { return operation == null ? default(TObject) : operation.result; } }
        public float PercentComplete { get { return IsDone ? 1f : operation.request != null ? operation.request.progress : 0f; } }
        public TObject WaitForCompletion() { return operation == null ? default(TObject) : operation.Wait(); }

        public event Action<UnityEngine_ResourceManagement_AsyncOperations_AsyncOperationHandle<TObject>> Completed
        {
            add { if (operation != null) operation.OnCompleted(value); }
            remove { if (operation != null) operation.RemoveCompleted(value); }
        }

        internal void Acquire() { if (operation != null) operation.Acquire(); }
    }

    public class UnityEngine_ResourceManagement_ResourceManager
    {
        public UnityEngine_ResourceManagement_AsyncOperations_AsyncOperationHandle<T> Acquire<T>(UnityEngine_ResourceManagement_AsyncOperations_AsyncOperationHandle<T> handle)
        {
            handle.Acquire();
            return handle;
        }
    }

    public struct UnityEngine_ResourceManagement_ResourceProviders_SceneInstance
    {
        readonly Scene scene;
        readonly AsyncOperation operation;

        public UnityEngine_ResourceManagement_ResourceProviders_SceneInstance(Scene scene, AsyncOperation operation)
        {
            this.scene = scene;
            this.operation = operation;
        }

        public Scene Scene { get { return scene; } }
        public AsyncOperation ActivateAsync() { operation.allowSceneActivation = true; return operation; }
    }

    // A serialized reference to an addressable prefab (KeepAliveAssetLoader.prefabRef), kept with its
    // fields; the game never loads through it.
    [Serializable]
    public class UnityEngine_AddressableAssets_AssetReferenceGameObject
    {
        [SerializeField] string m_AssetGUID = "";
        [SerializeField] string m_SubObjectName;
        [SerializeField] string m_SubObjectType = null;

        public UnityEngine_AddressableAssets_AssetReferenceGameObject(string guid) { m_AssetGUID = guid; }
        public string AssetGUID { get { return m_AssetGUID; } }
        public string SubObjectName { get { return m_SubObjectName; } set { m_SubObjectName = value; } }
        public string SubObjectType { get { return m_SubObjectType; } }
    }
}
