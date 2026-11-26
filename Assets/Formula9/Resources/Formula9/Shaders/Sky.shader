// The city's sky: a gradient from the horizon up, the sun's glow and disc, the moon and stars by night,
// and soft procedural clouds that drift with the wind. DayCycle sets everything here each frame from
// the hour keys and weather in Resources/Formula9/world_look.json.
Shader "Formula9/Sky"
{
    Properties
    {
        _Top ("Top", Color) = (0.25, 0.5, 0.78, 1)
        _Horizon ("Horizon", Color) = (0.84, 0.9, 0.95, 1)
        _Below ("Below the horizon", Color) = (0.75, 0.78, 0.79, 1)
        _CloudColor ("Clouds", Color) = (1, 1, 1, 1)
        _CloudCover ("Cloud cover", Range(0, 1)) = 0.45
        _CloudShade ("Cloud undersides", Range(0, 1)) = 0.3
        _CloudOffset ("Cloud drift (xy coarse, zw fine)", Vector) = (0, 0, 0, 0)
        _SunDir ("Towards the sun", Vector) = (0, 1, 0, 0)
        _SunColor ("Sun glow (a: how much shows)", Color) = (1, 1, 1, 0)
        _MoonDir ("Towards the moon (w: how much shows)", Vector) = (0, -1, 0, 0)
        _Stars ("Stars", Range(0, 1)) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            fixed4 _Top, _Horizon, _Below, _CloudColor, _SunColor;
            float4 _CloudOffset, _SunDir, _MoonDir;
            float _CloudCover, _CloudShade, _Stars;
            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }

            float Hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
            float Hash3(float3 p) { return frac(sin(dot(p, float3(127.1, 311.7, 74.7))) * 43758.5453); }
            float Noise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                f = f * f * (3 - 2 * f);
                return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), f.x), lerp(Hash(i + float2(0, 1)), Hash(i + 1), f.x), f.y);
            }
            float Fbm(float2 p)
            {
                float sum = 0, amplitude = 0.5;
                for (int k = 0; k < 5; k++) { sum += amplitude * Noise(p); p *= 2.03; amplitude *= 0.5; }
                return sum;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 dir = normalize(i.dir);
                float up = dir.y;
                float3 color = up >= 0 ? lerp(_Horizon.rgb, _Top.rgb, pow(saturate(up), 0.55)) : lerp(_Horizon.rgb, _Below.rgb, saturate(-up * 6));
                float3 sunDir = normalize(_SunDir.xyz);
                float toward = saturate(dot(dir, sunDir));

                // Stars, faded out near the horizon, twinkling a little
                if (_Stars > 0.001 && up > 0)
                {
                    float3 p = dir * 260;
                    float3 cell = floor(p);
                    float h = Hash3(cell);
                    float star = step(0.997, h) * saturate(1 - length(frac(p) - 0.5) * 2.2);
                    float twinkle = 0.65 + 0.35 * sin(_Time.y * (1.5 + h * 3) + h * 80);
                    color += star * twinkle * _Stars * saturate(up * 4);
                }

                // The sun's glow, widest low in the sky, where sunrise and sunset colour the horizon around it
                float low = 1 - saturate(up * 2.5);
                float glow = pow(toward, 8) * (0.12 + 0.6 * low) + pow(toward, 300) * 0.5;
                color += _SunColor.rgb * glow * _SunColor.a;
                float sunDisc = smoothstep(0.99935, 0.9997, dot(dir, sunDir)) * step(-0.02, up);
                color = lerp(color, _SunColor.rgb * 0.4 + 0.75, sunDisc * _SunColor.a);

                // The moon: a pale disc with a faint halo
                float3 moonDir = normalize(_MoonDir.xyz);
                float towardMoon = saturate(dot(dir, moonDir));
                float moon = smoothstep(0.99955, 0.9998, towardMoon) * step(0, up);
                color += float3(0.55, 0.62, 0.8) * pow(towardMoon, 400) * 0.25 * _MoonDir.w;
                color = lerp(color, float3(0.86, 0.89, 0.95), moon * _MoonDir.w);

                if (up > 0.01)
                {
                    // Clouds on a flat layer overhead, thinning out towards the horizon. Two layers drift at
                    // different speeds, so the clouds change shape as the wind carries them.
                    float2 layer = dir.xz / (up + 0.08) * 1.6;
                    float shape = Fbm(layer + _CloudOffset.xy) * 0.72 + Fbm(layer * 1.9 + _CloudOffset.zw) * 0.28;
                    float cloud = smoothstep(1 - _CloudCover, 1.15 - _CloudCover * 0.6, shape);
                    cloud *= saturate(up * 4);
                    // Thick parts are darker underneath; the edges near the sun catch its light
                    float thick = saturate((shape - (1 - _CloudCover)) * 3);
                    float3 cloudColor = _CloudColor.rgb * lerp(1, 0.55, thick * _CloudShade) * (0.88 + 0.12 * up);
                    cloudColor += _SunColor.rgb * pow(toward, 5) * (1 - thick) * 0.45 * _SunColor.a;
                    color = lerp(color, cloudColor, cloud * 0.9);
                }
                return fixed4(color, 1);
            }
            ENDCG
        }
    }
    FallBack Off
}
