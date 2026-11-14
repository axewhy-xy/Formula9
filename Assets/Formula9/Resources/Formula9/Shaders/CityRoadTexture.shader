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

            struct v2f
            {
                float4 position : SV_POSITION;
                float2 worldXZ : TEXCOORD0;
            };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                o.position = UnityObjectToClipPos(vertex);
                o.worldXZ = mul(unity_ObjectToWorld, vertex).xz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 groundUV = i.worldXZ / 4.0;
                // Reflect a square of bare asphalt between the left stripe
                // and the centre dashes. Reflection keeps the tile edges continuous.
                float2 phase = frac(groundUV * 0.5) * 2.0;
                float2 mirrored = 1.0 - abs(1.0 - phase);
                float2 atlasUV = float2(0.16, 0.16) + mirrored * 0.24;
                // Bound the mip footprint to keep painted columns out of this crop.
                float2 dx = ddx(groundUV) * 0.24;
                float2 dy = ddy(groundUV) * 0.24;
                float gradientScale = min(1.0, 0.015 / max(0.00001, max(length(dx), length(dy))));
                fixed3 asphalt = _UseRoadAtlas > 0.5
                    ? tex2Dgrad(_MainTex, atlasUV, dx * gradientScale, dy * gradientScale).rgb
                    : tex2D(_MainTex, i.worldXZ / 8.0).rgb;
                return fixed4(saturate(asphalt * _Color.rgb), 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
