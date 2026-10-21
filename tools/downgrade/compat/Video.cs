using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// UnityEngine.Video, which the PS Vita's 2018.2 player lacks (its VideoModule has no types). The
// rewriter sends the namespace here on every platform, so the Windows players play the videos the way
// the PS Vita does. The game's videos are the health monitor's loops (the status screen, Pool's
// hospital bed, the ending); video_rules.py turns each into a clip asset: its frames in a texture
// atlas, its sound as an AudioClip. The player blits the current frame into its target texture
// whenever it changes and plays the sound on its audio source, whose position then sets the video's
// time, so the two stay together. Time runs unscaled, as a VideoPlayer's does (the status screen
// pauses the game).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public class UnityEngine_Video_VideoPlayer : MonoBehaviour
    {
        public delegate void EventHandler(UnityEngine_Video_VideoPlayer source);
        public delegate void ErrorEventHandler(UnityEngine_Video_VideoPlayer source, string message);
        public delegate void FrameReadyEventHandler(UnityEngine_Video_VideoPlayer source, long frameIdx);
        public delegate void TimeEventHandler(UnityEngine_Video_VideoPlayer source, double seconds);

        // The engine VideoPlayer's serialized fields that the pipeline keeps (asset_rules.VIDEO_FIELDS).
        [SerializeField] UnityEngine_Video_VideoClip m_VideoClip;
        [SerializeField] RenderTexture m_TargetTexture;
        [SerializeField] UnityEngine_Video_VideoRenderMode m_RenderMode = UnityEngine_Video_VideoRenderMode.RenderTexture;
        [SerializeField] bool m_Looping;
        [SerializeField] bool m_PlayOnAwake = true;
        [SerializeField] float m_PlaybackSpeed = 1f;
        [SerializeField] UnityEngine_Video_VideoAudioOutputMode m_AudioOutputMode = UnityEngine_Video_VideoAudioOutputMode.AudioSource;
        [SerializeField] AudioSource[] m_TargetAudioSources;
        [SerializeField] float[] m_DirectAudioVolumes;

        bool playing, paused;
        double clock;           // seconds into the clip
        int shownFrame = -1;    // the frame in the target texture
        AudioSource sound;      // where the clip's sound plays, while it does
        AudioSource directSource;
        float lastSoundTime;

        internal static readonly List<UnityEngine_Video_VideoPlayer> all = new List<UnityEngine_Video_VideoPlayer>();

        void OnEnable()
        {
            all.Add(this);
            if (m_PlayOnAwake) Play();
        }

        void OnDisable()
        {
            all.Remove(this);
            Halt();
        }

        public event EventHandler started;
        public event EventHandler loopPointReached;
        public event EventHandler prepareCompleted;
        public event EventHandler seekCompleted;
        public event EventHandler frameDropped;
        public event ErrorEventHandler errorReceived;
        public event FrameReadyEventHandler frameReady;
        public event TimeEventHandler clockResyncOccurred;

        public bool isPlaying { get { return playing && !paused; } }
        public bool isPaused { get { return paused; } }
        public bool isPrepared { get { return m_VideoClip != null; } }
        public bool canStep { get { return true; } }
        public bool canSetDirectAudioVolume { get { return true; } }
        public bool canSetPlaybackSpeed { get { return true; } }
        public bool canSetSkipOnDrop { get { return false; } }
        public bool canSetTime { get { return true; } }
        public bool canSetTimeUpdateMode { get { return false; } }
        public bool sendFrameReadyEvents { get; set; }
        public double time
        {
            get { return clock; }
            set
            {
                clock = Math.Max(0, Math.Min(value, length));
                if (sound != null && sound.clip != null) sound.time = Mathf.Min((float)clock, sound.clip.length - 0.001f);
                lastSoundTime = (float)clock;
                ShowFrame();
                if (seekCompleted != null) seekCompleted(this);
            }
        }
        public long frame
        {
            get { return m_VideoClip != null ? m_VideoClip.FrameAt(clock) : -1; }
            set { if (m_VideoClip != null && m_VideoClip.frameRate > 0) time = value / m_VideoClip.frameRate; }
        }
        public ulong frameCount { get { return m_VideoClip != null ? m_VideoClip.frameCount : 0; } }
        public float frameRate { get { return m_VideoClip != null ? (float)m_VideoClip.frameRate : 0f; } }
        public float playbackSpeed
        {
            get { return m_PlaybackSpeed; }
            set { m_PlaybackSpeed = value; if (sound != null) sound.pitch = value; }
        }
        public float targetCameraAlpha { get; set; }
        public string targetMaterialProperty { get; set; }
        public Renderer targetMaterialRenderer { get; set; }
        public RenderTexture targetTexture { get { return m_TargetTexture; } set { m_TargetTexture = value; shownFrame = -1; ShowFrame(); } }
        public Texture texture { get { return m_TargetTexture; } }
        public string url { get; set; }
        public ushort audioTrackCount { get { return m_VideoClip != null ? m_VideoClip.audioTrackCount : (ushort)0; } }
        public ushort controlledAudioTrackCount { get; set; }
        public static ushort controlledAudioTrackMaxCount { get { return 1; } }
        public UnityEngine_Video_VideoSource source { get; set; }
        public UnityEngine_Video_VideoTimeUpdateMode timeUpdateMode { get; set; }
        public UnityEngine_Video_VideoClip clip
        {
            get { return m_VideoClip; }
            set
            {
                if (value == m_VideoClip) return;
                bool wasPlaying = playing;
                Halt();
                m_VideoClip = value;
                if (wasPlaying) Play();
            }
        }
        public Camera targetCamera { get; set; }
        public bool playOnAwake { get { return m_PlayOnAwake; } set { m_PlayOnAwake = value; } }
        public bool waitForFirstFrame { get; set; }
        public bool isLooping { get { return m_Looping; } set { m_Looping = value; if (sound != null) sound.loop = value; } }
        public bool skipOnDrop { get; set; }
        public UnityEngine_Video_VideoRenderMode renderMode { get { return m_RenderMode; } set { m_RenderMode = value; } }
        public UnityEngine_Video_Video3DLayout targetCamera3DLayout { get; set; }
        public UnityEngine_Video_VideoAspectRatio aspectRatio { get; set; }
        public UnityEngine_Video_VideoAudioOutputMode audioOutputMode { get { return m_AudioOutputMode; } set { m_AudioOutputMode = value; } }

        double length { get { return m_VideoClip != null ? m_VideoClip.length : 0; } }

        public void Play()
        {
            if (playing && !paused) return;
            if (!playing) clock = 0;
            playing = true;
            paused = false;
            shownFrame = -1;
            ShowFrame();
            StartSound();
            if (started != null) started(this);
        }

        public void Pause()
        {
            if (!playing) return;
            paused = true;
            if (sound != null) sound.Pause();
        }

        public void Stop()
        {
            Halt();
        }

        // Stopped, at the start; the target keeps its last frame, as with the engine's player.
        void Halt()
        {
            playing = paused = false;
            clock = 0;
            if (sound != null && sound.clip == SoundClip()) sound.Stop();
            sound = null;
        }

        public void Prepare()
        {
            if (prepareCompleted != null) prepareCompleted(this);
        }

        public void StepForward()
        {
            if (m_VideoClip != null && m_VideoClip.frameRate > 0) time = (frame + 1) / m_VideoClip.frameRate;
        }

        public void SetDirectAudioVolume(ushort trackIndex, float volume)
        {
            if (trackIndex != 0) return;
            if (m_DirectAudioVolumes == null || m_DirectAudioVolumes.Length == 0) m_DirectAudioVolumes = new float[1];
            m_DirectAudioVolumes[0] = volume;
            if (directSource != null) directSource.volume = volume;
        }

        public float GetDirectAudioVolume(ushort trackIndex)
        {
            return m_DirectAudioVolumes != null && trackIndex < m_DirectAudioVolumes.Length ? m_DirectAudioVolumes[trackIndex] : 1f;
        }

        public void SetDirectAudioMute(ushort trackIndex, bool mute) { if (directSource != null) directSource.mute = mute; }
        public void EnableAudioTrack(ushort trackIndex, bool enabled) { }

        public void SetTargetAudioSource(ushort trackIndex, AudioSource source)
        {
            if (trackIndex != 0) return;
            m_TargetAudioSources = new[] { source };
        }

        AudioClip SoundClip()
        {
            return m_VideoClip != null ? m_VideoClip.sound : null;
        }

        void StartSound()
        {
            AudioClip clipSound = SoundClip();
            if (clipSound == null) return;
            AudioSource target = null;
            if (m_AudioOutputMode == UnityEngine_Video_VideoAudioOutputMode.AudioSource)
                target = m_TargetAudioSources != null && m_TargetAudioSources.Length > 0 ? m_TargetAudioSources[0] : null;
            else if (m_AudioOutputMode == UnityEngine_Video_VideoAudioOutputMode.Direct)
            {
                if (directSource == null)
                {
                    directSource = gameObject.AddComponent<AudioSource>();
                    directSource.playOnAwake = false;
                    directSource.spatialBlend = 0f;
                    directSource.volume = GetDirectAudioVolume(0);
                }
                target = directSource;
            }
            if (target == null) return;
            sound = target;
            if (sound.clip != clipSound || !sound.isPlaying)
            {
                sound.clip = clipSound;
                sound.loop = m_Looping;
                sound.pitch = m_PlaybackSpeed;
                sound.time = Mathf.Min((float)clock, clipSound.length - 0.001f);
                IdleAudio.Play(sound);  // enabled again first if IdleAudio turned it off
            }
            else IdleAudio.UnPause(sound);
            lastSoundTime = sound.time;
        }

        void Update()
        {
            if (!playing || paused) return;
            if (m_VideoClip == null)
            {
                // Nothing to show: the video ends at once, so the game's waits for it finish.
                Halt();
                if (loopPointReached != null) loopPointReached(this);
                return;
            }
            bool wrapped = false;
            if (sound != null && sound.isPlaying && sound.clip == SoundClip())
            {
                float now = sound.time;
                wrapped = now < lastSoundTime;
                lastSoundTime = now;
                clock = now;
            }
            else
            {
                clock += Time.unscaledDeltaTime * m_PlaybackSpeed;
                if (clock >= length && length > 0)
                {
                    wrapped = true;
                    clock = m_Looping ? clock % length : length;
                }
            }
            if (wrapped || (!m_Looping && clock >= length))
            {
                if (!m_Looping)
                {
                    clock = length;
                    ShowFrame();
                    Halt();
                    clock = length;
                }
                if (loopPointReached != null) loopPointReached(this);
                if (!m_Looping) return;
            }
            ShowFrame();
        }

        void ShowFrame()
        {
            if (m_VideoClip == null || m_TargetTexture == null || m_RenderMode != UnityEngine_Video_VideoRenderMode.RenderTexture) return;
            int index = m_VideoClip.FrameAt(clock);
            if (index == shownFrame || m_VideoClip.frames == null) return;
            Vector2 scale, offset;
            m_VideoClip.FrameRect(index, out scale, out offset);
            RenderTexture active = RenderTexture.active;
            Graphics.Blit(m_VideoClip.frames, m_TargetTexture, scale, offset);
            RenderTexture.active = active;
            shownFrame = index;
            if (sendFrameReadyEvents && frameReady != null) frameReady(this, index);
        }

        // Keeps the events' fields read, so the compiler does not warn about the ones never raised.
        void Unused()
        {
            if (frameDropped != null || errorReceived != null || clockResyncOccurred != null) { }
        }
    }

    // A video as video_rules.py writes it: the frames kept (every one at 30 fps or less) in an atlas,
    // FrameCount of them row by row from the top left, each TileWidth x TileHeight in a cell of
    // StrideX x StrideY with Gutter pixels of its own edge around it; the sound, one loop long.
#pragma warning disable 0649  // the fields come from the asset
    public class UnityEngine_Video_VideoClip : ScriptableObject
    {
        [SerializeField] string m_OriginalPath;
        [SerializeField] Texture2D m_Frames;
        [SerializeField] AudioClip m_Sound;
        [SerializeField] int m_FrameCount;
        [SerializeField] float m_FrameRate;
        [SerializeField] double m_Length;
        [SerializeField] int m_Width;
        [SerializeField] int m_Height;
        [SerializeField] int m_Columns = 1;
        [SerializeField] int m_TileWidth;
        [SerializeField] int m_TileHeight;
        [SerializeField] int m_StrideX;
        [SerializeField] int m_StrideY;
        [SerializeField] int m_Gutter;

        public string originalPath { get { return m_OriginalPath; } }
        public ulong frameCount { get { return (ulong)m_FrameCount; } }
        public double frameRate { get { return m_FrameRate; } }
        public double length { get { return m_Length; } }
        public uint width { get { return (uint)m_Width; } }
        public uint height { get { return (uint)m_Height; } }
        public ushort audioTrackCount { get { return (ushort)(m_Sound != null ? 1 : 0); } }

        internal Texture2D frames { get { return m_Frames; } }
        internal AudioClip sound { get { return m_Sound; } }

        internal int FrameAt(double seconds)
        {
            if (m_FrameCount <= 0) return 0;
            return Mathf.Clamp((int)(seconds * m_FrameRate), 0, m_FrameCount - 1);
        }

        // The frame's rectangle in the atlas as Graphics.Blit's scale and offset (UV origin bottom left).
        internal void FrameRect(int index, out Vector2 scale, out Vector2 offset)
        {
            float atlasWidth = m_Frames.width, atlasHeight = m_Frames.height;
            int column = index % m_Columns, row = index / m_Columns;
            float left = column * m_StrideX + m_Gutter, top = row * m_StrideY + m_Gutter;
            scale = new Vector2(m_TileWidth / atlasWidth, m_TileHeight / atlasHeight);
            offset = new Vector2(left / atlasWidth, 1f - (top + m_TileHeight) / atlasHeight);
        }
    }
#pragma warning restore 0649

    public enum UnityEngine_Video_VideoSource { VideoClip = 0, Url = 1 }
    public enum UnityEngine_Video_VideoTimeUpdateMode { DSPTime = 0, GameTime = 1, UnscaledGameTime = 2 }
    public enum UnityEngine_Video_VideoRenderMode { CameraFarPlane = 0, CameraNearPlane = 1, RenderTexture = 2, MaterialOverride = 3, APIOnly = 4 }
    public enum UnityEngine_Video_Video3DLayout { No3D = 0, SideBySide3D = 1, OverUnder3D = 2 }
    public enum UnityEngine_Video_VideoAspectRatio { NoScaling = 0, FitVertically = 1, FitHorizontally = 2, FitInside = 3, FitOutside = 4, Stretch = 5 }
    public enum UnityEngine_Video_VideoAudioOutputMode { None = 0, AudioSource = 1, Direct = 2, APIOnly = 3 }

    // Test flags for the videos and the screens that show them (see Diag for the others):
    //   -crow-activate <name> <s>  s seconds after each scene load, activate every GameObject called
    //                              <name>, with its parents (the status screen: "UI Canvas Inventory OFF",
    //                              "Inventory Screenspace OFF"); repeatable
    //   -crow-video-log            log each playing video's clip, frame and time every second
    //   -crow-no-uv2               canvases without uv2, as before UIPixelate's fix (for comparisons)
    public class VideoProbe : MonoBehaviour
    {
        static readonly List<KeyValuePair<string, float>> activations = new List<KeyValuePair<string, float>>();
        static bool logVideos, noUv2;
        float lastLoad, lastLog;
        readonly HashSet<string> activated = new HashSet<string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            string[] args = ProbeFiles.Args;
            for (int i = 0; i + 2 < args.Length; i++)
            {
                float seconds;
                if (args[i] == "-crow-activate" && float.TryParse(args[i + 2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out seconds))
                    activations.Add(new KeyValuePair<string, float>(args[i + 1], seconds));
            }
            logVideos = Array.IndexOf(args, "-crow-video-log") >= 0;
            noUv2 = Array.IndexOf(args, "-crow-no-uv2") >= 0;
            if (activations.Count == 0 && !logVideos && !noUv2) return;
            var probe = new GameObject("CrowVideoProbe");
            DontDestroyOnLoad(probe);
            probe.AddComponent<VideoProbe>();
        }

        void Start()
        {
            lastLoad = Time.realtimeSinceStartup;
            CrowEvents.Loaded("VideoProbe.sceneLoaded", (scene, mode) => { lastLoad = Time.realtimeSinceStartup; activated.Clear(); });
        }

        void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (noUv2)
                foreach (Canvas canvas in Resources.FindObjectsOfTypeAll<Canvas>())
                    if (canvas.gameObject.scene.IsValid()) canvas.additionalShaderChannels &= ~AdditionalCanvasShaderChannels.TexCoord2;
            foreach (KeyValuePair<string, float> activation in activations)
            {
                string key = activation.Key + "@" + activation.Value;
                if (now - lastLoad < activation.Value || activated.Contains(key)) continue;
                activated.Add(key);
                int count = 0;
                foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (candidate.name != activation.Key || !candidate.scene.IsValid()) continue;
                    for (Transform t = candidate.transform; t != null; t = t.parent) t.gameObject.SetActive(true);
                    count++;
                }
                Debug.Log("CROW_VIDEO activated " + count + " objects called " + activation.Key);
            }
            if (logVideos && now - lastLog >= 1f)
            {
                lastLog = now;
                foreach (UnityEngine_Video_VideoPlayer player in UnityEngine_Video_VideoPlayer.all)
                    Debug.Log("CROW_VIDEO " + player.name + " clip=" + (player.clip != null ? player.clip.name : "none") +
                        " playing=" + player.isPlaying + " frame=" + player.frame + " time=" + player.time.ToString("F2") +
                        " target=" + (player.targetTexture != null ? player.targetTexture.name : "none"));
            }
        }
    }
}
