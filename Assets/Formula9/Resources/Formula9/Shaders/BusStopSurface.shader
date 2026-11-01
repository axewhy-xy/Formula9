Shader "Formula9/BusStopSurface"
{
    // Built-in Render Pipeline PBR shader. URP automatically uses its own Lit
    // shader from BusStopShelterFactory with the same colour/normal/roughness data.
    Properties
    {
        [MainTexture] _MainTex ("Albedo", 2D) = "white" {}
        [MainColor] _Color ("Tint", Color) = (1,1,1,1)
        [Normal] _BumpMap ("Surface Normal", 2D) = "bump" {}
        _BumpScale ("Bump Strength", Range(0,2)) = 0.6
        _Metallic ("Metallic", Range(0,1)) = 0.2
        _Smoothness ("Smoothness", Range(0,1)) = 0.4
        _MicroGrain ("Micrograin", Range(0,0.5)) = 0.13
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 250
        Cull Back
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0
        #include "UnityCG.cginc"
        #include "UnityStandardUtils.cginc"

        sampler2D _MainTex;
        sampler2D _BumpMap;
        fixed4 _Color;
        half _BumpScale;
        half _Metallic;
        half _Smoothness;
        half _MicroGrain;

        struct Input
        {
            float2 uv_MainTex;
            float2 uv_BumpMap;
        };

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 col = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            half3 normalTS = UnpackScaleNormal(tex2D(_BumpMap, IN.uv_BumpMap), _BumpScale);
            // Very small variation in microscopic roughness, rather than
            // changing the colour of each panel. The normal texture supplies
            // the actual simulated grooves and scratches.
            half grain = tex2D(_MainTex, IN.uv_MainTex * 1.37).g;
            o.Albedo = col.rgb;
            o.Normal = normalTS;
            o.Metallic = _Metallic;
            o.Smoothness = saturate(_Smoothness + (grain - 0.5h) * _MicroGrain);
            o.Occlusion = 1;
            o.Alpha = col.a;
        }
        ENDCG
    }
    FallBack "Standard"
}
