HEADER
{
	Description = "Lean vegetation shader - base colour, normal map, alpha test, custom foliage lighting";
	DevShader = false;
}

FEATURES
{
	#include "common/features.hlsl"

	Feature( F_ALPHA_TEST, 0..1, "Rendering" );

	Feature( F_WIND, 0..1, "Wind" );

	// Whether the model carries the usual foliage weights in its vertex colour: red how much an edge
	// flutters, green how much a branch sways, blue a phase to put it out of step with its
	// neighbours. Models without them get those from the vertex's height instead, which keeps a trunk
	// still at its base rather than wobbling it like a leaf.
	Feature( F_WIND_VERTEX_WEIGHTS, 0..1, "Wind" );
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

	// Not part of the standard input - declared here the way the engine's own foliage shader does.
	// Where a model carries them, the wind weights live in it.
	float4 vColor : COLOR0 < Semantic( Color ); >;
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
};

VS
{
	#include "common/vertex.hlsl"
	#include "common/trunk_bending.hlsl"

	StaticCombo( S_WIND, F_WIND, Sys( ALL ) );
	StaticCombo( S_WIND_VERTEX_WEIGHTS, F_WIND_VERTEX_WEIGHTS, Sys( ALL ) );

#if S_WIND

	// Bending goes with the square of height times this, and the constant inside ApplyTrunkBending is
	// sized for metres rather than inches - so for a tree a thousand units tall anything near 1 moves
	// it by a fraction of a unit, which reads as nothing at all. Tall trees want this well up.
	float g_flSwayStrength < Default( 10.0 ); Range( 0.0, 25.0 ); UiGroup( "Wind,10/Trunk" ); >;
	float g_flSwaySpeed < Default( 1.0 ); Range( 0.0, 10.0 ); UiGroup( "Wind,10/Trunk" ); >;

	float g_flEdgeFrequency < Default( 1.0 ); Range( 0.1, 5.0 ); UiGroup( "Wind,20/Detail" ); >;
	float g_flEdgeAmplitude < Default( 0.1 ); Range( 0.0, 1.0 ); UiGroup( "Wind,20/Detail" ); >;
	float g_flBranchFrequency < Default( 0.5 ); Range( 0.1, 5.0 ); UiGroup( "Wind,20/Detail" ); >;
	float g_flBranchAmplitude < Default( 0.1 ); Range( 0.0, 1.0 ); UiGroup( "Wind,20/Detail" ); >;

	// The material's own wind, which the scene's is added to rather than replaced by. Added, because a
	// scene that sets a very small wind would otherwise count as having one and leave the material's
	// unused - nothing visibly moving, and no way to tell why from looking at it.
	float g_flWindStrength < Default( 5.0 ); Range( 0.0, 25.0 ); UiGroup( "Wind,30/Strength" ); >;
	float2 g_vWindDirection2D < Default2( 1.0, 0.0 ); UiGroup( "Wind,30/Strength" ); >;

	/// Multiplies both together, for turning the whole thing up or down in one place.
	float g_flWindScale < Default( 1.0 ); Range( 0.0, 4.0 ); UiGroup( "Wind,30/Strength" ); >;

	#if !S_WIND_VERTEX_WEIGHTS
		// How far up the model counts as fully exposed, for a model with no wind weights painted in.
		float g_flWindFullHeight < Default( 256.0 ); Range( 16.0, 4096.0 ); UiGroup( "Wind,30/Strength" ); >;
	#endif

	float4 SmoothCurve( float4 x ) { return x * x * ( 3.0 - 2.0 * x ); }
	float4 TriangleWave( float4 x ) { return abs( frac( x + 0.5 ) * 2.0 - 1.0 ); }
	float4 SmoothTriangleWave( float4 x ) { return SmoothCurve( TriangleWave( x ) ); }

	// High-frequency bending, after GPU Gems 3 chapter 16 and the engine's own foliage shader: edges
	// flutter along their normal, branches sway up and along the wind, each on its own phase.
	void FloraDetailBending( inout float3 vPositionOs, float3 vNormalOs, float3 vWeights, float3x4 matObjectToWorld, float3 vWind )
	{
		const float4 vFoliageFreqs = float4( 1.975, 0.793, 0.375, 0.193 );

		const float flEdgeAtten = vWeights.r;
		const float flBranchAtten = vWeights.g;
		const float flDetailPhase = vWeights.b;

		// Taken from where the tree stands, so no two of them move together. Without it a forest
		// sways as one sheet, which is the thing that gives procedural wind away.
		float flObjPhase = dot( mul( matObjectToWorld, float4( 0, 0, 0, 1 ) ), 1 );
		float flBranchPhase = flDetailPhase + flObjPhase;
		float flVtxPhase = dot( vPositionOs.xyz, flDetailPhase + flBranchPhase );

		const float maxPhase = 50000.0f;

		float2 vTime = g_flTime * float2( g_flEdgeFrequency, g_flBranchFrequency );
		float2 vPhaseOffset = fmod( float2( flVtxPhase, flBranchPhase ), maxPhase );
		float2 vWavesIn = vTime + vPhaseOffset;

		float4 vWaves = frac( vWavesIn.xxyy * vFoliageFreqs ) * 2.0 - 1.0;
		vWaves = SmoothTriangleWave( vWaves );
		float2 vWavesSum = vWaves.xz + vWaves.yw;

		float flWindIntensity = saturate( length( vWind ) * 0.2 );

		// A branch across the wind is pushed more than one pointing along it.
		float flBranchWindBend = 1.0f - abs( dot( normalize( vWind.xyz + 0.001 ), normalize( float3( vPositionOs.xy, 0.0f ) + 0.001 ) ) );
		flBranchWindBend *= flBranchWindBend;

		vPositionOs.xyz += vWavesSum.x * flEdgeAtten * g_flEdgeAmplitude * flWindIntensity * vNormalOs.xyz;
		vPositionOs.xyz += vWavesSum.y * flBranchAtten * g_flBranchAmplitude * flWindIntensity * float3( 0.0f, 0.0f, 1.0f );
		vPositionOs.xyz += vWavesSum.y * flBranchAtten * g_flBranchAmplitude * flBranchWindBend * flWindIntensity * vWind.xyz;
	}

#endif

	PixelInput MainVs( VertexInput v )
	{
		PixelInput o = ProcessVertex( v );

#if S_WIND
		float3 vNormalOs;
		float4 vTangentUOs_flTangentVSign;

		VS_DecodeObjectSpaceNormalAndTangent( v, vNormalOs, vTangentUOs_flTangentVSign );

		float3 vPositionOs = v.vPositionOs.xyz;
		float3x4 matObjectToWorld = GetTransformMatrix( v.nInstanceTransformID );

		// The scene's wind, or the material's own when the scene sets none - the engine leaves these
		// at zero in a scene with nothing driving them, and then nothing but the trunk would move.
		float3 vSceneWind = g_vWindDirection.xyz * g_vWindStrengthFreqMulHighStrength.x;

		// The detail bending reads intensity as saturate( length * 0.2 ), so a unit vector only reaches
		// a fifth of its range - which is why the material's strength is in whole units rather than
		// fractions, and why 5 is where it starts to look like weather.
		float3 vOwnWind = float3( normalize( g_vWindDirection2D.xy + 0.0001f ), 0.0f ) * g_flWindStrength;
		float3 vWind = ( vSceneWind + vOwnWind ) * g_flWindScale;

		#if S_WIND_VERTEX_WEIGHTS
			float3 vWeights = v.vColor.rgb;
		#else
			// No weights painted in, so height stands in for them: nothing at the base, everything at
			// the crown. The phase varies down the trunk so one tree does not move all of a piece.
			float flUp = saturate( vPositionOs.z / g_flWindFullHeight );
			float3 vWeights = float3( flUp * flUp, flUp, frac( vPositionOs.z * 0.01f ) );
		#endif

		ApplyTrunkBending( vPositionOs, g_flSwayStrength, g_flSwaySpeed, vWind, g_flTime );
		FloraDetailBending( vPositionOs, vNormalOs, vWeights, matObjectToWorld, vWind );

		o.vPositionWs = mul( matObjectToWorld, float4( vPositionOs.xyz, 1.0 ) );
		o.vPositionPs.xyzw = Position3WsToPs( o.vPositionWs.xyz );
#endif

		return FinalizeVertex( o );
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
