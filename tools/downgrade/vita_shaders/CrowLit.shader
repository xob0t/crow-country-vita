// The PS Vita's Standard: Unity 2018.2's built-in Standard shader in its simple form (the one
// Unity compiles for low-end tiers; MIT, Unity Technologies: UnityStandardCoreForwardSimple.cginc
// and UnityStandardShadow.cginc) drawing every light in one pass. Built-in forward rendering draws
// an object once more for each pixel light after the main directional one (ForwardAdd); on the PS
// Vita each of those draws costs the main thread about as much as the object itself, and the GPU a
// second fill of its pixels. This shader has no ForwardAdd pass. Its base pass draws the main
// directional light with Standard's BRDF2 highlight (its mobile GGX, in gamma space) and simple indirect
// light, the harmonics and the four point lights Unity gives each object per vertex (as Standard does),
// and adds per pixel, Lambert only (over the specular colour too, where the material has highlights):
// - up to four "key" lights CrowCompat sets as globals (compat/VitaLit.cs): spot lights (with
//   their cone) and point lights that reach the camera's view, which Unity no longer sees (their
//   culling mask is 0 while they are key lights, and the shader leaves a key out of a material drawn on
//   a layer its mask leaves out); CROW_KEYS_2/4 say how many. The lights are
//   worked out side by side, one in each lane of float4s (_CrowKeyX/Y/Z: positions; _CrowKeyW:
//   1/range^2; _CrowKeyDX/DY/DZ/DW: the cone), the SGX's vector instructions doing four at once:
//   4 key lights cost 50 cycles a pixel instead of 87 one after another, 2 lights 34 instead of 43
//   (psp2shaderperf), with the same sums;
// - one fill directional light (_CrowFill).
// Distance falloff and the spot cone follow Unity's lookup textures (1/(1+25 d^2/r^2), faded to 0
// from 80% of the range; the default spot cookie's soft edge as a linear ramp in the cone's cosine).
// No material of the game has a normal, parallax, occlusion, detail mask or emission map, so the base
// pass reads none: Standard's occlusion and detail mask reads would fetch Unity's white default.
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
        _EmissionColor("Color", Color) = (0,0,0)
        _EmissionMap("Emission", 2D) = "white" {}
        _DetailAlbedoMap("Detail Albedo x2", 2D) = "grey" {}
        [Enum(UV0,0,UV1,1)] _UVSec ("UV Set for secondary textures", Float) = 0
        [HideInInspector] _Mode ("__mode", Float) = 0.0
        [HideInInspector] _SrcBlend ("__src", Float) = 1.0
        [HideInInspector] _DstBlend ("__dst", Float) = 0.0
        [HideInInspector] _ZWrite ("__zw", Float) = 1.0
        // What compat/VitaLit.cs checks before using the shader (its globals are this version's).
        [HideInInspector] _CrowLitV4 ("", Float) = 0
        // A material the PS Vita build saved with this shader (asset_rules.vita_lit_overlay): the shader
        // it had, 1 Standard, 2 SFBStandard, 3 Legacy Shaders/Diffuse (0: made so at run time or a
        // variant keeper), and a Legacy Diffuse material's own colour; -crow-lit game puts them back.
        [HideInInspector] _CrowLitFrom ("", Float) = 0
        [HideInInspector] _CrowLitColor ("", Color) = (1,1,1,1)
        // The material's depth layer (-1 when it is opaque, asset_rules.vita_lit_bake, PS Vita build): see CrowDepthLayerZ.
        [HideInInspector] _CrowDepthLayer ("", Float) = 0
        // The layer of the renderers that draw the material, when they are all on one (asset_rules.material_layers in
        // the PS Vita build, compat/VitaLit.cs at run time elsewhere) as the scale that brings its bit of a key's mask
        // to the units: 2^-layer for layers 0-15 (Lo), 2^-(layer - 16) for 16-31 (Hi), the other 0. The defaults are
        // Default's (layer 0), which every key light lights.
        [HideInInspector] _CrowLayerLo ("", Float) = 1
        [HideInInspector] _CrowLayerHi ("", Float) = 0
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

            // The keywords the game's Standard materials use.
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

            #pragma multi_compile __ CROW_KEYS_2 CROW_KEYS_4

            #pragma vertex vertCrow
            #pragma fragment fragCrow

            #define UNITY_STANDARD_SIMPLE 1
            #include "UnityStandardCoreForwardSimple.cginc"

            #if defined(CROW_KEYS_4)
                #define CROW_KEYS 4
            #elif defined(CROW_KEYS_2)
                #define CROW_KEYS 2
            #else
                #define CROW_KEYS 0
            #endif

            // Key light k in lane k: X/Y/Z its position, W 1/range^2 (unused lanes: far away); DX/DY/DZ the
            // spot's forward over the cone's ramp width, DW the ramp's offset (points: 0, 1). The cone's are
            // floats (26 Oct, LASER): a laser sight's ramp is a few hundred-thousandths of the cosine wide, so
            // they run to 10^4 and more, and as halves (the PS Vita keeps a half uniform in 16 bits, 8 apart
            // at 10^4) the dot was gone in half the directions Mara aimed and a wider disc in others.
            float4 _CrowKeyX, _CrowKeyY, _CrowKeyZ, _CrowKeyW;
            float4 _CrowKeyDX, _CrowKeyDY, _CrowKeyDZ, _CrowKeyDW;
            half4 _CrowKeyColor[4];   // colour times intensity
            half4 _CrowFill;          // a fill directional light: xyz the direction to it
            half4 _CrowFillColor;
            // Each key light's culling mask in its lane, its low and high 16 bits (27 Oct, ARCADE): a key leaves out the
            // material's layer as the game's light does (each Arcade cabinet's point light 8 cm in front of its screen
            // lights the cabinet, not the screen, and as a key it washed the screen out to white).
            float4 _CrowKeyMaskLo, _CrowKeyMaskHi;
            float _CrowLayerLo, _CrowLayerHi;

            // Depth layers (21 Oct; every opaque material since 29 Oct, NOTE): surfaces laid a few
            // millimetres over others (an end cap 0.3 mm in front of Fairy Forest's pale wall, writing
            // 1 mm over a note's paper, decals on floors and walls) fight on the PS Vita, whose depth
            // test can't separate them where Windows does. A material with layer n
            // has its depth moved toward the camera by n times CROW_DEPTH_LAYER_M d^2 metres at distance d
            // (5.85 mm at 20 m, one 16-bit depth step of the Main Camera's 1-24 m), worked out from the
            // camera's own near and far planes so it is the same in metres for every camera: -1 puts an
            // opaque surface behind the decals and overlays that lie on it. Fog and shadows keep the true
            // depth. _CrowDebug 1 (-crow-lit-debug 1) switches the layers off, for comparisons.
            #define CROW_DEPTH_LAYER_M 1.4624e-5
            float _CrowDepthLayer;
            float _CrowDebug;

            float CrowDepthLayerZ(float w)
            {
                // Window depth changes by n f / (f - n) / d^2 a metre at distance d.
                float b = _ProjectionParams.y * _ProjectionParams.z / (_ProjectionParams.z - _ProjectionParams.y);
                float shift = (_CrowDebug == 1 ? 0 : _CrowDepthLayer) * CROW_DEPTH_LAYER_M * b;
                #if defined(UNITY_REVERSED_Z)
                    return shift * w;           // depth z / w, 1 at the near plane
                #else
                    return -2.0 * shift * w;    // depth (z / w + 1) / 2, 0 at the near plane (PS Vita)
                #endif
            }

            // A key light's distance falloff, x = d^2 / range^2: Unity's 1 / (1 + 25 x), faded to 0 from 80%
            // of the range (1 / (1 - 0.8^2) = 2.7778). vita_shaders/CrowParticles.shader has the same.
            #define CROW_KEY_ATTEN 25.0
            #define CROW_KEY_FADE 2.7778

            // Standard's Albedo with no detail mask (Unity's white default: the detail at full strength).
            half3 CrowAlbedo(float4 texcoords)
            {
                half3 albedo = _Color.rgb * tex2D(_MainTex, texcoords.xy).rgb;
                #if defined(_DETAIL_MULX2)
                    albedo *= tex2D(_DetailAlbedoMap, texcoords.zw).rgb * unity_ColorSpaceDouble.rgb;
                #endif
                return albedo;
            }

            // Standard's MetallicSetup with CrowAlbedo.
            FragmentCommonData CrowSetup(float4 i_tex)
            {
                half2 metallicGloss = MetallicGloss(i_tex.xy);
                FragmentCommonData o = (FragmentCommonData)0;
                o.diffColor = DiffuseAndSpecularFromMetallic(CrowAlbedo(i_tex), metallicGloss.x, /*out*/ o.specColor, /*out*/ o.oneMinusReflectivity);
                o.smoothness = metallicGloss.y;
                return o;
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
                FragmentCommonData s = CrowSetup (i.tex);
                s.diffColor = PreMultiplyAlpha (s.diffColor, alpha, s.oneMinusReflectivity, /*out*/ s.alpha);
                s.normalWorld = normalize(i.normalWorld.xyz);
                s.eyeVec = i.eyeVec.xyz;
                s.posWorld = i.posWorld;
                s.reflUVW = i.fogCoord.yzw;
                s.tangentSpaceNormal = 0;

                UnityLight mainLight = MainLight();
                half ndotl = saturate(dot(s.normalWorld, mainLight.dir));
                half atten = SHADOW_ATTENUATION(i);
                UnityGI gi = FragmentGI (s, 1, i.ambientOrLightmapUV, atten, mainLight);
                #if GLOSSMAP
                    half grazing = saturate(s.smoothness + (1 - s.oneMinusReflectivity));
                #else
                    half grazing = i.eyeVec.w;
                #endif
                // Standard's BRDF2 (Unity's mobile GGX, its gamma-space form) for the main light, where the simple
                // shader's BRDF3 had a narrow lookup lobe (ARCADE, 27 Oct). This project renders in gamma space,
                // where Standard's highlight (BRDF1 on Windows) is the square root of GGX's: a lobe as broad as the
                // surface's roughness allows and about 1 at its flank, over the specular colour (0.22 for
                // dielectrics, most of the albedo for metals). BRDF3's lobe was about 0.7 at its peak and 0 beside
                // it, so shiny items lit by one light drew much darker than the game: the inventory's chain, keys,
                // crank, shotgun and mask (CHAIN's term for metals is replaced by this one). The half vector is the
                // pixel's and the normal normalized per pixel, as Standard has them: from the interpolated normal
                // or R.L, the narrow lobes of smooth surfaces fell short at their peak (the shotgun at half its
                // brightness). With highlights off there is no direct specular, as in Standard.
                half perceptualRoughness = SmoothnessToPerceptualRoughness(s.smoothness);
                half roughness = PerceptualRoughnessToRoughness(perceptualRoughness);
                half3 direct = s.diffColor;
                #if !defined(_SPECULARHIGHLIGHTS_OFF)
                    float3 h = mainLight.dir - s.eyeVec;
                    float hl = rsqrt(max(dot(h, h), 0.0001));
                    half nh = saturate(dot(s.normalWorld, h) * hl);
                    half lh = saturate(dot(mainLight.dir, h) * hl);
                    float d = nh * nh * (roughness * roughness - 1.0) + 1.00001;
                    half specular = roughness / (max(0.32, lh) * (1.5 + roughness) * d);
                    direct += min(specular, 100.0) * s.specColor;
                #endif
                // The indirect light as BRDF3's, with Standard's surface reduction (gamma space) on its specular.
                half3 c = gi.indirect.diffuse * s.diffColor
                        + (1.0 - 0.28 * roughness * perceptualRoughness) * gi.indirect.specular * lerp(s.specColor, grazing, i.normalWorld.w);
                c += direct * (gi.light.color * ndotl);

                // The fill and key lights, Lambert only; with highlights, metals (specular colour, no diffuse)
                // take them as broad highlights. Without, Standard draws no highlight from them: the specular
                // colour lit by them brightened Mansion's metallic rugs by half (highlights off, metallic 0.36-0.38).
                half3 diffuse = _CrowFillColor.rgb * saturate(dot(s.normalWorld, _CrowFill.xyz));
                #if CROW_KEYS > 0
                    // Each lane a light.
                    float3 p = i.posWorld;
                    half3 nw = s.normalWorld;
                    float4 tx = _CrowKeyX - p.x;
                    float4 ty = _CrowKeyY - p.y;
                    float4 tz = _CrowKeyZ - p.z;
                    float4 d2 = tx * tx + ty * ty + tz * tz;
                    float4 r = rsqrt(max(d2, 0.000001));
                    half4 ndl = (tx * nw.x + ty * nw.y + tz * nw.z) * r;
                    half4 cone = _CrowKeyDW - (tx * _CrowKeyDX + ty * _CrowKeyDY + tz * _CrowKeyDZ) * r;
                    half4 x = d2 * _CrowKeyW;
                    half4 w = saturate((1.0 - x) * CROW_KEY_FADE) / (1.0 + CROW_KEY_ATTEN * x) * saturate(cone) * saturate(ndl);
                    // The mask's bit for the material's layer, lane by lane.
                    float4 bits = floor(_CrowKeyMaskLo * _CrowLayerLo + _CrowKeyMaskHi * _CrowLayerHi);
                    w *= frac(bits * 0.5) * 2.0;
                    diffuse += _CrowKeyColor[0].rgb * w.x + _CrowKeyColor[1].rgb * w.y;
                    #if CROW_KEYS > 2
                        diffuse += _CrowKeyColor[2].rgb * w.z + _CrowKeyColor[3].rgb * w.w;
                    #endif
                #endif
                #if defined(_SPECULARHIGHLIGHTS_OFF)
                    c += s.diffColor * diffuse;
                #else
                    c += (s.diffColor + s.specColor) * diffuse;
                #endif

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
