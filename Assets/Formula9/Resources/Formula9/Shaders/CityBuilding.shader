// Plain lit colour for the extruded city: _Color times the mesh's vertex colour.
Shader "Formula9/CityBuilding"
{
    Properties { _Color ("Color", Color) = (1, 1, 1, 1) }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        CGPROGRAM
        #pragma surface surf Lambert fullforwardshadows
        fixed4 _Color;
        struct Input { fixed4 color : COLOR; };
        // A little light of its own, so walls in shadow keep their colour instead of going black
        void surf(Input input, inout SurfaceOutput output)
        {
            output.Albedo = _Color.rgb * input.color.rgb;
            output.Emission = output.Albedo * 0.18;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
