Shader "Formula9/CityRoadTexture"
{
    Properties
    {
        _Color ("Road tint", Color) = (1.34, 1.36, 1.39, 1)
        _MainTex ("Asphalt texture", 2D) = "gray" {}
        [HideInInspector] _UseRoadAtlas ("Use asphalt strip from atlas", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off
        ZTest LEqual
        ZWrite On
        Offset -1, -1
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _Color;
            float _UseRoadAtlas;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 lane : TEXCOORD1; // Across metres, distance, lane count, finished width.
                float4 paintBounds : TEXCOORD2; // Dash limits and stop-bar flags.
                float2 barDistances : TEXCOORD3;
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                float2 worldXZ : TEXCOORD0;
                float4 lane : TEXCOORD1;
                float4 paintBounds : TEXCOORD2; // Dash limits and stop-bar flags.
                float2 barDistances : TEXCOORD3;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.position = UnityObjectToClipPos(v.vertex);
                o.worldXZ = mul(unity_ObjectToWorld, v.vertex).xz;
                o.lane = v.lane;
                o.paintBounds = v.paintBounds;
                o.barDistances = v.barDistances;
                return o;
            }

            float Coverage(float distance, float halfWidth, float footprint)
            {
                float low = max(-halfWidth, distance - footprint);
                float high = min(halfWidth, distance + footprint);
                return saturate((high - low) / (2.0 * footprint));
            }

            float IncomingMask(float barType, float across)
            {
                if (barType < 0.5) return 0.0;
                if (barType < 1.5) return 1.0;
                return barType < 2.5 ? step(across, 0.5) : step(0.5, across);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 groundUV = i.worldXZ / 4.0;
                // Reflect a square of bare asphalt between the left stripe
                // and the centre dashes. Reflection keeps the tile edges continuous.
                float2 texturePhase = frac(groundUV * 0.5) * 2.0;
                float2 mirrored = 1.0 - abs(1.0 - texturePhase);
                float2 atlasUV = float2(0.16, 0.16) + mirrored * 0.24;
                // Bound the mip footprint to keep painted columns out of this crop.
                float2 dx = ddx(groundUV) * 0.24;
                float2 dy = ddy(groundUV) * 0.24;
                float gradientScale = min(1.0, 0.015 / max(0.00001, max(length(dx), length(dy))));
                fixed3 asphalt = _UseRoadAtlas > 0.5
                    ? tex2Dgrad(_MainTex, atlasUV, dx * gradientScale, dy * gradientScale).rgb
                    : tex2D(_MainTex, i.worldXZ / 8.0).rgb;
                float laneCount = round(i.lane.z);
                float widthPerLane = max(0.001, i.lane.w / max(1.0, laneCount));
                float lanePosition = i.lane.x / widthPerLane;
                float divider = floor(lanePosition + 0.5);
                float lineFootprint = clamp(fwidth(lanePosition) * widthPerLane * 0.5, 0.005, 0.5);
                float dividerCoverage = Coverage((lanePosition - divider) * widthPerLane, 0.08, lineFootprint);
                dividerCoverage *= step(1.0, divider) * step(divider, laneCount - 1.0);

                float along = i.lane.y;
                float dashPhase = frac(along / 6.0) * 6.0 - 3.0;
                float dashFootprint = clamp(fwidth(along) * 0.5, 0.005, 3.0);
                float dash = Coverage(dashPhase, 1.5, dashFootprint) +
                    Coverage(dashPhase - 6.0, 1.5, dashFootprint) +
                    Coverage(dashPhase + 6.0, 1.5, dashFootprint);
                float fromLow = along - i.paintBounds.x;
                float fromHigh = i.paintBounds.y - along;
                // Use the perpendicular bar near the stop station. Use the
                // strip coordinate farther away so a curved road cannot hit
                // the same infinite plane a second time.
                float barWindow = max(4.0, i.lane.w * 0.5 + 3.0);
                float useLow = step(0.5, i.paintBounds.z) * step(abs(fromLow), barWindow);
                float useHigh = step(0.5, i.paintBounds.w) * step(abs(fromHigh), barWindow);
                float lowCutoff = lerp(fromLow, i.barDistances.x, useLow);
                float highCutoff = lerp(fromHigh, i.barDistances.y, useHigh);
                float withinStrip = step(0.0, min(lowCutoff, highCutoff));

                float across = i.lane.x / max(0.001, i.lane.w);
                float lowIncoming = useLow * IncomingMask(i.paintBounds.z, across);
                float highIncoming = useHigh * IncomingMask(i.paintBounds.w, across);
                float stopDistance = min(lowIncoming > 0.5 ? lowCutoff : 1e6,
                    highIncoming > 0.5 ? highCutoff : 1e6);
                float finalDash = step(0.0, stopDistance) * step(stopDistance, 3.0);
                float useFinalDash = step(0.0, stopDistance) * step(stopDistance, 6.0);
                dash = lerp(dash, finalDash, useFinalDash);

                float2 footprint = clamp(fwidth(i.barDistances) * 0.5, 0.005, 0.5);
                float stopBar = max(lowIncoming * Coverage(i.barDistances.x, 0.15, footprint.x),
                    highIncoming * Coverage(i.barDistances.y, 0.15, footprint.y));
                float paint = step(1.0, laneCount) * max(stopBar, dividerCoverage * dash * withinStrip);
                float3 colour = saturate(asphalt * _Color.rgb);
                return fixed4(lerp(colour, float3(1.0, 1.0, 1.0), paint), 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
