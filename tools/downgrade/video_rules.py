"""Videos: the PS Vita's 2018.2 player has no VideoPlayer and no VideoClip. The game's videos, the
health monitor's five loops (VP8 WebM at 650x250 and 60 fps, with Vorbis sound; once more in the
Addressables bundle), become CrowCompat clip assets: the frames in a texture atlas and the sound as an
AudioClip, which CrowCompat's VideoPlayer (compat/Video.cs) blits into its target texture and plays on
its audio source. The Windows players get the same, so they show what the PS Vita does.

video_overlay runs after the other asset rules: it drops the exported videos (and name_overlay's moves
of them) and points the references to them at the clip assets, which keep the videos' GUIDs."""

import io
import math
import re
import sys
import wave
from fractions import Fraction
from pathlib import Path

from asset_rules import (
    COMPAT_GUID,
    PLATFORM_TEXTURE_FORMATS,
    SCENE_REFERENCE_FOLDERS_SKIPPED,
    TEXTURE_PLATFORM_ENTRY,
    current_bytes,
    current_text,
    dxt_format,
    script_file_id,
)
from common import YAML_FILES, expect, folder_meta, guid_for, listing, log, meta_guid, meta_of, scan

VIDEO_SUFFIXES = {'.webm', '.mp4', '.mov', '.ogv'}
FOLDER = Path('Assets') / 'CrowVideo'
# The frames kept: every frame of a clip at FRAME_RATE or less, at FRAME_SCALE of its size. The
# monitor is a quad in the status screen, which the game shows at 416x234 at most; the half-size,
# 30 fps loops take an atlas of 2048x1024 each (1 MB in DXT1).
FRAME_RATE = 30
FRAME_SCALE = 0.5
# Each frame sits in the atlas with GUTTER pixels of its own edge around it, so bilinear filtering at
# a frame's border doesn't read its neighbours. Sizes are multiples of 4, DXT's block size.
GUTTER = 2
ATLAS_MAX = 2048
# Unity's fileIDs of an imported file's main object and of a native asset's.
CLIP_FILE_ID, ASSET_FILE_ID = 32900000, 11400000
TEXTURE_FILE_ID, AUDIO_FILE_ID = 2800000, 8300000


def decode(path: Path):
    """(frames as RGB images, frame rate, mono sound as 16-bit samples, sample rate or None)."""
    import av
    import numpy as np

    with av.open(str(path)) as container:
        stream = container.streams.video[0]
        rate = Fraction(stream.average_rate)
        frames = [frame.to_image().convert('RGB') for frame in container.decode(stream)]
    sound, sample_rate = None, None
    with av.open(str(path)) as container:
        if container.streams.audio:
            stream = container.streams.audio[0]
            sample_rate = stream.rate
            chunks = [frame.to_ndarray().astype(np.float32) for frame in container.decode(stream)]
            # Planar float frames are (channels, samples); packed ones (1, samples * channels).
            channels = stream.codec_context.channels
            mono = [
                (c.reshape(-1, channels).mean(axis=1) if c.shape[0] == 1 and channels > 1 else c.mean(axis=0))
                for c in chunks
            ]
            sound = np.concatenate(mono) if mono else np.zeros(0, np.float32)
            # The loop is the video's length: the sound loops with it.
            samples = round(len(frames) / rate * sample_rate)
            sound = np.pad(sound[:samples], (0, max(0, samples - len(sound))))
            sound = (np.clip(sound, -1, 1) * 32767).round().astype('<i2')
    return frames, rate, sound, sample_rate


def round4(value):
    return max(4, round(value / 4) * 4)


def pack(frames, rate):
    """The atlas (an RGB image) of the frames kept, and the clip's layout fields."""
    import numpy as np
    from PIL import Image

    step = max(1, math.ceil(rate / FRAME_RATE))
    kept = frames[::step]
    width, height = frames[0].size
    tile_w, tile_h = round4(width * FRAME_SCALE), round4(height * FRAME_SCALE)
    stride_w, stride_h = tile_w + 2 * GUTTER, tile_h + 2 * GUTTER
    columns = min(len(kept), ATLAS_MAX // stride_w)
    rows = math.ceil(len(kept) / columns)
    atlas_w = 1 << (columns * stride_w - 1).bit_length()
    atlas_h = 1 << (rows * stride_h - 1).bit_length()
    if atlas_h > ATLAS_MAX:
        sys.exit(
            f'{len(kept)} frames of {tile_w}x{tile_h} do not fit a {ATLAS_MAX} atlas: lower FRAME_RATE or FRAME_SCALE'
        )
    atlas = Image.new('RGB', (atlas_w, atlas_h))
    for index, frame in enumerate(kept):
        tile = np.asarray(frame.resize((tile_w, tile_h), Image.LANCZOS))
        tile = np.pad(tile, ((GUTTER, GUTTER), (GUTTER, GUTTER), (0, 0)), mode='edge')
        atlas.paste(Image.fromarray(tile), ((index % columns) * stride_w, (index // columns) * stride_h))
    layout = {
        'm_FrameCount': len(kept),
        'm_FrameRate': float(rate / step),
        'm_Length': float(len(frames) / rate),
        'm_Width': width,
        'm_Height': height,
        'm_Columns': columns,
        'm_TileWidth': tile_w,
        'm_TileHeight': tile_h,
        'm_StrideX': stride_w,
        'm_StrideY': stride_h,
        'm_Gutter': GUTTER,
    }
    return atlas, layout


def wav_bytes(sound, sample_rate):
    out = io.BytesIO()
    with wave.open(out, 'wb') as writer:
        writer.setnchannels(1)
        writer.setsampwidth(2)
        writer.setframerate(sample_rate)
        writer.writeframes(sound.tobytes())
    return out.getvalue()


def png_bytes(image):
    out = io.BytesIO()
    image.save(out, 'PNG', optimize=False)
    return out.getvalue()


CLIP_ASSET = """%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!114 &{file_id}
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInternal: {{fileID: 0}}
  m_GameObject: {{fileID: 0}}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {{fileID: {script}, guid: {compat}, type: 3}}
  m_Name: {name}
  m_EditorClassIdentifier:
  m_OriginalPath: {original}
  m_Frames: {{fileID: {texture_id}, guid: {frames}, type: 3}}
  m_Sound: {sound}
{layout}
"""
ASSET_META = """fileFormatVersion: 2
guid: {guid}
NativeFormatImporter:
  externalObjects: {{}}
  mainObjectFileID: {file_id}
  userData:
  assetBundleName:
  assetBundleVariant:
"""
ATLAS_META = """fileFormatVersion: 2
guid: {guid}
TextureImporter:
  serializedVersion: 7
  mipmaps:
    mipMapMode: 0
    enableMipMap: 0
    sRGBTexture: 1
    linearTexture: 0
  isReadable: 0
  textureFormat: -1
  maxTextureSize: {size}
  textureSettings:
    serializedVersion: 2
    filterMode: 1
    aniso: 1
    mipBias: 0
    wrapU: 1
    wrapV: 1
    wrapW: 1
  nPOTScale: 0
  lightmap: 0
  compressionQuality: 100
  spriteMode: 0
  alphaUsage: 0
  alphaIsTransparency: 0
  textureType: 0
  textureShape: 1
  platformSettings:
  - serializedVersion: 2
    buildTarget: DefaultTexturePlatform
    maxTextureSize: {size}
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 1
    compressionQuality: 100
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    androidETC2FallbackOverride: 0
{platform}  userData:
  assetBundleName:
  assetBundleVariant:
"""
# Short loops: kept whole in memory as PCM (100 KB a clip, mono at 48 kHz), loaded with the clip.
SOUND_META = """fileFormatVersion: 2
guid: {guid}
AudioImporter:
  serializedVersion: 6
  defaultSettings:
    loadType: 0
    sampleRateSetting: 0
    sampleRateOverride: 0
    compressionFormat: 0
    quality: 1
    conversionMode: 0
  platformSettingOverrides: {{}}
  forceToMono: 1
  normalize: 0
  preloadAudioData: 1
  loadInBackground: 0
  ambisonic: 0
  3D: 0
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def clip_overlay(path: Path, relative: Path, guid: str, platform: str):
    """The clip asset, its atlas and its sound for one exported video, under FOLDER."""
    frames, rate, sound, sample_rate = decode(path)
    atlas, layout = pack(frames, rate)
    stem = path.stem
    frames_guid, sound_guid = guid_for('video-frames', guid), guid_for('video-sound', guid)
    overlay = {}
    atlas_path = FOLDER / f'{stem} frames.png'
    overlay[atlas_path] = png_bytes(atlas)
    platform_entry = ''
    if platform in PLATFORM_TEXTURE_FORMATS:  # DXT1, as the texture rules import an opaque image
        target = PLATFORM_TEXTURE_FORMATS[platform][0]
        platform_entry = TEXTURE_PLATFORM_ENTRY.format(target=target, size=ATLAS_MAX, format=dxt_format(platform, True))
    overlay[meta_of(atlas_path)] = ATLAS_META.format(guid=frames_guid, size=ATLAS_MAX, platform=platform_entry).encode()
    sound_ref = '{fileID: 0}'
    if sound is not None:
        sound_path = FOLDER / f'{stem} sound.wav'
        overlay[sound_path] = wav_bytes(sound, sample_rate)
        overlay[meta_of(sound_path)] = SOUND_META.format(guid=sound_guid).encode()
        sound_ref = f'{{fileID: {AUDIO_FILE_ID}, guid: {sound_guid}, type: 3}}'
    clip_path = FOLDER / f'{stem}.asset'
    overlay[clip_path] = CLIP_ASSET.format(
        file_id=ASSET_FILE_ID,
        script=script_file_id('CrowCompat', 'UnityEngine_Video_VideoClip'),
        compat=COMPAT_GUID,
        name=stem,
        original=relative.as_posix(),
        texture_id=TEXTURE_FILE_ID,
        frames=frames_guid,
        sound=sound_ref,
        layout='\n'.join(f'  {key}: {value}' for key, value in layout.items()),
    ).encode()
    overlay[meta_of(clip_path)] = ASSET_META.format(guid=guid, file_id=ASSET_FILE_ID).encode()
    return overlay, layout


def video_overlay(exported: Path, overlay: dict, excluded: set, platform: str):
    """Replaces the exported videos with clip assets: adds to overlay and excluded in place."""
    assets = exported / 'Assets'
    videos = sorted(
        Path('Assets') / relative
        for relative, stat in listing(assets).items()
        if stat is not None and relative.suffix.lower() in VIDEO_SUFFIXES
    )
    expect(len(videos), 10, 'videos in the export')  # the health monitor's five, twice
    # name_overlay's moves of them ("<stem>/<original name>.webm" and its folder).
    for key in [
        k for k in overlay if k.suffix.lower() in VIDEO_SUFFIXES or Path(k.stem).suffix.lower() in VIDEO_SUFFIXES
    ]:
        del overlay[key]
        folder = key.parent
        if meta_of(folder) in overlay and not any(folder in k.parents for k in overlay):
            del overlay[meta_of(folder)]
    guids = {}
    overlay.update(folder_meta(FOLDER))
    frames = 0
    for relative in videos:
        guid = meta_guid(exported / meta_of(relative))
        guids[guid] = relative
        excluded.update({relative, meta_of(relative)})
        clip, layout = clip_overlay(exported / relative, relative, guid, platform)
        overlay.update(clip)
        frames += layout['m_FrameCount']
    # The references to the videos, now to the clip assets (same GUID, a native asset's main object).
    keys = [guid.encode() for guid in sorted(guids)]
    reference = re.compile(rf'\{{fileID: {CLIP_FILE_ID}, guid: ({"|".join(sorted(guids))}), type: 3\}}')
    names_video = scan(
        'video-references',
        assets,
        lambda relative: relative.suffix in YAML_FILES and relative.parts[0] not in SCENE_REFERENCE_FOLDERS_SKIPPED,
        lambda path: any(key in path.read_bytes() for key in keys),
        version=' '.join(sorted(guids)),
    )
    files, count = 0, 0
    for found, named in names_video.items():
        relative = Path('Assets') / found
        if relative in overlay:
            data = current_bytes(exported, overlay, relative)
            named = any(key in data for key in keys)
        if not named:
            continue
        if relative in excluded:
            sys.exit(
                f'{relative} references a video and was moved by an earlier rule; video_overlay needs its path now'
            )
        text, changed = reference.subn(
            rf'{{fileID: {ASSET_FILE_ID}, guid: \1, type: 2}}', current_text(exported, overlay, relative)
        )
        if changed:
            overlay[relative] = text.encode('utf-8')
            files += 1
            count += changed
    log(f'Videos: {len(videos)} clips as frame atlases ({frames} frames); {count} references in {files} files')
    expect(count, 14, 'references to the videos made clip assets')
