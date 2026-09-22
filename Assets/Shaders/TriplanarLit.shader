// Triplanar lit surface for URP.
//
// The port's geometry has no usable UVs, and that is deliberate rather than a
// gap: upstream samples every surface triplanar in object space, so a weapon's
// anodising grain is 9.5 cm of world scale on a 2 cm rail without anyone
// unwrapping anything. Baking UVs would have meant an unwrapper, an atlas, and
// a texel budget, to reproduce something the projection already gives away.
//
// Channels follow the bake (see tools/bake/pages/textures.html):
//   _Albedo  RGB = colour, A = cutout mask (foliage only)
//   _Normal  tangent-space, OpenGL +Y
//   _Mask    R = metalness, G = AO, A = smoothness   (URP Layout)
//   _Height  grey = the albedo's height channel, kept for future parallax
//
// Lighting is a hand-rolled main-light GGX rather than a URP Lit clone: the
// surfaces are procedural noise, not scanned materials, so the win from
// matching URP's exact BRDF is smaller than the risk of drifting from it.
Shader "ClaudeOfDuty/TriplanarLit"
{
    Properties
    {
        [MainTexture] _Albedo ("Albedo", 2D) = "white" {}
        [NoScaleOffset] _NormalTex ("Normal", 2D) = "bump" {}
        [NoScaleOffset] _Mask ("Mask (R metal, G ao, A smooth)", 2D) = "white" {}
        [NoScaleOffset] _HeightTex ("Height", 2D) = "grey" {}

        _Tiling ("Tiles per metre", Float) = 0.5
        _Tint ("Tint", Color) = (1,1,1,1)
        _NormalScale ("Normal scale", Range(0,2)) = 1
        _Occlusion ("Occlusion strength", Range(0,1)) = 1
        // Roughness comes from the mask's green channel and is remapped the way
        // the source shader remaps it: clamp(rough * scale + bias, min, 1).
        _RoughScale ("Roughness scale", Range(0,2)) = 1
        _RoughBias ("Roughness bias", Range(-1,1)) = 0
        _RoughMin ("Roughness minimum", Range(0,1)) = 0
        _Metallic ("Metallic scale", Range(0,2)) = 1
        _Ambient ("Ambient scale", Range(0,2)) = 1
        // How much of the environment this surface sees. Weapons sit at 0.24 —
        // a shouldered rifle has the shooter's own body and head blocking most
        // of the sky, and upstream measures this as the single largest reason
        // the viewmodel used to read as a sticker pasted on the frame.
        _EnvIntensity ("Environment intensity", Range(0,2)) = 1

        // Edge wear and cavity grime, driven by the baked vertex masks in
        // COLOR.rgb (wear / grime / ao) that the viewmodel bakes upstream.
        _WearColor ("Wear colour", Color) = (0.42,0.44,0.47,1)
        _WearParams ("Wear (amount, rough, metal, mix)", Vector) = (0,0,0,0)
        _GrimeColor ("Grime colour", Color) = (0.04,0.04,0.03,1)
        _Cavity ("Cavity grime", Range(0,1)) = 0

        [Toggle] _WorldSpace ("Project in world space", Float) = 0
        [Toggle(_ALPHATEST_ON)] _AlphaTest ("Alpha clip", Float) = 0
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        HLSLINCLUDE
        // Lighting.hlsl, not just Core: the shared helpers below take URP's
        // `Light` type, which only exists once that header is in scope. Pulling
        // it in at pass level is too late for anything declared up here.
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        TEXTURE2D(_Albedo);    SAMPLER(sampler_Albedo);
        TEXTURE2D(_NormalTex); SAMPLER(sampler_NormalTex);
        TEXTURE2D(_Mask);      SAMPLER(sampler_Mask);
        TEXTURE2D(_HeightTex); SAMPLER(sampler_HeightTex);

        CBUFFER_START(UnityPerMaterial)
            float4 _Albedo_ST;
            half4  _Tint;
            float  _Tiling;
            half   _NormalScale;
            half   _Occlusion;
            half   _RoughScale;
            half   _RoughBias;
            half   _RoughMin;
            half   _Metallic;
            half   _Ambient;
            half   _EnvIntensity;
            half4  _WearColor;
            half4  _WearParams;
            half4  _GrimeColor;
            half   _Cavity;
            half   _WorldSpace;
            half   _Cutoff;
        CBUFFER_END

        struct CodSurface
        {
            half3 albedo;
            half3 normalWS;
            half  metal;
            half  smooth;
            half  ao;
            half  alpha;
        };

        /** Blend weights that keep flat faces flat and only mix near 45 degrees. */
        half3 TriplanarWeights(half3 n)
        {
            half3 w = pow(abs(n), 4.0);
            return w / max(w.x + w.y + w.z, 1e-4);
        }

        /**
         * Morten Mikkelsen's triplanar normal blend: each axis' tangent-space
         * normal is rotated into world space before weighting, which is what
         * keeps the bumps pointing the same way across a blended edge.
         */
        half3 TriplanarNormal(float3 p, half3 n, half3 w)
        {
            half3 nx = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalTex, sampler_NormalTex, p.zy * _Tiling), _NormalScale);
            nx = half3(nx.xy + n.zy, abs(nx.z) * n.x);
            half3 ny = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalTex, sampler_NormalTex, p.xz * _Tiling), _NormalScale);
            ny = half3(ny.xy + n.xz, abs(ny.z) * n.y);
            half3 nz = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalTex, sampler_NormalTex, p.xy * _Tiling), _NormalScale);
            nz = half3(nz.xy + n.xy, abs(nz.z) * n.z);
            return normalize(nx * w.x + ny * w.y + nz * w.z);
        }

        CodSurface SampleCodSurface(float3 positionWS, half3 normalWS, half3 normalOS, half3 positionOS, half3 vertexMask)
        {
            float3 p = _WorldSpace > 0.5 ? positionWS : positionOS;
            half3 n = _WorldSpace > 0.5 ? normalWS : normalOS;
            half3 w = TriplanarWeights(n);

            float2 uvX = p.zy * _Tiling;
            float2 uvY = p.xz * _Tiling;
            float2 uvZ = p.xy * _Tiling;

            half4 ax = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, uvX);
            half4 ay = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, uvY);
            half4 az = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, uvZ);

            CodSurface s;
            half3 albedo = (ax.rgb * w.x + ay.rgb * w.y + az.rgb * w.z) * _Tint.rgb;

            half4 mx = SAMPLE_TEXTURE2D(_Mask, sampler_Mask, uvX);
            half4 my = SAMPLE_TEXTURE2D(_Mask, sampler_Mask, uvY);
            half4 mz = SAMPLE_TEXTURE2D(_Mask, sampler_Mask, uvZ);
            half4 mask = mx * w.x + my * w.y + mz * w.z;

            // _Mask is the URP-layout map: R metalness, G occlusion, A smoothness.
            half rough = saturate((1.0h - mask.a) * _RoughScale + _RoughBias);

            // Cavity grime reads the height field's valleys, so it cannot swim
            // when the object moves — the same reason upstream drives it from
            // height rather than from world space.
            half hx = SAMPLE_TEXTURE2D(_HeightTex, sampler_HeightTex, uvX).r;
            half hy = SAMPLE_TEXTURE2D(_HeightTex, sampler_HeightTex, uvY).r;
            half hz = SAMPLE_TEXTURE2D(_HeightTex, sampler_HeightTex, uvZ).r;
            half height = hx * w.x + hy * w.y + hz * w.z;
            half cav = 1.0h - height;

            // Edge wear: convex corners lose their coating to bare metal.
            half wear = saturate(vertexMask.r * _WearParams.x);
            albedo = lerp(albedo, _WearColor.rgb, wear * _WearParams.w);
            rough = lerp(rough, _WearParams.y, wear);
            half metalBase = mask.r;
            half metal = lerp(metalBase, _WearParams.z, wear);
            metal = saturate(metal * _Metallic);

            half grime = saturate(vertexMask.g + cav * cav * _Cavity);
            albedo = lerp(albedo, _GrimeColor.rgb, grime);

            s.albedo = albedo;
            s.metal = metal;
            s.smooth = saturate(1.0h - max(rough, _RoughMin));
            s.ao = lerp(1.0h, saturate(mask.g * vertexMask.b), _Occlusion);
            s.normalWS = TriplanarNormal(p, n, w);
            // Alpha matters only for cutout surfaces; everywhere else it is a
            // height field and must not be allowed to clip anything.
            s.alpha = ax.a * w.x + ay.a * w.y + az.a * w.z;
            return s;
        }

        /** Cook-Torrance GGX, single main light. */
        half3 DirectBRDF(CodSurface s, Light light, half3 viewDirWS, half3 normalWS)
        {
            half3 l = light.direction;
            half3 h = normalize(l + viewDirWS);
            half NoL = saturate(dot(normalWS, l));
            half NoV = saturate(abs(dot(normalWS, viewDirWS)) + 1e-5);
            half NoH = saturate(dot(normalWS, h));
            half VoH = saturate(dot(viewDirWS, h));

            half a = max(s.smooth * s.smooth, 1e-3);
            half a2 = a * a;
            half d = (NoH * NoH) * (a2 - 1.0) + 1.0;
            half D = a2 / max(PI * d * d, 1e-6);

            half k = a * 0.5;
            half G = (NoL / (NoL * (1.0 - k) + k)) * (NoV / (NoV * (1.0 - k) + k));

            half3 F0 = lerp(0.04h, s.albedo, s.metal);
            half3 F = F0 + (1.0 - F0) * pow(1.0 - VoH, 5.0);

            half3 spec = (D * G) * F / max(4.0 * NoL * NoV, 1e-4);
            half3 diffuse = (1.0 - s.metal) * s.albedo * (1.0 - F) * (1.0 / PI);

            return (diffuse + spec) * light.color * NoL * light.distanceAttenuation * light.shadowAttenuation;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma shader_feature_local _ALPHATEST_ON

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                half4  color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 positionOS : TEXCOORD1;
                half3  normalWS : TEXCOORD2;
                half3  normalOS : TEXCOORD3;
                float  fogFactor : TEXCOORD4;
                half3  vertexMask : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                VertexPositionInputs vpi = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs vni = GetVertexNormalInputs(IN.normalOS);

                OUT.positionCS = vpi.positionCS;
                OUT.positionWS = vpi.positionWS;
                OUT.positionOS = IN.positionOS.xyz;
                OUT.normalWS = vni.normalWS;
                OUT.normalOS = IN.normalOS;
                OUT.vertexMask = IN.color.rgb;
                OUT.fogFactor = ComputeFogFactor(vpi.positionCS.z);
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                // Parallax is off for now: the height channel is baked and
                // shipped, but a 22-step march needs the procedural albedo's own
                // height to stay in sync, so it is a fidelity pass of its own.
                CodSurface s = SampleCodSurface(IN.positionWS, normalize(IN.normalWS), normalize(IN.normalOS), IN.positionOS, IN.vertexMask);

                #ifdef _ALPHATEST_ON
                clip(s.alpha - _Cutoff);
                #endif

                half3 viewDirWS = normalize(GetWorldSpaceViewDir(IN.positionWS));
                half3 shadingNormal = s.normalWS;

                half4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light main = GetMainLight(shadowCoord);

                half3 colour = DirectBRDF(s, main, viewDirWS, shadingNormal);
                colour += SampleSH(shadingNormal) * s.albedo * s.ao * _Ambient;

                // Environment specular. Without this a 0.01-albedo receiver lit
                // only by a directional light is black, which is exactly how
                // upstream photographs the gun: mostly specular, mostly from the
                // sky, heavily occluded by the shooter. The Fresnel term is
                // computed here rather than borrowed from URP so the shader does
                // not depend on a helper whose signature moved between versions.
                half3 reflectDir = reflect(-viewDirWS, shadingNormal);
                half perceptualRoughness = 1.0h - s.smooth;
                half3 F0 = lerp(0.04h, s.albedo, s.metal);
                half NoV = saturate(dot(shadingNormal, viewDirWS));
                half3 envF = F0 + (max(1.0h - perceptualRoughness, F0) - F0) * pow(1.0h - NoV, 5.0h);
                half3 env = GlossyEnvironmentReflection(reflectDir, perceptualRoughness, s.ao);
                colour += env * envF * _EnvIntensity;

                colour = MixFog(colour, IN.fogFactor);
                return half4(colour, 1.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #pragma shader_feature_local _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            ShadowVaryings ShadowVert(ShadowAttributes IN)
            {
                ShadowVaryings OUT = (ShadowVaryings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(IN.normalOS);
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));

                #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                OUT.positionCS = positionCS;
                return OUT;
            }

            half4 ShadowFrag(ShadowVaryings IN) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            DepthVaryings DepthVert(DepthAttributes IN)
            {
                DepthVaryings OUT = (DepthVaryings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 DepthFrag(DepthVaryings IN) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
