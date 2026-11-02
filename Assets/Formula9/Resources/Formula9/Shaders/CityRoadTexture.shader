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
        // Bias road pixels towards the camera to reduce ground z-fighting.
        Offset -1, -1
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
                float4 laneUV : TEXCOORD1; // across, distance/24, signed lanes, marking-enabled flag
                float4 fadeUV : TEXCOORD2; // start cutoff, end cutoff, fade span, end-junction flag
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 laneUV : TEXCOORD0;
                float4 fadeUV : TEXCOORD1;
            };
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.laneUV = v.laneUV;
                o.fadeUV = v.fadeUV;
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
                // RoadMesh now uses laneUV.w as the marked/unmarked flag,
                // not as a per-vertex fade. The transition is evaluated for
                // every pixel using road-distance and junction cutoffs.
                float fade = saturate(i.laneUV.w);
                float distanceM = along * 24.0;
                float transitionM = max(0.05, i.fadeUV.z);
                if (i.fadeUV.x > 0.0)
                    fade *= smoothstep(i.fadeUV.x, i.fadeUV.x + transitionM, distanceM);
                if (i.fadeUV.w > 0.5)
                    fade *= 1.0 - smoothstep(i.fadeUV.y - transitionM, i.fadeUV.y, distanceM);
                // The user image contains genuine white painted edges at 8% and
                // 92%, and a dashed centre at 50%. It supplies BOTH the paint
                // pattern and grain. The lane model only picks which source
                // column to sample; it draws no geometric divider quads.
                
// Continuous asphalt UVs that avoid sampling painted regions.
                float bareU = 0.20 + across * 0.16;

                float lanePosition = across * max(1.0, lanes);
                float nearest = floor(lanePosition + 0.5);
                float offset = lanePosition - nearest;

// Calculate painted UVs separately from asphalt UVs.
                float paintU = 0.5 + offset * 0.5;
                float validMark = 0.0;

                if (lanes >= 1.0)
                {
                    if (nearest <= 0.0)
                    {
                        paintU = 0.076 + offset * 0.5;
                        validMark = 1.0;
                    }
                    else if (nearest >= lanes)
                    {
                        paintU = 0.923 + offset * 0.5;
                        validMark = 1.0;
                    }
                    else
                    {
                        validMark = 1.0;
                    }
                }

// Calculate stable gradients for each texture sample.
                float2 bareDx = float2(ddx(across) * 0.16, ddx(along));
                float2 bareDy = float2(ddy(across) * 0.16, ddy(along));

                float2 paintDx = float2(ddx(lanePosition) * 0.5, ddx(along));
                float2 paintDy = float2(ddy(lanePosition) * 0.5, ddy(along));

// Sample asphalt and paint independently.
                fixed4 bare = tex2Dgrad(_MainTex, float2(bareU, along), bareDx, bareDy);

                fixed4 painted = tex2Dgrad(_MainTex, float2(paintU, along), paintDx, paintDy);

// Smooth the marking boundaries to avoid hard seams.
                float feather = max(fwidth(lanePosition), 0.005);
                float markingBand = 1.0 - smoothstep(0.15 - feather,0.15 + feather,abs(offset));

                float markingMask = fade * validMark * markingBand;

// Blend the samples without discontinuous UV selection.
                fixed4 result = lerp(bare, painted, markingMask);

                result.rgb = saturate(result.rgb * _Color.rgb);
                // For bidirectional roads, show two clearly separated yellow
                // centre stripes instead of a confusing dashed white centre.
                // This is an illustrative visual divider, not an LTA survey.
                // At the junction fade-out, return to bare asphalt.
                if (twoWay > 0.5 && lanes >= 2.0 && fade > 0.001)
                {
                    float centreOffset = abs(lanePosition - lanes * 0.5);
                    float twinBand = 1.0 - step(0.031, abs(centreOffset - 0.061));
                    float3 plain = tex2Dgrad(_MainTex, float2(0.30, along),
                        float2(0, paintDx.y), float2(0, paintDy.y)).rgb * _Color.rgb;
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
