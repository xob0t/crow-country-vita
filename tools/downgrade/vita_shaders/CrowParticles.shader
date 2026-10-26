// The PS Vita's Particles/Standard Surface. Unity's (2018.2's built-in, a surface shader over
// UnityStandardParticles.cginc) has no PS Vita programs: the editor compiles none for psp2, its own at
// target 3.5 nor a copy at 3.0 (23 Oct: "psp2 (total internal programs: 0)", no error), so its materials
// drew with its fallback, VertexLit: opaque quads (the Crypt's water rings, bubbles, splashes, leaves and
// blobs, the crows' feathers, the enemies' death puffs, Scamper's prints). The PS Vita build saves those
// materials with this one (asset_rules.vita_particle_bake). It draws what they use: the texture times the
// particle's colour and _Color, alpha blended (Fade: all 9 of the game's such materials; the cut-out,
// premultiplied and emission variants serve any other material), lit per vertex as Particles/Standard Surface's forward base pass lights a particle (the main
// directional light, the harmonics, the four point lights Unity gives each object per vertex; diffuse
// only, Standard's dielectric factor) plus CrowVita/Lit's fill and key lights (compat/VitaLit.cs: those
// lights' culling masks are 0, so Unity gives them to no shader), and fog. Left out (no material uses
// them): flipbook blending, soft particles, camera fading, distortion, normal and emission maps.
Shader "CrowVita/Particles Lit"
{
    Properties
    {
        _MainTex("Albedo", 2D) = "white" {}
        _Color("Color", Color) = (1,1,1,1)
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
        [Gamma] _Metallic("Metallic", Range(0.0, 1.0)) = 0.0
        _EmissionColor("Color", Color) = (0,0,0)

        [HideInInspector] _Mode ("__mode", Float) = 0.0
        [HideInInspector] _BlendOp ("__blendop", Float) = 0.0
        [HideInInspector] _SrcBlend ("__src", Float) = 1.0
        [HideInInspector] _DstBlend ("__dst", Float) = 0.0
        [HideInInspector] _ZWrite ("__zw", Float) = 1.0
        [HideInInspector] _Cull ("__cull", Float) = 2.0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "IgnoreProjector"="True" "PreviewType"="Plane" "PerformanceChecks"="False" }

        Pass
        {
            Name "FORWARD"
            Tags { "LightMode" = "ForwardBase" }

            BlendOp [_BlendOp]
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull [_Cull]

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature _ _ALPHATEST_ON _ALPHABLEND_ON _ALPHAPREMULTIPLY_ON
            #pragma shader_feature _EMISSION
            #pragma multi_compile __ VERTEXLIGHT_ON
            #pragma multi_compile_fog

            #include "UnityCG.cginc"
            #include "UnityLightingCommon.cginc"
            #include "UnityStandardUtils.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            half4 _Color;
            half _Cutoff;
            half _Metallic;
            half4 _EmissionColor;

            // CrowVita/Lit's globals (vita_shaders/CrowLit.shader): the key lights side by side, a light in each
            // lane (unused lanes far away and black), and the fill directional light.
            float4 _CrowKeyX, _CrowKeyY, _CrowKeyZ, _CrowKeyW;
            half4 _CrowKeyDX, _CrowKeyDY, _CrowKeyDZ, _CrowKeyDW;
            half4 _CrowKeyColor[4];
            half4 _CrowFill;
            half4 _CrowFillColor;
            // CrowLit.shader's key light falloff.
            #define CROW_KEY_ATTEN 25.0
            #define CROW_KEY_FADE 2.7778

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
                half3 light : TEXCOORD1;
                UNITY_FOG_COORDS(2)
            };

            v2f vert(appdata v)
            {
                v2f o;
                float3 p = mul(unity_ObjectToWorld, v.vertex).xyz;
                half3 n = UnityObjectToWorldNormal(v.normal);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                o.color = v.color * _Color;
                half3 light = ShadeSH9(half4(n, 1));
                light += _LightColor0.rgb * saturate(dot(n, _WorldSpaceLightPos0.xyz));
                #if defined(VERTEXLIGHT_ON)
                    light += Shade4PointLights(unity_4LightPosX0, unity_4LightPosY0, unity_4LightPosZ0,
                        unity_LightColor[0].rgb, unity_LightColor[1].rgb, unity_LightColor[2].rgb, unity_LightColor[3].rgb,
                        unity_4LightAtten0, p, n);
                #endif
                light += _CrowFillColor.rgb * saturate(dot(n, _CrowFill.xyz));
                // The key lights as CrowVita/Lit works them out per pixel (distance falloff, the spot cone, Lambert).
                float4 tx = _CrowKeyX - p.x;
                float4 ty = _CrowKeyY - p.y;
                float4 tz = _CrowKeyZ - p.z;
                float4 d2 = tx * tx + ty * ty + tz * tz;
                half4 r = rsqrt(max(d2, 0.000001));
                half4 ndl = (tx * n.x + ty * n.y + tz * n.z) * r;
                half4 cone = _CrowKeyDW - (tx * _CrowKeyDX + ty * _CrowKeyDY + tz * _CrowKeyDZ) * r;
                half4 x = d2 * _CrowKeyW;
                half4 w = saturate((1.0 - x) * CROW_KEY_FADE) / (1.0 + CROW_KEY_ATTEN * x) * saturate(cone) * saturate(ndl);
                light += _CrowKeyColor[0].rgb * w.x + _CrowKeyColor[1].rgb * w.y + _CrowKeyColor[2].rgb * w.z + _CrowKeyColor[3].rgb * w.w;
                o.light = light * OneMinusReflectivityFromMetallic(_Metallic);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half4 albedo = tex2D(_MainTex, i.uv) * i.color;
                #if defined(_ALPHATEST_ON)
                    clip(albedo.a - _Cutoff);
                #endif
                half4 c = half4(albedo.rgb * i.light, albedo.a);
                #if defined(_EMISSION)
                    c.rgb += _EmissionColor.rgb;
                #endif
                #if defined(_ALPHAPREMULTIPLY_ON)
                    c.rgb *= c.a;
                #endif
                #if !defined(_ALPHABLEND_ON) && !defined(_ALPHAPREMULTIPLY_ON)
                    c.a = 1;
                #endif
                UNITY_APPLY_FOG(i.fogCoord, c);
                return c;
            }
            ENDCG
        }
    }

    Fallback Off
}
