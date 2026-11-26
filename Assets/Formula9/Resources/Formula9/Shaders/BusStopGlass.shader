Shader "Formula9/BusStopGlass"
{
    Properties
    {
        [MainTexture] _MainTex ("Subtle Streak Texture", 2D) = "white" {}
        _Tint ("Glass Tint", Color) = (0.78,0.86,0.88,0.35)
        _Opacity ("Front-facing Opacity", Range(0,1)) = 0.17
        _EdgeReflect ("Grazing Reflection", Range(0,1)) = 0.48
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Pass
        {
            // A restrained glass approximation without expensive grab/refraction.
            // Shared across Unity Built-in and URP as an unlit transparent pass.
            ZWrite Off
            ZTest LEqual
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            // DayCycle's night: 0 by day, nearer 1 as the unlit ground darkens
            float4 _Formula9GroundDim;
            float4 _MainTex_ST;
            float4 _Tint;
            float _Opacity;
            float _EdgeReflect;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normalWorld : TEXCOORD0;
                float3 posWorld : TEXCOORD1;
                float2 uv : TEXCOORD2;
            };
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normalWorld = normalize(UnityObjectToWorldNormal(v.normal));
                o.posWorld = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.normalWorld);
                float3 v = normalize(_WorldSpaceCameraPos.xyz - i.posWorld);
                float edge = pow(1 - saturate(abs(dot(n, v))), 4);
                float3 tex = tex2D(_MainTex, i.uv).rgb;
                float streak = dot(tex, float3(0.2126,0.7152,0.0722));
                // Light grey glass: mostly see-through head-on, brighter at angles.
                float3 sky = float3(0.71, 0.81, 0.84);
                float reflection = saturate(edge * _EdgeReflect);
                float3 rgb = lerp(_Tint.rgb * (0.94 + streak * 0.12), sky, reflection);
                float alpha = saturate(_Opacity + reflection * 0.40);
                return float4(rgb * (1 - _Formula9GroundDim.rgb), alpha);
            }
            ENDCG
        }
    }
    Fallback "Transparent/Diffuse"
}
