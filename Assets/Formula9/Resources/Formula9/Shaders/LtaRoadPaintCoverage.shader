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


            fixed4 frag(Varyings i) : SV_Target
            {
                // Ground metres per pixel normal to each edge. Keep close-up paint to scale.
                float4 pixel = max(fwidth(i.edges), 0.0001);
                float4 expansion = clamp(0.25 * pixel - 0.035, 0.0, 0.35);
                float4 feather = min(pixel, 1.60);
                float4 covers = saturate(0.5 + (i.edges + expansion) / feather);
                float coverage = min(min(covers.x, covers.y), min(covers.z, covers.w));
                clip(coverage - 0.0039);
                return fixed4(_Color.rgb * 0.93, _Color.a * coverage * 0.98);
            }
            ENDCG
        }
    }
    Fallback Off
}
