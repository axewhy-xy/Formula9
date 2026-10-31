// A road's kerb or asphalt, tiled in metres. Unlit like the ground under it. It does not write depth,
// so a road's layers and the roads it overlaps paint over each other in queue order.
Shader "Formula9/CityRoad"
{
    Properties { _MainTex ("Grain", 2D) = "white" {} _Color ("Color", Color) = (1, 1, 1, 1) }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+1" }
        Pass
        {
            ZWrite Off
            Offset -1, -1
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            fixed4 _Color;
            static const float GrainTileM = 6;
            struct v2f { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata_base input) { v2f output; output.position = UnityObjectToClipPos(input.vertex); output.uv = input.vertex.xz / GrainTileM; return output; }
            fixed4 frag(v2f input) : SV_Target { return tex2D(_MainTex, input.uv) * _Color; }
            ENDCG
        }
    }
}
