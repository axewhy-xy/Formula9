Shader "Formula9/LtaReadableMarkings"
{
    SubShader
    {
        Tags { "Queue"="Transparent-50" "RenderType"="Transparent" }
        Pass
        {
            Cull Off ZWrite Off ZTest LEqual Offset -2,-4
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            float4 _Formula9GroundDim;
            struct Input { float4 vertex:POSITION; float4 shape:TEXCOORD0; float4 placement:TEXCOORD1; };
            struct Output { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; float2 halfSize:TEXCOORD1; float kind:TEXCOORD2; float2 metres:TEXCOORD3; };
            Output vert(Input v)
            {
                Output o;
                float4 anchor = UnityObjectToClipPos(v.vertex);
                float2 f = v.placement.xy;
                float2 r = float2(f.y,-f.x);
                float4 ahead = UnityObjectToClipPos(v.vertex + float4(f.x,0,f.y,0));
                float4 side = UnityObjectToClipPos(v.vertex + float4(r.x,0,r.y,0));
                float3 ndc = anchor.xyz/max(anchor.w,.0001);
                float3 df = ahead.xyz/max(ahead.w,.0001)-ndc;
                float3 dr = side.xyz/max(side.w,.0001)-ndc;
                float2 fp = df.xy*_ScreenParams.xy*.5;
                float2 rp = dr.xy*_ScreenParams.xy*.5;
                // Preserve an arrow's width when its heading points into the camera.
                float2 along = length(fp) > .05
                    ? normalize(fp)
                    : (length(rp) > .05 ? normalize(float2(-rp.y,rp.x)) : float2(0,1));
                float2 across = float2(along.y,-along.x);
                across *= dot(across,rp)<0 ? -1 : 1;
                o.kind = v.shape.z;
                float2 minimum = float2(14,18);
                o.halfSize = .5*max(minimum,v.placement.zw*float2(length(rp),length(fp)));
                o.uv = v.shape.xy;
                float2 local = v.vertex.xz + r * o.uv.x * v.placement.z * .5
                    + f * o.uv.y * v.placement.w * .5;
                o.metres = mul(unity_ObjectToWorld, float4(local.x, v.vertex.y, local.y, 1)).xz;
                float2 delta = across*o.uv.x*o.halfSize.x + along*o.uv.y*o.halfSize.y;
                o.pos = anchor;
                o.pos.xy += delta*2/_ScreenParams.xy*anchor.w;
                // Interpolate the pavement plane's depth across the enlarged screen quad.
                float det = rp.x*fp.y-rp.y*fp.x;
                if (abs(det) > .05 * max(.001, length(fp) * length(rp)))
                    o.pos.z += ((delta.x*fp.y-delta.y*fp.x)*dr.z +
                        (rp.x*delta.y-rp.y*delta.x)*df.z)*anchor.w/det;
                if (anchor.w <= 0) o.pos = float4(2,2,2,1);
                return o;
            }

            float segment(float2 p, float2 a, float2 b, float2 scale)
            {
                float2 edge=(b-a)*scale, offset=(p-a)*scale;
                return length(offset-edge*clamp(dot(offset,edge)/max(.0001,dot(edge,edge)),0.0,1.0));
            }
            float branch(float2 p, float2 scale, float side)
            {
                p.x *= side;
                float2 bend=float2(.27,.25), tip=float2(.82,.25);
                float d=segment(p,float2(0,-.15),bend,scale);
                d=min(d,segment(p,bend,tip,scale));
                d=min(d,segment(p,tip,float2(.52,.50),scale));
                return min(d,segment(p,tip,float2(.52,0),scale));
            }
            float symbolDistance(float2 p,float2 s,int k)
            {
                if (k==6 || k==7)
                {
                    if (k==6) p.x=-p.x;
                    float2 tip=float2(.65,.77);
                    float d=segment(p,float2(-.46,-.78),float2(-.46,-.17),s);
                    d=min(d,segment(p,float2(-.46,-.17),tip,s));
                    d=min(d,segment(p,tip,float2(.16,.65),s));
                    return min(d,segment(p,tip,float2(.63,.33),s));
                }
                float d=segment(p,float2(0,-.8),float2(0,-.15),s);
                if (k==1 || k==2 || k==3 || k==8)
                {
                    float2 tip=float2(0,.8);
                    d=min(d,segment(p,float2(0,-.15),tip,s));
                    d=min(d,segment(float2(abs(p.x),p.y),tip,float2(.41,.46),s));
                }
                if (k==1 || k==4 || k==5 || k==8) d=min(d,branch(p,s,-1));
                if (k==0 || k==2 || k==5 || k==8) d=min(d,branch(p,s,1));
                return d;
            }
            // World-space variation fades out as the grain becomes smaller than a pixel.
            float PaintWear(float2 metres)
            {
                float footprint = max(length(ddx(metres)), length(ddy(metres)));
                float2 patch = metres * 9.0;
                float grain = sin(dot(patch, float2(1.37, 2.11))) *
                    sin(dot(patch, float2(-2.43, 1.19)));
                return (1.0 - smoothstep(.025, .16, footprint)) *
                    smoothstep(.15, .85, grain);
            }

            fixed4 frag(Output i):SV_Target
            {
                float d=symbolDistance(i.uv,i.halfSize,(int)(i.kind+.5));
                float ink=1-smoothstep(.55,1.22,d);
                float wear = PaintWear(i.metres);
                float3 colour = float3(.91,.91,.87) * (1.0 - .12 * wear);
                return fixed4(colour * (1.0 - _Formula9GroundDim.rgb),
                ink * (.98 - .18 * wear));
            }
            ENDCG
        }
    }
}
