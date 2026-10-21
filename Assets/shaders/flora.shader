HEADER
{
	Description = "Lean vegetation shader - base colour, normal map, alpha test, custom foliage lighting";
	DevShader = false;
}

FEATURES
{
	#include "common/features.hlsl"

	Feature( F_ALPHA_TEST, 0..1, "Rendering" );
}

MODES
{
	Forward();
	Depth( S_MODE_DEPTH );
	ToolsShadingComplexity( "tools_shading_complexity.shader" );
}

COMMON
{
	#include "common/shared.hlsl"
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
};

VS
{
	#include "common/vertex.hlsl"

	// Nothing custom here on purpose. ProcessVertex feeds the standard instancing path, which is
	// what lets the engine batch every flora scene object sharing this material into one draw.
	PixelInput MainVs( VertexInput v )
	{
		return FinalizeVertex( ProcessVertex( v ) );
	}
}

PS
{
	#define CUSTOM_MATERIAL_INPUTS 1

	#include "common/pixel.hlsl"
	#include "common/classes/Light.hlsl"
	#include "common/classes/AmbientLight.hlsl"

	StaticCombo( S_ALPHA_TEST, F_ALPHA_TEST, Sys( ALL ) );

	// Leaf cards are single-sided geometry seen from both sides.
	RenderState( CullMode, F_RENDER_BACKFACES ? NONE : DEFAULT );

	// The forward pass rides on the depth prepass: flora renders as scene objects, so the prepass
	// has already written its depth and run the alpha test. Testing EQUAL instead of writing again
	// lets early-Z throw away hidden fragments before they shade, which is most of the win on
	// layered leaf cards where overdraw is worst.
	#if ( S_MODE_DEPTH == 0 )
		RenderState( DepthFunc, EQUAL );
		RenderState( DepthWriteEnable, false );
	#endif

	CreateInputTexture2D( BaseColor, Srgb, 8, "None", "_color", "Albedo,1/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tBaseColor < Channel( RGBA, Box( BaseColor ), Srgb ); OutputFormat( DXT5 ); SrgbRead( True ); >;

	CreateInputTexture2D( NormalMap, Linear, 8, "NormalizeNormals", "_normal", "Normal,2/,0/0", Default4( 0.50, 0.50, 1.00, 1.00 ) );
	Texture2D g_tNormalMap < Channel( RGBA, Box( NormalMap ), Linear ); OutputFormat( DXT5 ); SrgbRead( False ); >;

	float3 g_vTint < UiType( Color ); UiGroup( "Albedo,1/,0/0" ); Default3( 1.00, 1.00, 1.00 ); >;

	#if S_ALPHA_TEST
		float g_flAlphaTestRef < UiGroup( "Alpha Test,3/,0/0" ); Default1( 0.5 ); Range1( 0, 1 ); >;
	#endif

	// Leaves are thin and translucent, so light coming through from behind matters as much as light
	// landing on the front.
	float g_flTransmission < UiGroup( "Foliage,4/,0/0" ); Default1( 0.6 ); Range1( 0, 2 ); >;

	// Softens the terminator. A hard cutoff across a canopy reads as a mass of black edges.
	float g_flWrap < UiGroup( "Foliage,4/,0/0" ); Default1( 0.5 ); Range1( 0, 1 ); >;

	// Pushes the shading normal toward world up. Canopies lit by their true card normals look like
	// a pile of flat planes; biasing up makes a tree read as one soft volume.
	float g_flNormalUpBias < UiGroup( "Foliage,4/,0/0" ); Default1( 0.3 ); Range1( 0, 1 ); >;

	float g_flAmbientScale < UiGroup( "Foliage,4/,0/0" ); Default1( 1.0 ); Range1( 0, 2 ); >;

	// Cascade shadows without the screen-space contact shadow mask that
	// DirectionalLightShadow::GetVisibility folds in. Flora does take part in the depth prepass, so
	// that mask is valid here - but it costs a texture fetch per pixel per light for an effect that
	// is invisible on a canopy, so it is skipped deliberately rather than out of necessity.
	float SunVisibility( float3 worldPosition, float2 screenPosition )
	{
		if ( g_DirectionalLightCascadeCount == 0 )
			return 1.0f;

		// FindCascade is a free function in DirectionalLightShadow.hlsl, not a member of the struct.
		float3 positionLs;
		int cascade = FindCascade( worldPosition, positionLs );

		if ( cascade < 0 )
			return 1.0f;

		return DirectionalLightShadow::SampleCascade( cascade, worldPosition, screenPosition );
	}

	// What a pixel gets when DDGI does not cover it: the baked probes if the scene has them,
	// otherwise the reflection probes and the flat ambient.
	float3 Fallback( float3 worldPosition, float4 positionSs, float3 normalWs )
	{
		if ( UsesBakedLightingFromProbe )
			return AmbientLight::FromLightMapProbeVolume( worldPosition, normalWs );

		return AmbientLight::FromEnvMapProbe( worldPosition, positionSs, normalWs );
	}

	// The ambient the rest of the scene gets. AmbientLight::From can't be used as it is: once there is any DDGI volume in
	// the scene it takes everything from DDGI, and gives whatever is outside every volume no ambient at all - pitch black
	// wherever the sun doesn't reach. The engine's own shading (vr_lighting.fxc) asks per pixel instead: DDGI inside a
	// volume, otherwise the baked probes if the scene has them, otherwise the reflection probes and the flat ambient.
	float3 FloraAmbient( float3 worldPosition, float4 positionSs, float3 normalWs )
	{
		if ( DDGI::IsEnabled() )
		{
			// EvaluateVolumes blends every volume covering the pixel and reports how much of it they
			// account for in .a. Weighting the fallback by what is left over means a pixel at a
			// volume's edge fades between the two instead of switching at the boundary.
			float4 ddgi = DDGI::EvaluateVolumes( worldPosition, normalWs, CalculatePositionToCameraDirWs( worldPosition ) );

			if ( ddgi.a >= 1.0f )
				return ddgi.rgb;

			return ddgi.rgb + Fallback( worldPosition, positionSs, normalWs ) * ( 1.0f - ddgi.a );
		}

		return Fallback( worldPosition, positionSs, normalWs );
	}

	// How much of a light a leaf takes from its angle to it: wrapped round the terminator on the front, and through the
	// leaf from behind.
	float FoliageLighting( float ndotl )
	{
		float front = saturate( ( ndotl + g_flWrap ) / ( 1.0f + g_flWrap ) );
		float back = saturate( -ndotl ) * g_flTransmission;

		return front + back;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 uv = i.vTextureCoords.xy;

		float4 color = Tex2DS( g_tBaseColor, g_sAniso, uv );

		#if S_ALPHA_TEST
			// Discard before doing any lighting work - on alpha-tested foliage most invocations end
			// here, and everything below is wasted on them.
			clip( color.a - g_flAlphaTestRef );
		#endif

		float3 albedo = color.rgb * g_vTint;

		float3 tangentNormal = DecodeNormal( Tex2DS( g_tNormalMap, g_sAniso, uv ).rgb );
		float3 normalWs = normalize( TransformNormal( tangentNormal, i.vNormalWs, i.vTangentUWs, i.vTangentVWs ) );

		// Cards are drawn two-sided, so on a backface the interpolated normal points away from the
		// camera and lighting collapses. Flip it before biasing, or the bias fights the flip.
		#if ( S_RENDER_BACKFACES )
			normalWs = i.face ? normalWs : -normalWs;
		#endif

		normalWs = normalize( lerp( normalWs, float3( 0, 0, 1 ), g_flNormalUpBias ) );

		float3 worldPosition = i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz;

		float3 diffuse = 0.0f;

		// The sun. The engine lights it on its own rather than through the cluster's light list, which holds point and
		// spot lights only - except on baked lighting, where it comes in with the baked lights in the loop below. The
		// same split as ComputeDirectLighting in vr_lighting.fxc.
		if ( g_DirectionalLightEnabled && !LightmappedLight::UsesLightmaps() && !ProbeLight::UsesProbes() )
		{
			float sunNdotL = dot( normalWs, -g_DirectionalLightDirection.xyz );

			diffuse += g_DirectionalLightColor.rgb * SunVisibility( worldPosition, i.vPositionSs.xy ) * FoliageLighting( sunNdotL );
		}

		uint lightCount = Light::Count( i.vPositionSs );
		for ( uint index = 0; index < lightCount; index++ )
		{
			Light light = Light::From( worldPosition, i.vPositionSs, index );

			if ( light.LightData.Type == LightType::LightTypeDirectional )
			{
				// Attenuation is meant to be 1 for directional lights, but GetLightAttenuation does
				// not special-case type - it runs the full spot-cone and distance-falloff maths
				// against a synthetic light position.
				light.Attenuation = 1.0f;
				light.Visibility = SunVisibility( worldPosition, i.vPositionSs.xy );
			}

			float ndotl = dot( normalWs, light.Direction );

			diffuse += light.Color * light.Attenuation * light.Visibility * FoliageLighting( ndotl );
		}

		float3 ambient = FloraAmbient( worldPosition, i.vPositionSs, normalWs ) * g_flAmbientScale;

		float4 result = float4( albedo * ( diffuse + ambient ), 1.0f );

		// The same call ShadingModelStandard::Shade ends on, so gradient, cubemap and volumetric fog
		// all match the rest of the scene. Without it foliage stays saturated while everything around
		// it fades into the distance. Costs nothing when the scene has no fog - it early-outs on
		// g_bFogEnabled.
		return DoAtmospherics( worldPosition, i.vPositionSs.xy, result );
	}
}
