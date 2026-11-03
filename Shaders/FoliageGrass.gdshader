// Grass painted as foliage: lit like the ground it stands on, in its colour. Its blades' normals all
// point up (as exported); both their faces keep them so, rather than the back face being lit as if
// it faced down. Its colour comes from each blade's instance (the FoliageField sets it to the
// terrain's flat colour under it, given in sRGB as the terrain's are).
shader_type spatial;
render_mode cull_disabled;

uniform float roughness : hint_range(0, 1) = 0.9;

vec3 to_linear(vec3 c) {
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

void fragment() {
	ALBEDO = to_linear(COLOR.rgb);
	ROUGHNESS = roughness;
	// The back face as lit as the front: up, like the ground.
	if (!FRONT_FACING) {
		NORMAL = -NORMAL;
	}
}
