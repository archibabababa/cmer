Shader "Hidden/CMER/Section" {
 Properties { _Color ("Color", Color) = (0.8,0.8,0.8,1) _CutPlane ("Plane", Vector)=(0,0,0,1) }
 SubShader { Tags { "RenderType"="Opaque" } Cull Off
 Pass { CGPROGRAM
 #pragma vertex vert
 #pragma fragment frag
 #include "UnityCG.cginc"
 float4 _Color; float4 _CutPlane;
 struct v2f { float4 pos:SV_POSITION; float3 world:TEXCOORD0; float3 normal:TEXCOORD1; };
 v2f vert(appdata_base v){v2f o;o.pos=UnityObjectToClipPos(v.vertex);o.world=mul(unity_ObjectToWorld,v.vertex).xyz;o.normal=UnityObjectToWorldNormal(v.normal);return o;}
 fixed4 frag(v2f i):SV_Target {clip(_CutPlane.w-dot(_CutPlane.xyz,i.world));float light=.65+.35*abs(dot(normalize(i.normal),normalize(float3(.4,.8,-.3))));return float4(_Color.rgb*light,1);}
 ENDCG }
 }
}
