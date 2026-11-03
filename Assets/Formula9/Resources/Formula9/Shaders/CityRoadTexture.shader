Shader "Formula9/CityRoadTexture"
{
    Properties
    {
        _Color ("Road tint", Color) = (1.34, 1.36, 1.39, 1)
        _MainTex ("Road lane atlas", 2D) = "gray" {}
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
        ZTest LEqual
        ZWrite [_ZWrite]
        // Helps asphalt win against nearby map-tile/ground depth.
        // Cannot fix overlapping coplanar asphalt polygons inside one mesh.
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
                float4 laneUV : TEXCOORD1; // (across 0..1, metres/24, signed lane count, enabled)
                float4 fadeUV : TEXCOORD2; // (start cutoff m, end cutoff m, fade width m, has end cutoff)
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 laneUV : TEXCOORD0;
                float4 fadeUV : TEXCOORD1;
                float2 worldXZ : TEXCOORD2;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.laneUV = v.laneUV;
                o.fadeUV = v.fadeUV;
                float3 world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldXZ = world.xz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float across = saturate(i.laneUV.x);
                float along = i.laneUV.y;
                float lanes = floor(abs(i.laneUV.z) + 0.5);
                float twoWay = i.laneUV.z < -0.5 ? 1.0 : 0.0;

                // 1. Every road, bend cap and junction cap receives *identical*
                // plain-asphalt UVs at identical world positions. No road-local
                // U restart and no atlas-column switch for unmarked asphalt.
                // Stay inside the user's plain asphalt atlas strip near U=.28.
                float bareU = 0.28 + 0.055 * sin(i.worldXZ.x * 0.12);
                float bareV = (i.worldXZ.y + i.worldXZ.x * 0.31) / 24.0;
                float2 bareUV = float2(bareU, bareV);
                float2 bareDx = ddx(bareUV);
                float2 bareDy = ddy(bareUV);
                fixed4 bare = tex2Dgrad(_MainTex, bareUV, bareDx, bareDy);

                // 2. UV2 stores metres, NOT a per-vertex marking opacity.
                // Fade each pixel rather than losing all paint on short roads.
                float fade = saturate(i.laneUV.w);
                float distanceM = along * 24.0;
                float fadeSpan = max(0.05, i.fadeUV.z);
                if (i.fadeUV.x > 0.0)
                    fade *= smoothstep(i.fadeUV.x, i.fadeUV.x + fadeSpan, distanceM);
                if (i.fadeUV.w > 0.5)
                    fade *= 1.0 - smoothstep(i.fadeUV.y - fadeSpan, i.fadeUV.y, distanceM);

                // Old RoadMesh Cap() vertices use zero UV1/UV2: keep them
                // plain and world-aligned instead of drawing spoked markings.
                float3 roadRGB = bare.rgb;

                if (lanes >= 1.0 && fade > 0.0)
                {
                    // 3. Determine the nearest lane boundary in continuous
                    // lane-position space. The atlas selection itself jumps,
                    // so we NEVER use implicit ddx/ddy of the chosen paint UV.
                    float lanePosition = across * lanes;
                    float nearest = floor(lanePosition + 0.5);
                    float laneOffset = lanePosition - nearest;
                    float edgeLeft = 1.0 - step(0.5, nearest);
                    float edgeRight = step(lanes - 0.5, nearest);
                    float paintU = (edgeLeft > 0.5) ? (0.076 + laneOffset * 0.5)
                                 : (edgeRight > 0.5) ? (0.923 + laneOffset * 0.5)
                                 : (0.500 + laneOffset * 0.5);

                    float2 paintedUV = float2(paintU, along);
                    // Gradients are calculated from the continuous UV field,
                    // NOT from paintU (which changes atlas columns at seams).
                    float2 paintDx = float2(ddx(lanePosition) * 0.5, ddx(along));
                    float2 paintDy = float2(ddy(lanePosition) * 0.5, ddy(along));
                    fixed4 painted = tex2Dgrad(_MainTex, paintedUV, paintDx, paintDy);

                    float feather = max(fwidth(lanePosition), 0.005);
                    float band = 1.0 - smoothstep(0.15 - feather, 0.15 + feather, abs(laneOffset));
                    roadRGB = lerp(roadRGB, painted.rgb, saturate(fade * band));

                    if (twoWay > 0.5 && lanes >= 2.0)
                    {
                        // Replace centre white paint with two yellow lines.
                        float centreOffset = abs(lanePosition - lanes * 0.5);
                        float centreMask = (1.0 - smoothstep(0.17, 0.28, centreOffset)) * fade;
                        roadRGB = lerp(roadRGB, bare.rgb, centreMask);
                        float pixelWidth = max(fwidth(lanePosition), 0.005);
                        float stripe = 1.0 - smoothstep(0.031 - pixelWidth,
                            0.031 + pixelWidth, abs(centreOffset - 0.061));
                        roadRGB = lerp(roadRGB, float3(0.98, 0.78, 0.08) / max(_Color.rgb, 0.01),
                            saturate(stripe * fade));
                    }
                }

                return fixed4(saturate(roadRGB * _Color.rgb), 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
