using System;
using UnityEngine;

// UnityEngine.TextCore, which TextMeshPro 3 uses and Unity 2018.2 lacks: the font asset data
// (face info, glyphs, kerning pairs), serialized under the field names Unity 2022 gives them, so
// the game's TMP font assets load as written. FontEngine renders glyphs at run time, for dynamic
// font assets; there is no font engine here, so its calls report failure and TMP keeps the
// glyphs its static atlases already have.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    [Serializable]
    public struct UnityEngine_TextCore_FaceInfo
    {
        [SerializeField] int m_FaceIndex;
        [SerializeField] string m_FamilyName;
        [SerializeField] string m_StyleName;
        [SerializeField] int m_PointSize;
        [SerializeField] float m_Scale;
        [SerializeField] int m_UnitsPerEM;
        [SerializeField] float m_LineHeight;
        [SerializeField] float m_AscentLine;
        [SerializeField] float m_CapLine;
        [SerializeField] float m_MeanLine;
        [SerializeField] float m_Baseline;
        [SerializeField] float m_DescentLine;
        [SerializeField] float m_SuperscriptOffset;
        [SerializeField] float m_SuperscriptSize;
        [SerializeField] float m_SubscriptOffset;
        [SerializeField] float m_SubscriptSize;
        [SerializeField] float m_UnderlineOffset;
        [SerializeField] float m_UnderlineThickness;
        [SerializeField] float m_StrikethroughOffset;
        [SerializeField] float m_StrikethroughThickness;
        [SerializeField] float m_TabWidth;

        public int faceIndex { get { return m_FaceIndex; } set { m_FaceIndex = value; } }
        public string familyName { get { return m_FamilyName; } set { m_FamilyName = value; } }
        public string styleName { get { return m_StyleName; } set { m_StyleName = value; } }
        public int pointSize { get { return m_PointSize; } set { m_PointSize = value; } }
        public float scale { get { return m_Scale; } set { m_Scale = value; } }
        public int unitsPerEM { get { return m_UnitsPerEM; } set { m_UnitsPerEM = value; } }
        public float lineHeight { get { return m_LineHeight; } set { m_LineHeight = value; } }
        public float ascentLine { get { return m_AscentLine; } set { m_AscentLine = value; } }
        public float capLine { get { return m_CapLine; } set { m_CapLine = value; } }
        public float meanLine { get { return m_MeanLine; } set { m_MeanLine = value; } }
        public float baseline { get { return m_Baseline; } set { m_Baseline = value; } }
        public float descentLine { get { return m_DescentLine; } set { m_DescentLine = value; } }
        public float superscriptOffset { get { return m_SuperscriptOffset; } set { m_SuperscriptOffset = value; } }
        public float superscriptSize { get { return m_SuperscriptSize; } set { m_SuperscriptSize = value; } }
        public float subscriptOffset { get { return m_SubscriptOffset; } set { m_SubscriptOffset = value; } }
        public float subscriptSize { get { return m_SubscriptSize; } set { m_SubscriptSize = value; } }
        public float underlineOffset { get { return m_UnderlineOffset; } set { m_UnderlineOffset = value; } }
        public float underlineThickness { get { return m_UnderlineThickness; } set { m_UnderlineThickness = value; } }
        public float strikethroughOffset { get { return m_StrikethroughOffset; } set { m_StrikethroughOffset = value; } }
        public float strikethroughThickness { get { return m_StrikethroughThickness; } set { m_StrikethroughThickness = value; } }
        public float tabWidth { get { return m_TabWidth; } set { m_TabWidth = value; } }

        public bool Compare(UnityEngine_TextCore_FaceInfo other)
        {
            return m_FamilyName == other.m_FamilyName && m_StyleName == other.m_StyleName && m_PointSize == other.m_PointSize &&
                   m_Scale == other.m_Scale && m_UnitsPerEM == other.m_UnitsPerEM && m_LineHeight == other.m_LineHeight &&
                   m_AscentLine == other.m_AscentLine && m_CapLine == other.m_CapLine && m_MeanLine == other.m_MeanLine &&
                   m_Baseline == other.m_Baseline && m_DescentLine == other.m_DescentLine &&
                   m_SuperscriptOffset == other.m_SuperscriptOffset && m_SuperscriptSize == other.m_SuperscriptSize &&
                   m_SubscriptOffset == other.m_SubscriptOffset && m_SubscriptSize == other.m_SubscriptSize &&
                   m_UnderlineOffset == other.m_UnderlineOffset && m_UnderlineThickness == other.m_UnderlineThickness &&
                   m_StrikethroughOffset == other.m_StrikethroughOffset &&
                   m_StrikethroughThickness == other.m_StrikethroughThickness && m_TabWidth == other.m_TabWidth;
        }
    }

    [Serializable]
    public struct UnityEngine_TextCore_GlyphRect : IEquatable<UnityEngine_TextCore_GlyphRect>
    {
        [SerializeField] int m_X;
        [SerializeField] int m_Y;
        [SerializeField] int m_Width;
        [SerializeField] int m_Height;

        public int x { get { return m_X; } set { m_X = value; } }
        public int y { get { return m_Y; } set { m_Y = value; } }
        public int width { get { return m_Width; } set { m_Width = value; } }
        public int height { get { return m_Height; } set { m_Height = value; } }
        public static UnityEngine_TextCore_GlyphRect zero { get { return new UnityEngine_TextCore_GlyphRect(); } }

        public UnityEngine_TextCore_GlyphRect(int x, int y, int width, int height)
        {
            m_X = x;
            m_Y = y;
            m_Width = width;
            m_Height = height;
        }

        public UnityEngine_TextCore_GlyphRect(Rect rect) : this((int)rect.x, (int)rect.y, (int)rect.width, (int)rect.height) { }

        public bool Equals(UnityEngine_TextCore_GlyphRect other)
        {
            return m_X == other.m_X && m_Y == other.m_Y && m_Width == other.m_Width && m_Height == other.m_Height;
        }

        public override bool Equals(object obj) { return obj is UnityEngine_TextCore_GlyphRect && Equals((UnityEngine_TextCore_GlyphRect)obj); }
        public override int GetHashCode() { return ((m_X * 31 + m_Y) * 31 + m_Width) * 31 + m_Height; }
        public static bool operator ==(UnityEngine_TextCore_GlyphRect lhs, UnityEngine_TextCore_GlyphRect rhs) { return lhs.Equals(rhs); }
        public static bool operator !=(UnityEngine_TextCore_GlyphRect lhs, UnityEngine_TextCore_GlyphRect rhs) { return !lhs.Equals(rhs); }
    }

    [Serializable]
    public struct UnityEngine_TextCore_GlyphMetrics : IEquatable<UnityEngine_TextCore_GlyphMetrics>
    {
        [SerializeField] float m_Width;
        [SerializeField] float m_Height;
        [SerializeField] float m_HorizontalBearingX;
        [SerializeField] float m_HorizontalBearingY;
        [SerializeField] float m_HorizontalAdvance;

        public float width { get { return m_Width; } set { m_Width = value; } }
        public float height { get { return m_Height; } set { m_Height = value; } }
        public float horizontalBearingX { get { return m_HorizontalBearingX; } set { m_HorizontalBearingX = value; } }
        public float horizontalBearingY { get { return m_HorizontalBearingY; } set { m_HorizontalBearingY = value; } }
        public float horizontalAdvance { get { return m_HorizontalAdvance; } set { m_HorizontalAdvance = value; } }

        public UnityEngine_TextCore_GlyphMetrics(float width, float height, float bearingX, float bearingY, float advance)
        {
            m_Width = width;
            m_Height = height;
            m_HorizontalBearingX = bearingX;
            m_HorizontalBearingY = bearingY;
            m_HorizontalAdvance = advance;
        }

        public bool Equals(UnityEngine_TextCore_GlyphMetrics other)
        {
            return m_Width == other.m_Width && m_Height == other.m_Height && m_HorizontalBearingX == other.m_HorizontalBearingX &&
                   m_HorizontalBearingY == other.m_HorizontalBearingY && m_HorizontalAdvance == other.m_HorizontalAdvance;
        }

        public override bool Equals(object obj) { return obj is UnityEngine_TextCore_GlyphMetrics && Equals((UnityEngine_TextCore_GlyphMetrics)obj); }
        public override int GetHashCode() { return m_Width.GetHashCode() ^ m_Height.GetHashCode() * 7 ^ m_HorizontalAdvance.GetHashCode() * 13; }
        public static bool operator ==(UnityEngine_TextCore_GlyphMetrics lhs, UnityEngine_TextCore_GlyphMetrics rhs) { return lhs.Equals(rhs); }
        public static bool operator !=(UnityEngine_TextCore_GlyphMetrics lhs, UnityEngine_TextCore_GlyphMetrics rhs) { return !lhs.Equals(rhs); }
    }

    public enum UnityEngine_TextCore_GlyphClassDefinitionType { Undefined = 0, Base = 1, Ligature = 2, Mark = 3, Component = 4 }

    [Serializable]
    public class UnityEngine_TextCore_Glyph
    {
        [SerializeField] uint m_Index;
        [SerializeField] UnityEngine_TextCore_GlyphMetrics m_Metrics;
        [SerializeField] UnityEngine_TextCore_GlyphRect m_GlyphRect;
        [SerializeField] float m_Scale;
        [SerializeField] int m_AtlasIndex;
        [SerializeField] UnityEngine_TextCore_GlyphClassDefinitionType m_ClassDefinitionType;

        public uint index { get { return m_Index; } set { m_Index = value; } }
        public UnityEngine_TextCore_GlyphMetrics metrics { get { return m_Metrics; } set { m_Metrics = value; } }
        public UnityEngine_TextCore_GlyphRect glyphRect { get { return m_GlyphRect; } set { m_GlyphRect = value; } }
        public float scale { get { return m_Scale; } set { m_Scale = value; } }
        public int atlasIndex { get { return m_AtlasIndex; } set { m_AtlasIndex = value; } }
        public UnityEngine_TextCore_GlyphClassDefinitionType classDefinitionType { get { return m_ClassDefinitionType; } set { m_ClassDefinitionType = value; } }

        public UnityEngine_TextCore_Glyph() { m_Scale = 1f; }

        public UnityEngine_TextCore_Glyph(UnityEngine_TextCore_Glyph glyph)
        {
            m_Index = glyph.m_Index;
            m_Metrics = glyph.m_Metrics;
            m_GlyphRect = glyph.m_GlyphRect;
            m_Scale = glyph.m_Scale;
            m_AtlasIndex = glyph.m_AtlasIndex;
            m_ClassDefinitionType = glyph.m_ClassDefinitionType;
        }

        public UnityEngine_TextCore_Glyph(uint index, UnityEngine_TextCore_GlyphMetrics metrics, UnityEngine_TextCore_GlyphRect glyphRect)
            : this(index, metrics, glyphRect, 1f, 0) { }

        public UnityEngine_TextCore_Glyph(uint index, UnityEngine_TextCore_GlyphMetrics metrics, UnityEngine_TextCore_GlyphRect glyphRect, float scale, int atlasIndex)
        {
            m_Index = index;
            m_Metrics = metrics;
            m_GlyphRect = glyphRect;
            m_Scale = scale;
            m_AtlasIndex = atlasIndex;
        }

        public bool Compare(UnityEngine_TextCore_Glyph other)
        {
            return m_Index == other.m_Index && m_Metrics == other.m_Metrics && m_GlyphRect == other.m_GlyphRect &&
                   m_Scale == other.m_Scale && m_AtlasIndex == other.m_AtlasIndex;
        }
    }

    [Serializable]
    public struct UnityEngine_TextCore_LowLevel_GlyphValueRecord : IEquatable<UnityEngine_TextCore_LowLevel_GlyphValueRecord>
    {
        [SerializeField] float m_XPlacement;
        [SerializeField] float m_YPlacement;
        [SerializeField] float m_XAdvance;
        [SerializeField] float m_YAdvance;

        public float xPlacement { get { return m_XPlacement; } set { m_XPlacement = value; } }
        public float yPlacement { get { return m_YPlacement; } set { m_YPlacement = value; } }
        public float xAdvance { get { return m_XAdvance; } set { m_XAdvance = value; } }
        public float yAdvance { get { return m_YAdvance; } set { m_YAdvance = value; } }

        public UnityEngine_TextCore_LowLevel_GlyphValueRecord(float xPlacement, float yPlacement, float xAdvance, float yAdvance)
        {
            m_XPlacement = xPlacement;
            m_YPlacement = yPlacement;
            m_XAdvance = xAdvance;
            m_YAdvance = yAdvance;
        }

        public static UnityEngine_TextCore_LowLevel_GlyphValueRecord operator +(UnityEngine_TextCore_LowLevel_GlyphValueRecord a, UnityEngine_TextCore_LowLevel_GlyphValueRecord b)
        {
            return new UnityEngine_TextCore_LowLevel_GlyphValueRecord(a.m_XPlacement + b.m_XPlacement, a.m_YPlacement + b.m_YPlacement,
                a.m_XAdvance + b.m_XAdvance, a.m_YAdvance + b.m_YAdvance);
        }

        public static UnityEngine_TextCore_LowLevel_GlyphValueRecord operator *(UnityEngine_TextCore_LowLevel_GlyphValueRecord a, float emScale)
        {
            return new UnityEngine_TextCore_LowLevel_GlyphValueRecord(a.m_XPlacement * emScale, a.m_YPlacement * emScale,
                a.m_XAdvance * emScale, a.m_YAdvance * emScale);
        }

        public bool Equals(UnityEngine_TextCore_LowLevel_GlyphValueRecord other)
        {
            return m_XPlacement == other.m_XPlacement && m_YPlacement == other.m_YPlacement &&
                   m_XAdvance == other.m_XAdvance && m_YAdvance == other.m_YAdvance;
        }

        public override bool Equals(object obj) { return obj is UnityEngine_TextCore_LowLevel_GlyphValueRecord && Equals((UnityEngine_TextCore_LowLevel_GlyphValueRecord)obj); }
        public override int GetHashCode() { return m_XPlacement.GetHashCode() ^ m_YPlacement.GetHashCode() * 7 ^ m_XAdvance.GetHashCode() * 13 ^ m_YAdvance.GetHashCode() * 17; }
        public static bool operator ==(UnityEngine_TextCore_LowLevel_GlyphValueRecord lhs, UnityEngine_TextCore_LowLevel_GlyphValueRecord rhs) { return lhs.Equals(rhs); }
        public static bool operator !=(UnityEngine_TextCore_LowLevel_GlyphValueRecord lhs, UnityEngine_TextCore_LowLevel_GlyphValueRecord rhs) { return !lhs.Equals(rhs); }
    }

    [Serializable]
    public struct UnityEngine_TextCore_LowLevel_GlyphAdjustmentRecord : IEquatable<UnityEngine_TextCore_LowLevel_GlyphAdjustmentRecord>
    {
        [SerializeField] uint m_GlyphIndex;
        [SerializeField] UnityEngine_TextCore_LowLevel_GlyphValueRecord m_GlyphValueRecord;

        public uint glyphIndex { get { return m_GlyphIndex; } set { m_GlyphIndex = value; } }
        public UnityEngine_TextCore_LowLevel_GlyphValueRecord glyphValueRecord { get { return m_GlyphValueRecord; } set { m_GlyphValueRecord = value; } }

        public UnityEngine_TextCore_LowLevel_GlyphAdjustmentRecord(uint glyphIndex, UnityEngine_TextCore_LowLevel_GlyphValueRecord glyphValueRecord)
        {
            m_GlyphIndex = glyphIndex;
            m_GlyphValueRecord = glyphValueRecord;
        }

        public bool Equals(UnityEngine_TextCore_LowLevel_GlyphAdjustmentRecord other)
        {
            return m_GlyphIndex == other.m_GlyphIndex && m_GlyphValueRecord == other.m_GlyphValueRecord;
        }

        public override bool Equals(object obj) { return obj is UnityEngine_TextCore_LowLevel_GlyphAdjustmentRecord && Equals((UnityEngine_TextCore_LowLevel_GlyphAdjustmentRecord)obj); }
        public override int GetHashCode() { return (int)m_GlyphIndex ^ m_GlyphValueRecord.GetHashCode() * 7; }
        public static bool operator ==(UnityEngine_TextCore_LowLevel_GlyphAdjustmentRecord lhs, UnityEngine_TextCore_LowLevel_GlyphAdjustmentRecord rhs) { return lhs.Equals(rhs); }
        public static bool operator !=(UnityEngine_TextCore_LowLevel_GlyphAdjustmentRecord lhs, UnityEngine_TextCore_LowLevel_GlyphAdjustmentRecord rhs) { return !lhs.Equals(rhs); }
    }

    [Flags]
    public enum UnityEngine_TextCore_LowLevel_FontFeatureLookupFlags { None = 0, IgnoreLigatures = 4, IgnoreSpacingAdjustments = 0x100 }

    [Serializable]
    public struct UnityEngine_TextCore_LowLevel_GlyphPairAdjustmentRecord : IEquatable<UnityEngine_TextCore_LowLevel_GlyphPairAdjustmentRecord>
    {
        [SerializeField] UnityEngine_TextCore_LowLevel_GlyphAdjustmentRecord m_FirstAdjustmentRecord;
        [SerializeField] UnityEngine_TextCore_LowLevel_GlyphAdjustmentRecord m_SecondAdjustmentRecord;
        [SerializeField] UnityEngine_TextCore_LowLevel_FontFeatureLookupFlags m_FeatureLookupFlags;

        public UnityEngine_TextCore_LowLevel_GlyphAdjustmentRecord firstAdjustmentRecord { get { return m_FirstAdjustmentRecord; } set { m_FirstAdjustmentRecord = value; } }
        public UnityEngine_TextCore_LowLevel_GlyphAdjustmentRecord secondAdjustmentRecord { get { return m_SecondAdjustmentRecord; } set { m_SecondAdjustmentRecord = value; } }
        public UnityEngine_TextCore_LowLevel_FontFeatureLookupFlags featureLookupFlags { get { return m_FeatureLookupFlags; } set { m_FeatureLookupFlags = value; } }

        public UnityEngine_TextCore_LowLevel_GlyphPairAdjustmentRecord(UnityEngine_TextCore_LowLevel_GlyphAdjustmentRecord firstAdjustmentRecord,
                                                                       UnityEngine_TextCore_LowLevel_GlyphAdjustmentRecord secondAdjustmentRecord)
        {
            m_FirstAdjustmentRecord = firstAdjustmentRecord;
            m_SecondAdjustmentRecord = secondAdjustmentRecord;
            m_FeatureLookupFlags = UnityEngine_TextCore_LowLevel_FontFeatureLookupFlags.None;
        }

        public bool Equals(UnityEngine_TextCore_LowLevel_GlyphPairAdjustmentRecord other)
        {
            return m_FirstAdjustmentRecord == other.m_FirstAdjustmentRecord && m_SecondAdjustmentRecord == other.m_SecondAdjustmentRecord;
        }

        public override bool Equals(object obj) { return obj is UnityEngine_TextCore_LowLevel_GlyphPairAdjustmentRecord && Equals((UnityEngine_TextCore_LowLevel_GlyphPairAdjustmentRecord)obj); }
        public override int GetHashCode() { return m_FirstAdjustmentRecord.GetHashCode() ^ m_SecondAdjustmentRecord.GetHashCode() * 7; }
        public static bool operator ==(UnityEngine_TextCore_LowLevel_GlyphPairAdjustmentRecord lhs, UnityEngine_TextCore_LowLevel_GlyphPairAdjustmentRecord rhs) { return lhs.Equals(rhs); }
        public static bool operator !=(UnityEngine_TextCore_LowLevel_GlyphPairAdjustmentRecord lhs, UnityEngine_TextCore_LowLevel_GlyphPairAdjustmentRecord rhs) { return !lhs.Equals(rhs); }
    }

    // The values TMP passes and tests; the font engine is absent, so the others never occur.
    public enum UnityEngine_TextCore_LowLevel_FontEngineError
    {
        Success = 0, Invalid_File_Path = 1, Invalid_File_Format = 2, Invalid_File_Structure = 3, Invalid_File = 4,
        Invalid_Table = 8, Invalid_Glyph_Index = 16, Invalid_Character_Code = 17, Invalid_Pixel_Size = 23,
        Invalid_Library = 33, Invalid_Face = 35, Invalid_Library_or_Face = 41, Atlas_Generation_Cancelled = 100,
        Invalid_SharedTextureData = 101, OpenTypeLayoutLookup_Mismatch = 116,
    }

    public enum UnityEngine_TextCore_LowLevel_GlyphRenderMode
    {
        SMOOTH_HINTED = 4121, SMOOTH = 4117, COLOR_HINTED = 69656, COLOR = 69652, RASTER_HINTED = 4122, RASTER = 4118,
        SDF = 4134, SDF8 = 8230, SDF16 = 16422, SDF32 = 32806, SDFAA_HINTED = 4169, SDFAA = 4165,
    }

    [Flags]
    public enum UnityEngine_TextCore_LowLevel_GlyphLoadFlags
    {
        LOAD_DEFAULT = 0, LOAD_NO_SCALE = 1, LOAD_NO_HINTING = 2, LOAD_RENDER = 4, LOAD_NO_BITMAP = 8,
        LOAD_FORCE_AUTOHINT = 32, LOAD_MONOCHROME = 4096, LOAD_NO_AUTOHINT = 32768, LOAD_COLOR = 1048576,
        LOAD_COMPUTE_METRICS = 2097152, LOAD_BITMAP_METRICS_ONLY = 4194304,
    }

    public enum UnityEngine_TextCore_LowLevel_GlyphPackingMode { BestShortSideFit = 0, BestLongSideFit = 1, BestAreaFit = 2, BottomLeftRule = 3, ContactPointRule = 4 }
    // No font engine: dynamic font assets add no glyphs at run time and keep those in their atlas.
    public static class UnityEngine_TextCore_LowLevel_FontEngine
    {
        const UnityEngine_TextCore_LowLevel_FontEngineError Unavailable = UnityEngine_TextCore_LowLevel_FontEngineError.Invalid_Library;

        public static UnityEngine_TextCore_LowLevel_FontEngineError InitializeFontEngine() { return Unavailable; }
        public static UnityEngine_TextCore_LowLevel_FontEngineError LoadFontFace(Font font, int pointSize) { return Unavailable; }
        public static UnityEngine_TextCore_FaceInfo GetFaceInfo() { return new UnityEngine_TextCore_FaceInfo(); }
        public static uint GetGlyphIndex(uint unicode) { return 0; }
        public static void ResetAtlasTexture(Texture2D texture) { }

        public static bool TryGetGlyphWithUnicodeValue(uint unicode, UnityEngine_TextCore_LowLevel_GlyphLoadFlags flags, out UnityEngine_TextCore_Glyph glyph)
        {
            glyph = null;
            return false;
        }

        public static bool TryGetGlyphWithIndexValue(uint glyphIndex, UnityEngine_TextCore_LowLevel_GlyphLoadFlags flags, out UnityEngine_TextCore_Glyph glyph)
        {
            glyph = null;
            return false;
        }

        public static bool TryAddGlyphToTexture(uint glyphIndex, int padding, UnityEngine_TextCore_LowLevel_GlyphPackingMode packingMode,
            System.Collections.Generic.List<UnityEngine_TextCore_GlyphRect> freeGlyphRects, System.Collections.Generic.List<UnityEngine_TextCore_GlyphRect> usedGlyphRects,
            UnityEngine_TextCore_LowLevel_GlyphRenderMode renderMode, Texture2D texture, out UnityEngine_TextCore_Glyph glyph)
        {
            glyph = null;
            return false;
        }

        public static bool TryAddGlyphsToTexture(System.Collections.Generic.List<uint> glyphIndexes, int padding, UnityEngine_TextCore_LowLevel_GlyphPackingMode packingMode,
            System.Collections.Generic.List<UnityEngine_TextCore_GlyphRect> freeGlyphRects, System.Collections.Generic.List<UnityEngine_TextCore_GlyphRect> usedGlyphRects,
            UnityEngine_TextCore_LowLevel_GlyphRenderMode renderMode, Texture2D texture, out UnityEngine_TextCore_Glyph[] glyphs)
        {
            glyphs = null;
            return false;
        }

        public static UnityEngine_TextCore_LowLevel_GlyphPairAdjustmentRecord[] GetGlyphPairAdjustmentTable(uint[] glyphIndexes)
        {
            return new UnityEngine_TextCore_LowLevel_GlyphPairAdjustmentRecord[0];
        }

        public static UnityEngine_TextCore_LowLevel_GlyphPairAdjustmentRecord[] GetGlyphPairAdjustmentRecords(System.Collections.Generic.List<uint> glyphIndexes, out int recordCount)
        {
            recordCount = 0;
            return new UnityEngine_TextCore_LowLevel_GlyphPairAdjustmentRecord[0];
        }
    }
}
