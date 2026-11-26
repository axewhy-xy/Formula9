// The map tiles laid on the ground: unlit, so the map keeps its colours, but darkened where the sun's
// shadow falls (by _Formula9ShadowDarkness, which WorldLighting sets from world_look.json).
// Shared, mipmapped grass detail covers mapped greenery or the bare ground while tiles are unavailable.
Shader "Formula9/CityGround"
{
    Properties
    {
        _MainTex ("Tile", 2D) = "gray" {}
        _GrassTex ("Grass detail", 2D) = "white" {}
        _GrassScale ("Grass repeats per metre", Float) = 0.5
        _GrassStrength ("Grass strength", Range(0, 1)) = 0
        [HideInInspector] _HasMapTile ("Map tile loaded", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            #include "AutoLight.cginc"
            sampler2D _MainTex, _GrassTex;
            float _GrassScale, _GrassStrength, _HasMapTile;
            float _Formula9ShadowDarkness;
            // DayCycle's night: 0 by day, nearer 1 as the unlit ground darkens
            float4 _Formula9GroundDim;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; SHADOW_COORDS(1) UNITY_FOG_COORDS(2) float2 grassUV : TEXCOORD3; };
            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy;
                // World coordinates keep the repeat continuous across adjacent map tiles.
                o.grassUV = mul(unity_ObjectToWorld, v.vertex).xz * _GrassScale;
                TRANSFER_SHADOW(o);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 color = tex2D(_MainTex, i.uv);
                // Green dominance excludes map roads, roofs and water. Missing map tiles use grass
                // beneath the existing road, sidewalk and building geometry instead of plain grey.
                UNITY_BRANCH if (_GrassStrength > 0)
                {
                    half greenery = smoothstep(0.015, 0.08, color.g - max(color.r, color.b));
                    half coverage = lerp(1, greenery, _HasMapTile) * _GrassStrength;
                    color.rgb = lerp(color.rgb, tex2D(_GrassTex, i.grassUV).rgb, coverage);
                }
                color.rgb *= 1 - _Formula9ShadowDarkness * (1 - SHADOW_ATTENUATION(i));
                color.rgb *= 1 - _Formula9GroundDim.rgb;
                UNITY_APPLY_FOG(i.fogCoord, color);
                return color;
            }
            ENDCG
        }
        // So the ground is in the depth the sun's shadows are worked out against
        UsePass "Legacy Shaders/VertexLit/SHADOWCASTER"
    }
}
