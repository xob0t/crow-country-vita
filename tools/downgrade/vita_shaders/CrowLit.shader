// The PS Vita's Standard: Unity 2018.2's built-in Standard shader in its simple form (the one
// Unity compiles for low-end tiers; MIT, Unity Technologies: UnityStandardCoreForwardSimple.cginc
// and UnityStandardShadow.cginc) drawing every light in one pass. Built-in forward rendering draws
// an object once more for each pixel light after the main directional one (ForwardAdd); on the PS
// Vita each of those draws costs the main thread about as much as the object itself, and the GPU a
// second fill of its pixels. This shader has no ForwardAdd pass. Its base pass draws the main
// directional light with Standard's simple BRDF, the harmonics and the four point lights Unity
// gives each object per vertex (as Standard does), and adds per pixel, Lambert only:
// - up to four "key" lights CrowCompat sets as globals (compat/VitaLit.cs): spot lights (with
//   their cone) and point lights that reach the camera's view, which Unity no longer sees (their
//   culling mask is 0 while they are key lights); CROW_KEYS_2/4 say how many. The lights are
//   worked out side by side, one in each lane of float4s (_CrowKeyX/Y/Z: positions; _CrowKeyW:
//   1/range^2; _CrowKeyDX/DY/DZ/DW: the cone), the SGX's vector instructions doing four at once:
//   4 key lights cost 50 cycles a pixel instead of 87 one after another, 2 lights 34 instead of 43
//   (psp2shaderperf), with the same sums; CROW_KEYS_LOOP is the one-after-another form of 4 lights
//   (_CrowKeyPos/Dir, for comparisons: -crow-lit-soa off);
// - one fill directional light (_CrowFill).
// Distance falloff and the spot cone follow Unity's lookup textures (1/(1+25 d^2/r^2), faded to 0
// from 80% of the range; the default spot cookie's soft edge as a linear ramp in the cone's cosine).
Shader "CrowVita/Lit"
{
    Properties
    {
        _Color("Color", Color) = (1,1,1,1)
        _MainTex("Albedo", 2D) = "white" {}
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
        _Glossiness("Smoothness", Range(0.0, 1.0)) = 0.5
        _GlossMapScale("Smoothness Scale", Range(0.0, 1.0)) = 1.0
        [Enum(Metallic Alpha,0,Albedo Alpha,1)] _SmoothnessTextureChannel ("Smoothness texture channel", Float) = 0
        [Gamma] _Metallic("Metallic", Range(0.0, 1.0)) = 0.0
        _MetallicGlossMap("Metallic", 2D) = "white" {}
        [ToggleOff] _SpecularHighlights("Specular Highlights", Float) = 1.0
        [ToggleOff] _GlossyReflections("Glossy Reflections", Float) = 1.0
        _BumpScale("Scale", Float) = 1.0
        _BumpMap("Normal Map", 2D) = "bump" {}
        _Parallax ("Height Scale", Range (0.005, 0.08)) = 0.02
        _ParallaxMap ("Height Map", 2D) = "black" {}
        _OcclusionStrength("Strength", Range(0.0, 1.0)) = 1.0
        _OcclusionMap("Occlusion", 2D) = "white" {}
        _EmissionColor("Color", Color) = (0,0,0)
        _EmissionMap("Emission", 2D) = "white" {}
        _DetailMask("Detail Mask", 2D) = "white" {}
        _DetailAlbedoMap("Detail Albedo x2", 2D) = "grey" {}
        _DetailNormalMapScale("Scale", Float) = 1.0
        _DetailNormalMap("Normal Map", 2D) = "bump" {}
        [Enum(UV0,0,UV1,1)] _UVSec ("UV Set for secondary textures", Float) = 0
        [HideInInspector] _Mode ("__mode", Float) = 0.0
        [HideInInspector] _SrcBlend ("__src", Float) = 1.0
        [HideInInspector] _DstBlend ("__dst", Float) = 0.0
        [HideInInspector] _ZWrite ("__zw", Float) = 1.0
        // What compat/VitaLit.cs checks before using the shader (its globals are this version's).
        [HideInInspector] _CrowLitV3 ("", Float) = 0
        // A material the PS Vita build saved with this shader (asset_rules.vita_lit_overlay): the shader
        // it had, 1 Standard, 2 SFBStandard, 3 Legacy Shaders/Diffuse (0: made so at run time or a
        // variant keeper), and a Legacy Diffuse material's own colour; -crow-lit game puts them back.
        [HideInInspector] _CrowLitFrom ("", Float) = 0
        [HideInInspector] _CrowLitColor ("", Color) = (1,1,1,1)
        // The material's depth layer (asset_rules.DEPTH_LAYERS, PS Vita build): see CrowDepthLayerZ.
        [HideInInspector] _CrowDepthLayer ("", Float) = 0
    }

    CGINCLUDE
        #define UNITY_SETUP_BRDF_INPUT MetallicSetup
    ENDCG

    SubShader
    {
        Tags { "RenderType"="Opaque" "PerformanceChecks"="False" }
        LOD 300

        Pass
        {
            Name "FORWARD"
            Tags { "LightMode" = "ForwardBase" }

            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]

            CGPROGRAM
            #pragma target 3.0

            // The keywords the game's Standard materials use (no normal, parallax or occlusion maps).
            #pragma shader_feature _ _ALPHATEST_ON _ALPHABLEND_ON _ALPHAPREMULTIPLY_ON
            #pragma shader_feature _EMISSION
            #pragma shader_feature _METALLICGLOSSMAP
            #pragma shader_feature ___ _DETAIL_MULX2
            #pragma shader_feature _ _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature _ _GLOSSYREFLECTIONS_OFF

            // No lightmaps (the game has none in the areas measured: CROW_LIT logs it).
            #pragma multi_compile_fwdbase nolightmap nodynlightmap nodirlightmap noshadowmask
            // Fog as Standard has it.
            #pragma multi_compile_fog

            #pragma multi_compile __ CROW_KEYS_2 CROW_KEYS_4 CROW_KEYS_LOOP

            #pragma vertex vertCrow
            #pragma fragment fragCrow

            #define UNITY_STANDARD_SIMPLE 1
            #include "UnityStandardCoreForwardSimple.cginc"

            #if defined(CROW_KEYS_LOOP)
                #define CROW_KEYS 4
            #elif defined(CROW_KEYS_4)
                #define CROW_KEYS 4
            #elif defined(CROW_KEYS_2)
                #define CROW_KEYS 2
            #else
                #define CROW_KEYS 0
            #endif

            float4 _CrowKeyPos[4];    // xyz position, w 1/range^2 (unused slots: far away)
            half4 _CrowKeyDir[4];     // xyz the spot's forward over the cone's ramp width, w the ramp's offset (points: 0, 1)
            half4 _CrowKeyColor[4];   // colour times intensity
            float4 _CrowKeyX, _CrowKeyY, _CrowKeyZ, _CrowKeyW;   // the same, a light in each lane
            half4 _CrowKeyDX, _CrowKeyDY, _CrowKeyDZ, _CrowKeyDW;
            half4 _CrowFill;          // a fill directional light: xyz the direction to it
            half4 _CrowFillColor;

            // Depth layers (21 Oct): surfaces laid a fraction of a millimetre over others (an end cap
            // 0.3 mm in front of Fairy Forest's pale wall, decals on floors and walls) fight on the PS
            // Vita, whose depth test can't separate them where Windows does. A material with layer n
            // has its depth moved toward the camera by n times 1.4624e-5 d^2 metres at distance d
            // (5.85 mm at 20 m, one 16-bit depth step of the Main Camera's 1-24 m), worked out from the
            // camera's own near and far planes so it is the same in metres for every camera: -1 puts a
            // wall or floor behind what lies on it, +1 lifts a decal. Fog and shadows keep the true
            // depth. _CrowDebug 1 (-crow-lit-debug 1) switches the layers off, for comparisons.
            float _CrowDepthLayer;
            float _CrowDebug;

            float CrowDepthLayerZ(float w)
            {
                // Window depth changes by n f / (f - n) / d^2 a metre at distance d.
                float b = _ProjectionParams.y * _ProjectionParams.z / (_ProjectionParams.z - _ProjectionParams.y);
                float shift = (_CrowDebug == 1 ? 0 : _CrowDepthLayer) * 1.4624e-5 * b;
                #if defined(UNITY_REVERSED_Z)
                    return shift * w;           // depth z / w, 1 at the near plane
                #else
                    return -2.0 * shift * w;    // depth (z / w + 1) / 2, 0 at the near plane (PS Vita)
                #endif
            }

            struct VertexOutputCrow
            {
                UNITY_POSITION(pos);
                float4 tex                          : TEXCOORD0;
                half4 eyeVec                        : TEXCOORD1; // w: grazingTerm
                half4 ambientOrLightmapUV           : TEXCOORD2;
                SHADOW_COORDS(3)
                UNITY_FOG_COORDS_PACKED(4, half4) // x: fogCoord, yzw: reflectVec
                half4 normalWorld                   : TEXCOORD5; // w: fresnelTerm
                float3 posWorld                     : TEXCOORD6;
            };

            VertexOutputCrow vertCrow (VertexInput v)
            {
                VertexOutputCrow o;
                UNITY_INITIALIZE_OUTPUT(VertexOutputCrow, o);
                float4 posWorld = mul(unity_ObjectToWorld, v.vertex);
                o.posWorld = posWorld.xyz;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.tex = TexCoords(v);
                half3 eyeVec = normalize(posWorld.xyz - _WorldSpaceCameraPos);
                half3 normalWorld = UnityObjectToWorldNormal(v.normal);
                o.normalWorld.xyz = normalWorld;
                o.eyeVec.xyz = eyeVec;
                TRANSFER_SHADOW(o);
                o.ambientOrLightmapUV = VertexGIForward(v, posWorld, normalWorld);
                o.fogCoord.yzw = reflect(eyeVec, normalWorld);
                o.normalWorld.w = Pow4(1 - saturate(dot(normalWorld, -eyeVec)));
                #if !GLOSSMAP
                    o.eyeVec.w = saturate(_Glossiness + UNIFORM_REFLECTIVITY());
                #endif
                UNITY_TRANSFER_FOG(o, o.pos);
                o.pos.z += CrowDepthLayerZ(o.pos.w);
                return o;
            }

            half4 fragCrow (VertexOutputCrow i) : SV_Target
            {
                half alpha = Alpha(i.tex.xy);
                #if defined(_ALPHATEST_ON)
                    clip (alpha - _Cutoff);
                #endif
                FragmentCommonData s = UNITY_SETUP_BRDF_INPUT (i.tex);
                s.diffColor = PreMultiplyAlpha (s.diffColor, alpha, s.oneMinusReflectivity, /*out*/ s.alpha);
                s.normalWorld = i.normalWorld.xyz;
                s.eyeVec = i.eyeVec.xyz;
                s.posWorld = i.posWorld;
                s.reflUVW = i.fogCoord.yzw;
                s.tangentSpaceNormal = 0;

                UnityLight mainLight = MainLight();
                half ndotl = saturate(dot(s.normalWorld, mainLight.dir));
                half atten = SHADOW_ATTENUATION(i);
                half occlusion = Occlusion(i.tex.xy);
                half rl = dot(REFLECTVEC_FOR_SPECULAR(i, s), mainLight.dir);
                UnityGI gi = FragmentGI (s, occlusion, i.ambientOrLightmapUV, atten, mainLight);
                #if GLOSSMAP
                    half grazing = saturate(s.smoothness + (1 - s.oneMinusReflectivity));
                #else
                    half grazing = i.eyeVec.w;
                #endif
                half3 c = BRDF3_Indirect(s.diffColor, s.specColor, gi.indirect, grazing, i.normalWorld.w);
                c += BRDF3DirectSimple(s.diffColor, s.specColor, s.smoothness, rl) * (gi.light.color * ndotl);
                // Metals: the main light also as a broad highlight (26 Oct, CHAIN). BRDF3's highlight is a
                // narrow lobe from unity_NHxRoughness (about 0.7 at its peak for a smoothness of 0.5, where
                // Windows' Standard, BRDF1's GGX, spreads about 2 over the curve), and a metal keeps little
                // diffuse (metallic 0.745: a fifth of its albedo), so a metal lit by the main light alone
                // drew nearly black: the inventory's chain, whose only light is the menu's directional one.
                // As the fill and key lights below do, the main light lights the specular colour too, in
                // proportion to how metallic the surface is (0 for dielectrics: their look is unchanged), on
                // materials with highlights: the game darkens many walls and floors with metallic and
                // highlights off (Corridor A's liminal walls and floor, 0.275), which Standard lights with
                // no direct specular at all.
                #if !defined(_SPECULARHIGHLIGHTS_OFF)
                    half metal = saturate(1 - s.oneMinusReflectivity / unity_ColorSpaceDielectricSpec.a);
                    c += s.specColor * (metal * ndotl) * gi.light.color;
                #endif

                // The fill and key lights, Lambert only; metals (specular colour, no diffuse) take them
                // as broad highlights.
                half3 diffuse = _CrowFillColor.rgb * saturate(dot(s.normalWorld, _CrowFill.xyz));
                #if CROW_KEYS > 0 && !defined(CROW_KEYS_LOOP)
                    // Each lane a light: the same sums as the loop below.
                    float3 p = i.posWorld;
                    half3 nw = s.normalWorld;
                    float4 tx = _CrowKeyX - p.x;
                    float4 ty = _CrowKeyY - p.y;
                    float4 tz = _CrowKeyZ - p.z;
                    float4 d2 = tx * tx + ty * ty + tz * tz;
                    half4 r = rsqrt(max(d2, 0.000001));
                    half4 ndl = (tx * nw.x + ty * nw.y + tz * nw.z) * r;
                    half4 cone = _CrowKeyDW - (tx * _CrowKeyDX + ty * _CrowKeyDY + tz * _CrowKeyDZ) * r;
                    half4 x = d2 * _CrowKeyW;
                    half4 w = saturate((1.0 - x) * 2.7778) / (1.0 + 25.0 * x) * saturate(cone) * saturate(ndl);
                    diffuse += _CrowKeyColor[0].rgb * w.x + _CrowKeyColor[1].rgb * w.y;
                    #if CROW_KEYS > 2
                        diffuse += _CrowKeyColor[2].rgb * w.z + _CrowKeyColor[3].rgb * w.w;
                    #endif
                #elif CROW_KEYS > 0
                    float3 posWorld = i.posWorld;
                    UNITY_UNROLL
                    for (int k = 0; k < CROW_KEYS; k++)
                    {
                        float3 toLight = _CrowKeyPos[k].xyz - posWorld;
                        float d2 = dot(toLight, toLight);
                        half3 dir = toLight * rsqrt(max(d2, 0.000001));
                        half x = d2 * _CrowKeyPos[k].w;
                        half a = saturate((1.0 - x) * 2.7778) / (1.0 + 25.0 * x);
                        a *= saturate(dot(-dir, _CrowKeyDir[k].xyz) + _CrowKeyDir[k].w);
                        diffuse += _CrowKeyColor[k].rgb * (a * saturate(dot(s.normalWorld, dir)));
                    }
                #endif
                c += (s.diffColor + s.specColor) * diffuse;

                c += Emission(i.tex.xy);
                UNITY_APPLY_FOG(i.fogCoord, c);
                return OutputForward (half4(c, 1), s.alpha);
            }
            ENDCG
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On ZTest LEqual

            CGPROGRAM
            #pragma target 3.0
            #pragma shader_feature _ _ALPHATEST_ON _ALPHABLEND_ON _ALPHAPREMULTIPLY_ON
            #pragma shader_feature _METALLICGLOSSMAP
            #pragma multi_compile_shadowcaster
            #pragma vertex vertShadowCaster
            #pragma fragment fragShadowCaster
            #include "UnityStandardShadow.cginc"
            ENDCG
        }
    }
}
