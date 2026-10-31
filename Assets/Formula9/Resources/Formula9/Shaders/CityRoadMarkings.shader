// A road's white lines, worked out per pixel across the whole width of the road, so they fade evenly
// with distance instead of breaking up: solid at its edges and centre, dashed between lanes.
Shader "Formula9/CityRoadMarkings"
{
    Properties { _Color ("Color", Color) = (1, 1, 1, 1) }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Geometry+3" }
        Pass
        {
            ZWrite Off
            Offset -1, -1
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            fixed4 _Color;
            // LaneWidthM matches RoadMesh
            static const float LaneWidthM = 3.2, LineHalfWidthM = 0.12, DashPeriodM = 9, DashShare = 0.34;
            // texcoord: lane position across the road, metres along it, lanes, 1 if two-way
            struct v2f { float4 position : SV_POSITION; float4 line_ : TEXCOORD0; };
            v2f vert(appdata_base input) { v2f output; output.position = UnityObjectToClipPos(input.vertex); output.line_ = input.texcoord; return output; }
            fixed4 frag(v2f input) : SV_Target
            {
                float lane = input.line_.x, lanes = input.line_.z, twoWay = input.line_.w;
                float nearest = round(lane);
                float solid = nearest >= lanes - 0.5 || nearest <= (twoWay > 0.5 ? -lanes : 0) + 0.5 || (twoWay > 0.5 && abs(nearest) < 0.5) ? 1 : 0;
                // How much of this pixel the line covers, however small the line is on screen
                float across = abs(lane - nearest) * LaneWidthM;
                float pixel = max(fwidth(lane) * LaneWidthM, 1e-4);
                float coverage = saturate((across + LineHalfWidthM) / pixel + 0.5) - saturate((across - LineHalfWidthM) / pixel + 0.5);
                float dashes = input.line_.y / DashPeriodM;
                float dashPixel = max(fwidth(dashes), 1e-4);
                float dash = saturate((DashShare / 2 - abs(frac(dashes) - 0.5)) / dashPixel + 0.5);
                dash = lerp(dash, DashShare, saturate(dashPixel * 2 - 1));
                return fixed4(_Color.rgb, _Color.a * coverage * lerp(dash, 1, solid));
            }
            ENDCG
        }
    }
}
