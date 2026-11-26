// The city's sky: a gradient from the horizon up, with soft procedural clouds. Colours and cloud cover
// come from Resources/Formula9/world_look.json through WorldLighting.
Shader "Formula9/Sky"
{
    Properties
    {
        _Top ("Top", Color) = (0.25, 0.5, 0.78, 1)
        _Horizon ("Horizon", Color) = (0.84, 0.9, 0.95, 1)
        _Below ("Below the horizon", Color) = (0.75, 0.78, 0.79, 1)
        _CloudColor ("Clouds", Color) = (1, 1, 1, 1)
        _CloudCover ("Cloud cover", Range(0, 1)) = 0.45
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
            fixed4 _Top, _Horizon, _Below, _CloudColor;
            float _CloudCover;
            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }

            float Hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
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
                fixed3 color = up >= 0 ? lerp(_Horizon.rgb, _Top.rgb, pow(saturate(up), 0.55)) : lerp(_Horizon.rgb, _Below.rgb, saturate(-up * 6));
                if (up > 0.01)
                {
                    // Clouds on a flat layer overhead, thinning out towards the horizon
                    float2 layer = dir.xz / (up + 0.08) * 1.6;
                    float cloud = smoothstep(1 - _CloudCover, 1.15 - _CloudCover * 0.6, Fbm(layer));
                    cloud *= saturate(up * 4);
                    color = lerp(color, _CloudColor.rgb * (0.88 + 0.12 * up), cloud * 0.9);
                }
                return fixed4(color, 1);
            }
            ENDCG
        }
    }
    FallBack Off
}
