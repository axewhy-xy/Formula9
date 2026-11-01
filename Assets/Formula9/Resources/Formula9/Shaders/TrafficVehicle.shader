// The background traffic. Imported vehicles' textures, tinted by _Paint, which each instance sets for
// itself so one material can draw every colour of a vehicle. Models built from boxes colour their
// parts by vertex, which imported models do not use: _VertexColor turns that on.
Shader "Formula9/TrafficVehicle"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _MainTex ("Albedo", 2D) = "white" {}
        _Glossiness ("Smoothness", Range(0, 1)) = 0.3
        _Metallic ("Metallic", Range(0, 1)) = 0
        _Paint ("Paint", Color) = (1, 1, 1, 1)
        _VertexColor ("Use vertex colour", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows addshadow
        #pragma target 3.0
        sampler2D _MainTex;
        fixed4 _Color;
        half _Glossiness, _Metallic;
        float _VertexColor;
        struct Input { float2 uv_MainTex; fixed4 color : COLOR; };

        UNITY_INSTANCING_BUFFER_START(Vehicle)
            UNITY_DEFINE_INSTANCED_PROP(fixed4, _Paint)
        UNITY_INSTANCING_BUFFER_END(Vehicle)

        void surf(Input input, inout SurfaceOutputStandard output)
        {
            fixed3 tint = _Color.rgb * UNITY_ACCESS_INSTANCED_PROP(Vehicle, _Paint).rgb * lerp(fixed3(1, 1, 1), input.color.rgb, _VertexColor);
            output.Albedo = tex2D(_MainTex, input.uv_MainTex).rgb * tint;
            output.Metallic = _Metallic;
            output.Smoothness = _Glossiness;
        }
        ENDCG
    }
}
