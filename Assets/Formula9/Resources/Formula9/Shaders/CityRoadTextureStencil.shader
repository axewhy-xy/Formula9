Shader "Formula9/CityRoadTextureStencil"
{
    Properties
    {
        [HideInInspector] _MappedPaintReady ("Mapped mesh paint ready", Float) = 0
        _Color ("Road tint", Color) = (1.34, 1.36, 1.39, 1)
        [HideInInspector] _UseMappedDividers ("Use LTA dividers", Float) = 0
        [HideInInspector] _VaryAsphaltTiling ("Vary asphalt tiling", Float) = 0
        _MainTex ("Asphalt texture", 2D) = "gray" {}
        [Normal] _BumpMap ("Asphalt normal map", 2D) = "bump" {}
        _HeightMap ("Asphalt height map", 2D) = "gray" {}
        _NormalStrength ("Asphalt normal strength", Range(0, 2)) = 0
        _HeightScale ("Asphalt parallax scale", Range(0, 0.01)) = 0
        _AsphaltTileMetres ("Asphalt tile size (metres)", Float) = 2.5
        _AsphaltGrainContrast ("Asphalt grain contrast", Range(0, 1)) = 0.65
        _AsphaltWearStrength ("Asphalt wear variation", Range(0, 0.3)) = 0.17
        [HideInInspector] _AsphaltLightDirection ("Asphalt light direction", Vector) = (-0.44, 0.822, 0.36, 0)
        [HideInInspector] _UseAsphaltMaps ("Use standalone asphalt maps", Float) = 0
        [HideInInspector] _UseRoadAtlas ("Use asphalt strip from atlas", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off
        ZTest LEqual
        ZWrite On
        Offset -1, -1
        Stencil { Ref 1 WriteMask 1 Comp Always Pass Replace }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _BumpMap;
            sampler2D _HeightMap;
            float _NormalStrength;
            float _HeightScale;
            float _AsphaltTileMetres;
            float _AsphaltGrainContrast;
            float _AsphaltWearStrength;
            float4 _AsphaltLightDirection;
            float _UseAsphaltMaps;
            float _VaryAsphaltTiling;
            float4 _Color;
            float _UseRoadAtlas;
            float _UseMappedDividers;
            float _MappedPaintReady;

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
                float3 worldPosition : TEXCOORD0;
                float4 lane : TEXCOORD1;
                float4 paintBounds : TEXCOORD2; // Dash limits and stop-bar flags.
                float2 barDistances : TEXCOORD3;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.position = UnityObjectToClipPos(v.vertex);
                o.worldPosition = mul(unity_ObjectToWorld, v.vertex).xyz;
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
                if (barType < 0.5 || barType > 3.5) return 0.0;
                if (barType < 1.5) return 1.0;
                return barType < 2.5 ? step(across, 0.5) : step(0.5, across);
            }

            float2 AsphaltOffset(float2 corner)
            {
                float2 seed = float2(dot(corner, float2(127.1, 311.7)),
                    dot(corner, float2(269.5, 183.3)));
                return frac(sin(seed) * 43758.5453);
            }

            fixed3 VariedAsphalt(float2 roadMetres, float2 worldMetres, float2 roadDx, float2 roadDy)
            {
                // Each triangle blends three offsets of the same asphalt.
                // Neighbouring triangles share samples along their edges.
                float2 grid = worldMetres / 4.5;
                float2 corner = floor(grid);
                float2 withinCell = frac(grid);
                float2 horizontal = float2(1.0, 0.0);
                float2 vertical = float2(0.0, 1.0);
                float diagonal = withinCell.x + withinCell.y;
                float upper = step(1.0, diagonal);
                float3 weights = float3(abs(1.0 - diagonal),
                    lerp(withinCell.x, 1.0 - withinCell.y, upper),
                    lerp(withinCell.y, 1.0 - withinCell.x, upper));
                weights *= weights;
                weights /= weights.x + weights.y + weights.z;

                float2 uv = roadMetres / 3.0;
                float2 dx = roadDx / 3.0, dy = roadDy / 3.0;
                fixed3 a = tex2Dgrad(_MainTex, uv + AsphaltOffset(corner + float2(upper, upper)), dx, dy).rgb;
                fixed3 b = tex2Dgrad(_MainTex, uv + AsphaltOffset(corner + horizontal), dx, dy).rgb;
                fixed3 c = tex2Dgrad(_MainTex, uv + AsphaltOffset(corner + vertical), dx, dy).rgb;
                return a * weights.x + b * weights.y + c * weights.z;
            }

            float AsphaltWearNoise(float2 uv)
            {
                float2 cell = floor(uv);
                float2 blend = frac(uv);
                blend = blend * blend * (3.0 - 2.0 * blend);
                float bottom = lerp(AsphaltOffset(cell).x,
                    AsphaltOffset(cell + float2(1.0, 0.0)).x, blend.x);
                float top = lerp(AsphaltOffset(cell + float2(0.0, 1.0)).x,
                    AsphaltOffset(cell + float2(1.0, 1.0)).x, blend.x);
                // Preserve a neutral average when the wear field gets too small on screen.
                float footprint = max(length(ddx(uv)), length(ddy(uv)));
                return (lerp(bottom, top, blend.y) - 0.5) *
                    (1.0 - smoothstep(0.20, 0.75, footprint));
            }

            float3 SampleAsphaltMaps(float3 worldPosition)
            {
                // Match all three maps in world XZ across road strips and junction patches.
                float2 uv = worldPosition.xz / max(0.01, _AsphaltTileMetres);
                float2 dx = ddx(uv);
                float2 dy = ddy(uv);

                if (_HeightScale > 0.0)
                {
                    float3 perspectiveView = normalize(_WorldSpaceCameraPos - worldPosition);
                    float3 orthoView = UNITY_MATRIX_V[2].xyz;
                    float3 view = normalize(lerp(perspectiveView, orthoView, unity_OrthoParams.w));
                    float height = tex2Dgrad(_HeightMap, uv, dx, dy).r - 0.5;
                    // Fade the offset at grazing angles and when the texture minifies.
                    float fade = smoothstep(0.0, 0.35, view.y) *
                        (1.0 - smoothstep(0.02, 0.1, max(length(dx), length(dy))));
                    uv += view.xz * (height * _HeightScale * fade / max(view.y, 0.35));
                }

                float3 asphalt = tex2Dgrad(_MainTex, uv, dx, dy).rgb;
                if (_AsphaltGrainContrast > 0.0)
                {
                    // Compare two mip-filtered samples to bring out the local grain.
                    float3 average = tex2Dgrad(_MainTex, uv, dx * 4.0, dy * 4.0).rgb;
                    asphalt = saturate(asphalt + (asphalt - average) * _AsphaltGrainContrast);
                }
                if (_NormalStrength > 0.0)
                {
                    float3 normal = UnpackNormal(tex2Dgrad(_BumpMap, uv, dx, dy));
                    // Mirror addressing reverses the matching tangent axis every other tile.
                    float2 tileParity = frac(floor(uv) * 0.5) * 2.0;
                    float2 mirrorSign = 1.0 - 2.0 * tileParity;
                    float2 edge = min(frac(uv), 1.0 - frac(uv));
                    float2 footprint = max(abs(dx) + abs(dy), 0.0001);
                    float2 edgeFade = saturate(edge / footprint);
                    float2 slope = normal.xy * mirrorSign * edgeFade * _NormalStrength;
                    normal = float3(slope, sqrt(saturate(1.0 - dot(slope, slope))));
                    // The planar map uses world X and Z as its tangent axes.
                    float3 reliefLight = normalize(_AsphaltLightDirection.xzy);
                    float relief = saturate(dot(normal, reliefLight)) - reliefLight.z;
                    asphalt *= 1.0 + 0.45 * relief;
                }
                if (_AsphaltWearStrength > 0.0)
                {
                    // Use world coordinates so wear does not repeat at JPG tile boundaries.
                    float2 worldXZ = worldPosition.xz;
                    float broadWear = AsphaltWearNoise(worldXZ / 2.6);
                    float smallWear = AsphaltWearNoise(worldXZ / 0.85 + float2(7.3, 11.7));
                    asphalt *= 1.0 + _AsphaltWearStrength * (0.65 * broadWear + 0.35 * smallWear);
                }
                return asphalt;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 groundUV = i.worldPosition.xz / 4.0;
                // Reflect a square of bare asphalt between the left stripe
                // and the centre dashes. Reflection keeps the tile edges continuous.
                float2 texturePhase = frac(groundUV * 0.5) * 2.0;
                float2 mirrored = 1.0 - abs(1.0 - texturePhase);
                float2 atlasUV = float2(0.16, 0.16) + mirrored * 0.24;
                // Bound the mip footprint to keep painted columns out of this crop.
                float2 dx = ddx(groundUV) * 0.24;
                float2 dy = ddy(groundUV) * 0.24;
                float gradientScale = min(1.0, 0.015 / max(0.00001, max(length(dx), length(dy))));
                // U runs across the paved strip and V follows its length.
                // Junction patches have no strip coordinates and use ground UVs.
                float2 asphaltMetres = i.lane.w > 0.001
                    ? float2(i.lane.x - i.lane.w * 0.5, i.lane.y)
                    : i.worldPosition.xz;
                float2 roadDx = ddx(asphaltMetres);
                float2 roadDy = ddy(asphaltMetres);
                float3 asphalt;
                if (_UseAsphaltMaps > 0.5)
                    asphalt = SampleAsphaltMaps(i.worldPosition);
                else if (_UseRoadAtlas > 0.5)
                    asphalt = tex2Dgrad(_MainTex, atlasUV, dx * gradientScale, dy * gradientScale).rgb;
                else if (_VaryAsphaltTiling > 0.5)
                    asphalt = VariedAsphalt(asphaltMetres, i.worldPosition.xz, roadDx, roadDy);
                else
                    asphalt = tex2D(_MainTex, asphaltMetres / 8.0).rgb;
                float laneCount = round(abs(i.lane.z));
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
                // Negative lane count identifies a shared two-way carriageway.
                float centreDistance = i.lane.x - i.lane.w * 0.5;
                float centreFootprint = clamp(fwidth(centreDistance) * 0.5, 0.005, 0.5);
                // Ordinary segment ends only shorten the dashes.
                // The solid separator runs to the end of the paved strip.
                float centreLimit = step(i.paintBounds.x, i.paintBounds.y) *
                    lerp(1.0, step(0.0, lowCutoff), step(0.5, i.paintBounds.z)) *
                    lerp(1.0, step(0.0, highCutoff), step(0.5, i.paintBounds.w));
                float mappedDividers = step(0.5, _UseMappedDividers);
                float twoWay = step(0.5, -i.lane.z);
                dividerCoverage *= 1.0 - mappedDividers * twoWay *
                    (1.0 - step(0.5, abs(divider - laneCount * 0.5)));
                float centreLine = (1.0 - mappedDividers) * twoWay * centreLimit *
                    Coverage(centreDistance, 0.15, centreFootprint);
                float paint = step(1.0, laneCount) *
                    max(stopBar, max(dividerCoverage * dash * withinStrip, centreLine));
                paint *= 1.0 - _MappedPaintReady;
                float3 colour = saturate(asphalt * _Color.rgb);
                return fixed4(lerp(colour, float3(1.0, 1.0, 1.0), paint), 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
