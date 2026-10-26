// The camera effect's filters (com.sfbgames.crowcountry.CrowCountryCamEffect, SFB/CC01-CC04) for the
// PS Vita's SGX543, drawn by compat/CamEffect.cs in three passes instead of four, with the same
// results. The game's chain, at the PS Vita's half size: SFB (camera -> 550x400: grain, CRT mask,
// tint), PSX (-> 624x351: ordered dither, 16 levels), blur (-> 624x351: a chromatic cross blur from
// half-texel bilinear reads) and CRT (-> 416x234: noise, colour matrix, vignette), with the game's
// _switch = 1 (every camera effect in the game has isSwitch on, and its shaders then skip the CRT's
// glow). The game's fragment programs compute their texture coordinates (dependent reads, which the
// SGX can't fetch ahead), index a constant array for the dither and hash noise with sin and cos at
// full precision: SFB 16 cycles and 2 dependent reads, PSX 47 cycles, blur 33 and 4, CRT 72
// (psp2shaderperf). Here every texture coordinate is a varying the vertex program computes:
// - pass 0, SFB and PSX in one at 550x400: the PSX filter reads the 550x400 picture point-sampled
//   and dithers by its source texel, so its result is one value per 550x400 texel; the pass rounds
//   SFB's colour to 8 bits as the game's 550x400 texture held it, then dithers it (the 4x4 Bayer
//   matrix the game's table holds, from _CrowDither) and quantizes it. 24 cycles, no dependent read.
// - pass 1, the blur at 624x351 straight from the 550x400 picture: the game's blur reads the
//   624x351 PSX picture (a point-sampled copy of the 550x400 one) half a texel off its centre, each
//   read the mean of two texels; five point reads of the 550x400 picture at the 624x351 texels'
//   centres give the same sums. 6-13 cycles, the 624x351 PSX texture gone.
// - pass 2, the CRT pass with its noise from a random texture (_CrowNoise) at an offset that changes
//   with _Time instead of the sin/cos hash: the same uniform per-pixel grain, new at every frame and
//   still while the game's time stands still, as the game's. 24-27 cycles. With the game's own hash
//   instead, the console's frames were 88-95% identical to the game's chain (docs/status-log.md, 19 Oct).
// _MainTex_TexelSize's sign flips the picture as the game's vertex programs do.
Shader "CrowVita/Post"
{
    Properties
    {
        _MainTex ("", 2D) = "white" {}
        _GrainTex ("", 2D) = "white" {}
        _CRTTex ("", 2D) = "white" {}
        _CrowDither ("", 2D) = "black" {}
        _CrowNoise ("", 2D) = "white" {}
        // What compat/CamEffect.cs checks before using the shader.
        [HideInInspector] _CrowPostV1 ("", Float) = 0
    }

    CGINCLUDE
        #include "UnityCG.cginc"

        sampler2D _MainTex;
        float4 _MainTex_TexelSize;

        struct appdata
        {
            float4 vertex : POSITION;
            float2 texcoord : TEXCOORD0;
        };

        // The game's SFB and blur vertex programs: v flipped when the source is upside down.
        float2 Flip(float2 uv)
        {
            return float2(uv.x, _MainTex_TexelSize.y >= 0.0 ? uv.y : 1.0 - uv.y);
        }
    ENDCG

    SubShader
    {
        ZClip On
        ZTest Always
        ZWrite Off
        Cull Off

        // 0: SFB and PSX, camera -> 550x400.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            sampler2D _GrainTex;
            sampler2D _CRTTex;
            sampler2D _CrowDither;
            float4 _CrowDitherScale;   // xy: the target's size / 4 (the dither's 4x4 cells)

            struct v2f
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 grain : TEXCOORD1;
                float2 crt : TEXCOORD2;
                float2 dither : TEXCOORD3;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.position = UnityObjectToClipPos(v.vertex);
                float2 uv = Flip(v.texcoord);
                o.uv = uv;
                // SFB: the grain picture's middle, mirrored in x 5 times a second and in y 2.5 times.
                float2 grain = uv * float2(0.6, 0.45) + float2(0.2, 0.3);
                float2 phase = frac(abs(_Time.yy * float2(10.0, 5.0)));
                phase = _Time.yy * float2(10.0, 5.0) >= 0.0 ? phase : -phase;
                float2 mirror = phase >= 0.5 ? 1.0 : 0.0;
                o.grain = mirror * (1.0 - 2.0 * grain) + grain;
                o.crt = uv * float2(56.476, 81.445);
                // PSX: the dither cell of the target texel (its own texture coordinates, not flipped).
                o.dither = v.texcoord * _CrowDitherScale.xy;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                float3 grain = float3(tex2D(_GrainTex, i.grain).rg, 0.0);
                float3 crt = tex2D(_CRTTex, i.crt).rgb;
                float bayer = tex2D(_CrowDither, i.dither).r;
                // SFB
                float3 s = (grain - c.rgb) * 0.114 + c.rgb;
                s = (s * crt - s) * 0.039 + s;
                s = s * float3(0.900933, 0.893554, 0.878639);
                // The game's 550x400 texture: 8 bits a channel.
                s = floor(saturate(s) * 255.0 + 0.5) * (1.0 / 255.0);
                // PSX: darker, red pulled to the middle, the dither's offset (0.00282353 - rank * 0.00117647,
                // the rank 0-15 as rank/15 in _CrowDither), 16 levels.
                float3 t = s * 1.075 - 0.075;
                t.r = t.r * 0.97 + 0.015;
                t = t + (0.00282353 - bayer * 0.01764705);
                return float4(saturate(floor(t * 16.0) * 0.0625), c.a);
            }
            ENDCG
        }

        // 1: the blur, 550x400 PSX picture -> 624x351.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 _CrowStep;   // xy: one texel of the 624x351 grid the game's PSX picture had

            struct v2f
            {
                float4 position : SV_POSITION;
                float2 c : TEXCOORD0;
                float2 l : TEXCOORD1;
                float2 r : TEXCOORD2;
                float2 u : TEXCOORD3;
                float2 d : TEXCOORD4;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.position = UnityObjectToClipPos(v.vertex);
                float2 uv = Flip(v.texcoord);
                o.c = uv;
                o.l = uv - float2(_CrowStep.x, 0.0);
                o.r = uv + float2(_CrowStep.x, 0.0);
                o.u = uv + float2(0.0, _CrowStep.y);
                o.d = uv - float2(0.0, _CrowStep.y);
                return o;
            }

            // The game's blur with _switch on (a = 0.113, b = 0.09, times 1.83486), its reads at half a
            // texel the means of the centre and a neighbour, per channel:
            //   r = a (D' + U' + L' + C) + (a + b) R'
            //   g = a (D' + U' + 2 L' + R') + b C
            //   b = a (D' + U' + C) + (2 a + b) L'      X' = (C + X) / 2
            #define A 0.113
            #define B 0.09
            #define G 1.83486
            static const half3 WC = half3(3.0 * A + 0.5 * B, 2.5 * A + B, 3.0 * A + 0.5 * B) * G;
            static const half3 WU = half3(0.5 * A, 0.5 * A, 0.5 * A) * G;
            static const half3 WL = half3(0.5 * A, A, A + 0.5 * B) * G;
            static const half3 WR = half3(0.5 * (A + B), 0.5 * A, 0.0) * G;

            half4 frag(v2f i) : SV_Target
            {
                half3 c = tex2D(_MainTex, i.c).rgb;
                half3 l = tex2D(_MainTex, i.l).rgb;
                half3 r = tex2D(_MainTex, i.r).rgb;
                half3 u = tex2D(_MainTex, i.u).rgb;
                half3 d = tex2D(_MainTex, i.d).rgb;
                return half4(c * WC + (u + d) * WU + l * WL + r * WR, 1.0);
            }
            ENDCG
        }

        // 2: the CRT pass, 624x351 -> 416x234, noise from _CrowNoise.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            sampler2D _CrowNoise;
            float4 _CrowNoiseScale;   // xy: the target's size / the noise texture's, zw: the noise texture's size
            float4x4 colorMat;

            struct v2f
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 noise : TEXCOORD1;
                float2 vignette : TEXCOORD2;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.position = UnityObjectToClipPos(v.vertex);
                float2 uv = (v.texcoord - 0.5) * 0.9967109 + 0.4991;
                o.uv = uv;
                // A new offset of whole noise texels whenever the game's time moves.
                float t = _Time.y;
                float2 offset = floor(frac(sin(float2(t * 12.9898, t * 78.233) + float2(0.5, 1.5)) * 43758.5453) * _CrowNoiseScale.zw) / _CrowNoiseScale.zw;
                o.noise = v.texcoord * _CrowNoiseScale.xy + offset;
                o.vignette = 0.5 - uv;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half3 c = tex2D(_MainTex, i.uv).rgb;
                half3 n = tex2D(_CrowNoise, i.noise).rgb;
                c = c * 0.96 + saturate(c * n) * 0.04;
                c = c * 0.96 + saturate(c * n.r) * 0.04;
                c = mul((half3x3)colorMat, c) + (half3)colorMat._m03_m13_m23;
                half vignette = saturate((1.0 - sqrt(dot(i.vignette, i.vignette)) * 1.414213) * 4.0);
                return half4(c * (vignette * 0.25 + 0.75), 1.0);
            }
            ENDCG
        }
    }
}
