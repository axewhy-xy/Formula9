Shader "Formula9/CityRoadTexture"
{
    Properties
    {
        _Color ("Road tint", Color) = (1.34,1.36,1.39,1)
        _MainTex ("Road texture (unaltered user image)", 2D) = "gray" {}
        [HideInInspector] _StencilRef ("Stencil Ref", Float) = 1
        [HideInInspector] _StencilComp ("Stencil Comparison", Float) = 8
        [HideInInspector] _StencilPass ("Stencil Pass", Float) = 0
        [HideInInspector] _StencilWriteMask ("Stencil Write Mask", Float) = 0
        [HideInInspector] _ZWrite ("Depth Write", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off
        ZWrite [_ZWrite]
        ZTest LEqual
        Stencil
        {
            Ref [_StencilRef]
            Comp [_StencilComp]
            Pass [_StencilPass]
            WriteMask [_StencilWriteMask]
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _Color;
            struct appdata
            {
                float4 vertex : POSITION;
                float4 laneUV : TEXCOORD1;
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 laneUV : TEXCOORD0;
            };
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.laneUV = v.laneUV;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float across = saturate(i.laneUV.x);
                float along = i.laneUV.y;
                // Negative signed lane counts identify bidirectional roads.
                // The backend's Lanes field is per direction on those roads.
                float twoWay = i.laneUV.z < -0.5 ? 1.0 : 0.0;
                float lanes = floor(abs(i.laneUV.z) + 0.5);
                float fade = saturate(i.laneUV.w);
                // The user image contains genuine white painted edges at 8% and
                // 92%, and a dashed centre at 50%. It supplies BOTH the paint
                // pattern and grain. The lane model only picks which source
                // column to sample; it draws no geometric divider quads.
                float sourceU = (across < 0.5)
                    ? (0.17 + across * 0.52)
                    : (0.57 + (across - 0.5) * 0.52); // grain away from paint
                float lanePosition = across * max(1.0, lanes);
                float nearest = floor(lanePosition + 0.5);
                float offset = lanePosition - nearest;
                if (lanes >= 1.0 && fade > 0.05)
                {
                    if (nearest <= 0.0 && abs(offset) < 0.15)
                        sourceU = 0.076 + offset * 0.5;
                    else if (nearest >= lanes && abs(offset) < 0.15)
                        sourceU = 0.923 + offset * 0.5;
                    else if (nearest > 0.0 && nearest < lanes && abs(offset) < 0.15)
                        sourceU = 0.5 + offset * 0.5;
                }
                fixed4 result = tex2D(_MainTex, float2(sourceU, along));
                // Blend markings out at junction mouths without hiding asphalt.
                if (fade < 0.999)
                {
                    fixed4 bare = tex2D(_MainTex, float2((across < 0.5) ? (0.17 + across * 0.52) : (0.57 + (across - 0.5) * 0.52), along));
                    result.rgb = lerp(bare.rgb, result.rgb, fade);
                }
                result.rgb = saturate(result.rgb * _Color.rgb);
                // For bidirectional roads, show two clearly separated yellow
                // centre stripes instead of a confusing dashed white centre.
                // This is an illustrative visual divider, not an LTA survey.
                // At the junction fade-out, return to bare asphalt.
                if (twoWay > 0.5 && lanes >= 2.0 && fade > 0.001)
                {
                    float centreOffset = abs(lanePosition - lanes * 0.5);
                    float twinBand = 1.0 - step(0.031, abs(centreOffset - 0.061));
                    float3 plain = tex2D(_MainTex, float2(0.30, along)).rgb * _Color.rgb;
                    result.rgb = lerp(result.rgb, saturate(plain),
                        (1.0 - smoothstep(0.17, 0.28, centreOffset)) * fade);
                    result.rgb = lerp(result.rgb, float3(0.98, 0.78, 0.08),
                        twinBand * fade);
                }
                result.a = 1;
                return result;
            }
            ENDCG
        }
    }
    Fallback Off
}
