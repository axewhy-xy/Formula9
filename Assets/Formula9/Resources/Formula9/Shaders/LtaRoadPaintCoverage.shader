Shader "Formula9/LtaRoadPaintCoverage"
{
    Properties
    {
        _Color ("Paint colour", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "Queue"="AlphaTest+10" "RenderType"="Transparent" }
        Cull Off
        ZTest LEqual
        ZWrite Off
        Offset -2, -4
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            struct Input { float4 position : POSITION; float4 edges : TEXCOORD0; };
            struct Varyings { float4 position : SV_POSITION; float4 edges : TEXCOORD0; float2 metres : TEXCOORD1; };

            Varyings vert(Input v)
            {
                Varyings o;
                o.position = UnityObjectToClipPos(v.position);
                o.edges = v.edges;
                o.metres = mul(unity_ObjectToWorld, v.position).xz;
                return o;
            }

            // World-space variation fades out as the grain becomes smaller than a pixel.
            float PaintWear(float2 metres)
            {
                float footprint = max(length(ddx(metres)), length(ddy(metres)));
                float2 patch = metres * 9.0;
                float grain = sin(dot(patch, float2(1.37, 2.11))) *
                    sin(dot(patch, float2(-2.43, 1.19)));
                return (1.0 - smoothstep(.025, .16, footprint)) *
                    smoothstep(.15, .85, grain);
            }

            fixed4 frag(Varyings i) : SV_Target
            {
                // Ground metres per pixel normal to each edge. Keep close-up paint to scale.
                float4 pixel = max(fwidth(i.edges), 0.0001);
                float4 expansion = clamp(0.25 * pixel - 0.035, 0.0, 0.35);
                float4 feather = min(pixel, 1.60);
                float4 covers = saturate(0.5 + (i.edges + expansion) / feather);
                float coverage = min(min(covers.x, covers.y), min(covers.z, covers.w));
                clip(coverage - 0.0039);
                float wear = PaintWear(i.metres);
                float3 tint = _Color.rgb * (0.93 - 0.12 * wear);
                return fixed4(tint, _Color.a * coverage * (0.98 - 0.18 * wear));
            }
            ENDCG
        }
    }
    Fallback Off
}
