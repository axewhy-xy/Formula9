Shader "Formula9/CityRoadAshWithCentreDividers"
{
    Properties
    {
        [HideInInspector] _SurveyPaintMask ("Survey paint mask", 2D) = "black" {}
        [HideInInspector] _SurveyMaskBounds ("Survey mask bounds", Vector) = (0,0,0,0)
        _Color ("Road tint", Color) = (0.53,0.54,0.56,1)
        _MainTex ("Road grain or paint", 2D) = "white" {}
        [HideInInspector] _StencilRef ("Stencil Ref", Float) = 1
        [HideInInspector] _StencilComp ("Stencil Comparison", Float) = 8
        [HideInInspector] _StencilPass ("Stencil Pass", Float) = 0
        [HideInInspector] _StencilReadMask ("Stencil Read Mask", Float) = 255
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
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }
        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            sampler2D _SurveyPaintMask;
            float4 _SurveyMaskBounds;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 lane : TEXCOORD1; float4 paintBounds : TEXCOORD2; float2 barDistances : TEXCOORD3; };
            struct v2f { float4 pos : SV_POSITION; float2 cityXZ : TEXCOORD4; float2 uv : TEXCOORD0; float4 lane : TEXCOORD1; float4 paintBounds : TEXCOORD2; float2 barDistances : TEXCOORD3; };
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.cityXZ = v.vertex.xz;
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

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 asphalt = tex2D(_MainTex, i.uv) * _Color;
                return asphalt;
            }
            ENDCG
        }
    }
    Fallback "Unlit/Texture"
}
