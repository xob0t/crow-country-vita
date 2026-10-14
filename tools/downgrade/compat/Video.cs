using UnityEngine;

// UnityEngine.Video, which the PS Vita's 2018.2 player lacks (its VideoModule has no types). The
// game's videos are the health monitor's loops in the status screen. Until they are played another
// way, a started video reports playing for the two frames after Play, so the game's waits for it
// finish as if it had ended, and it shows no picture.
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
        [SerializeField] RenderTexture m_TargetTexture;
        [SerializeField] bool m_Looping;
        [SerializeField] bool m_PlayOnAwake = true;
        [SerializeField] float m_PlaybackSpeed = 1f;

        int playedFrame = -10;
        bool paused;

        void OnEnable()
        {
            if (m_PlayOnAwake) Play();
        }

        public event EventHandler started;
        public event EventHandler loopPointReached;
        public event EventHandler prepareCompleted;
        public event EventHandler seekCompleted;
        public event EventHandler frameDropped;
        public event ErrorEventHandler errorReceived;
        public event FrameReadyEventHandler frameReady;
        public event TimeEventHandler clockResyncOccurred;

        public bool isPlaying { get { return !paused && Time.frameCount <= playedFrame + 1; } }
        public bool isPaused { get { return paused; } }
        public bool isPrepared { get { return true; } }
        public bool canStep { get { return false; } }
        public bool canSetDirectAudioVolume { get { return false; } }
        public bool canSetPlaybackSpeed { get { return false; } }
        public bool canSetSkipOnDrop { get { return false; } }
        public bool canSetTime { get { return false; } }
        public bool canSetTimeUpdateMode { get { return false; } }
        public bool sendFrameReadyEvents { get; set; }
        public double time { get; set; }
        public long frame { get; set; }
        public ulong frameCount { get { return 0; } }
        public float frameRate { get { return 0f; } }
        public float playbackSpeed { get { return m_PlaybackSpeed; } set { m_PlaybackSpeed = value; } }
        public float targetCameraAlpha { get; set; }
        public string targetMaterialProperty { get; set; }
        public Renderer targetMaterialRenderer { get; set; }
        public RenderTexture targetTexture { get { return m_TargetTexture; } set { m_TargetTexture = value; } }
        public Texture texture { get { return null; } }
        public string url { get; set; }
        public ushort audioTrackCount { get { return 0; } }
        public ushort controlledAudioTrackCount { get; set; }
        public static ushort controlledAudioTrackMaxCount { get { return 0; } }
        public UnityEngine_Video_VideoSource source { get; set; }
        public UnityEngine_Video_VideoTimeUpdateMode timeUpdateMode { get; set; }
        public UnityEngine_Video_VideoClip clip { get; set; }
        public Camera targetCamera { get; set; }
        public bool playOnAwake { get { return m_PlayOnAwake; } set { m_PlayOnAwake = value; } }
        public bool waitForFirstFrame { get; set; }
        public bool isLooping { get { return m_Looping; } set { m_Looping = value; } }
        public bool skipOnDrop { get; set; }
        public UnityEngine_Video_VideoRenderMode renderMode { get; set; }
        public UnityEngine_Video_Video3DLayout targetCamera3DLayout { get; set; }
        public UnityEngine_Video_VideoAspectRatio aspectRatio { get; set; }
        public UnityEngine_Video_VideoAudioOutputMode audioOutputMode { get; set; }

        public void Play()
        {
            playedFrame = Time.frameCount;
            paused = false;
            if (started != null) started(this);
        }

        public void Pause() { paused = true; }

        public void Stop()
        {
            playedFrame = -10;
            paused = false;
        }

        public void Prepare()
        {
            if (prepareCompleted != null) prepareCompleted(this);
        }

        public void StepForward() { }
        public void SetDirectAudioVolume(ushort trackIndex, float volume) { }
        public float GetDirectAudioVolume(ushort trackIndex) { return 0f; }
        public void SetDirectAudioMute(ushort trackIndex, bool mute) { }
        public void EnableAudioTrack(ushort trackIndex, bool enabled) { }
        public void SetTargetAudioSource(ushort trackIndex, AudioSource source) { }

#if !CROW_VITA
        // Players with a VideoPlayer (Windows): members Unity 2019 added. The time update mode only
        // matters when the game is paused or slowed; 2018 plays in DSP time.
        public static bool get_canSetTimeUpdateMode(UnityEngine.Video.VideoPlayer player) { return false; }
        public static UnityEngine_Video_VideoTimeUpdateMode get_timeUpdateMode(UnityEngine.Video.VideoPlayer player) { return UnityEngine_Video_VideoTimeUpdateMode.DSPTime; }
        public static void set_timeUpdateMode(UnityEngine.Video.VideoPlayer player, UnityEngine_Video_VideoTimeUpdateMode mode) { }
#endif

        // Keeps the events' fields read, so the compiler does not warn about the ones never raised.
        void Unused()
        {
            if (loopPointReached != null || seekCompleted != null || frameDropped != null || errorReceived != null ||
                frameReady != null || clockResyncOccurred != null) { }
        }
    }

    public class UnityEngine_Video_VideoClip : ScriptableObject
    {
        public string originalPath { get { return ""; } }
        public ulong frameCount { get { return 0; } }
        public double frameRate { get { return 0; } }
        public double length { get { return 0; } }
        public uint width { get { return 0; } }
        public uint height { get { return 0; } }
        public ushort audioTrackCount { get { return 0; } }
    }

    public enum UnityEngine_Video_VideoSource { VideoClip = 0, Url = 1 }
    public enum UnityEngine_Video_VideoTimeUpdateMode { DSPTime = 0, GameTime = 1, UnscaledGameTime = 2 }
    public enum UnityEngine_Video_VideoRenderMode { CameraFarPlane = 0, CameraNearPlane = 1, RenderTexture = 2, MaterialOverride = 3, APIOnly = 4 }
    public enum UnityEngine_Video_Video3DLayout { No3D = 0, SideBySide3D = 1, OverUnder3D = 2 }
    public enum UnityEngine_Video_VideoAspectRatio { NoScaling = 0, FitVertically = 1, FitHorizontally = 2, FitInside = 3, FitOutside = 4, Stretch = 5 }
    public enum UnityEngine_Video_VideoAudioOutputMode { None = 0, AudioSource = 1, Direct = 2, APIOnly = 3 }
}
