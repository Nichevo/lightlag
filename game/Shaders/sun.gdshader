// The star (Medium and High): a boiling surface darker at the limb, a corona of soft streamers that
// slowly churn, and glare streaks across the lens. Colours above 1 bloom on High. SunSprite draws a
// square from -size to +size and sets the uniforms.
shader_type canvas_item;
render_mode blend_add;

uniform vec4 core_color : source_color = vec4(1.0, 0.85, 0.55, 1.0);
uniform float radius_px = 10.0;
uniform float half_px = 100.0;       // half the square's side
uniform float boost = 1.0;           // above 1 on High so the core blooms
uniform float seed = 0.0;
uniform float glare = 1.0;           // strength of the streaks (fades when zoomed right in)

varying vec2 local;

void vertex() {
	local = VERTEX;
}

float h31(vec3 p) {
	p = fract(p * 0.3183099 + vec3(0.11, 0.17, 0.13) + seed);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float noise3(vec3 x) {
	vec3 i = floor(x);
	vec3 f = fract(x);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(mix(h31(i), h31(i + vec3(1, 0, 0)), f.x), mix(h31(i + vec3(0, 1, 0)), h31(i + vec3(1, 1, 0)), f.x), f.y),
	           mix(mix(h31(i + vec3(0, 0, 1)), h31(i + vec3(1, 0, 1)), f.x), mix(h31(i + vec3(0, 1, 1)), h31(i + vec3(1, 1, 1)), f.x), f.y), f.z);
}

float fbm3(vec3 p) {
	float v = 0.0, a = 0.5;
	for (int i = 0; i < 5; i++) {
		v += a * noise3(p);
		p = p * 2.02 + vec3(1.3, 7.1, 4.7);
		a *= 0.5;
	}
	return v;
}

void fragment() {
	float t = TIME * 0.05;
	vec2 q = local / radius_px;
	float d = length(q);
	vec3 hot = core_color.rgb;
	vec3 white = vec3(1.0, 0.97, 0.9);
	vec3 col = vec3(0.0);
	float alpha = 0.0;

	if (d < 1.0) {
		// The disc: granulation that boils slowly, brighter in the middle, darker and redder at the limb.
		float z = sqrt(1.0 - d * d);
		vec3 n = vec3(q, z);
		float gran = fbm3(n * 9.0 + vec3(0.0, 0.0, t * 2.0));
		float spots = smoothstep(0.62, 0.7, fbm3(n * 3.0 + vec3(t * 0.3, 0.0, 5.0)));
		float limb = pow(z, 0.45);
		vec3 disc = mix(hot * vec3(1.0, 0.7, 0.5), mix(hot, white, 0.6), limb);
		disc *= 0.85 + 0.3 * gran;
		disc *= 1.0 - 0.35 * spots;
		float edge = smoothstep(1.0, 1.0 - 1.5 / max(radius_px, 1.0), d);
		col += disc * boost * edge;
		alpha = edge;
	}

	// Corona: soft light falling off with distance, broken into streamers that churn.
	float ang = atan(q.y, q.x);
	float ring = max(d - 1.0, 0.0);
	float px = length(local);
	float streamer = fbm3(vec3(cos(ang) * 2.5, sin(ang) * 2.5, ring * 1.2 - t * 1.5));
	float fall = exp(-ring * 2.2) * (0.55 + 0.9 * streamer);
	// Keep a halo of a fixed few pixels too, so a tiny far-off sun still glows.
	float minHalo = exp(-max(px - radius_px, 0.0) / 9.0) * 0.6;
	float corona = max(fall * 0.55, minHalo) * smoothstep(half_px, half_px * 0.6, px);
	col += hot * corona * mix(1.0, boost, 0.5);

	// Glare: thin streaks through the centre (a long level one and four short ones), and a faint ring.
	vec2 g = local / half_px;
	float level = exp(-abs(g.y) * 160.0) * exp(-abs(g.x) * 2.2);
	float cross1 = exp(-abs(g.x - g.y) * 120.0) + exp(-abs(g.x + g.y) * 120.0);
	cross1 *= exp(-length(g) * 5.0);
	float halo = exp(-pow((length(g) - 0.32) * 28.0, 2.0)) * 0.05;
	col += mix(hot, white, 0.5) * (level * 0.55 + cross1 * 0.3 + halo) * glare;

	COLOR = vec4(col, 1.0);
}
