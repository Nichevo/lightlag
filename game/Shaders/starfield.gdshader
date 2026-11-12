// The sky behind the map: three layers of stars that drift at different rates as the view pans
// (parallax), a faint galaxy band with dark dust lanes, and two soft nebula clouds. Everything is made
// from the game's seed, so each game has its own sky. Drawn on one full-screen rectangle.
shader_type canvas_item;

uniform float seed = 0.0;
uniform vec2 pan_far = vec2(0.0);    // pixel offsets of each layer (worked out by Backdrop.cs)
uniform vec2 pan_mid = vec2(0.0);
uniform vec2 pan_near = vec2(0.0);
uniform vec2 band_dir = vec2(0.94, 0.34);   // the galaxy band's direction across the screen
uniform float band_offset = 0.0;            // how far from the screen centre it runs (pixels)
uniform vec3 band_tint : source_color = vec3(0.75, 0.72, 0.9);
uniform vec2 neb1 = vec2(300.0, 200.0);     // nebula centres, in sky pixels (before panning)
uniform vec2 neb2 = vec2(1200.0, 700.0);
uniform vec3 neb1_color : source_color = vec3(0.25, 0.45, 0.6);
uniform vec3 neb2_color : source_color = vec3(0.55, 0.25, 0.45);
uniform float neb1_size = 420.0;
uniform float neb2_size = 360.0;
uniform float ui_scale = 1.0;              // the interface size: screen pixels per sky pixel
uniform float brightness = 1.0;             // above 1 on High, so the brightest stars bloom a little
uniform vec3 base = vec3(0.02, 0.03, 0.05); // the plain background colour underneath

float h21(vec2 p) {
	p = fract(p * vec2(123.34, 456.21) + seed);
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

vec2 h22(vec2 p) {
	float a = h21(p);
	return vec2(a, h21(p + a + 17.0));
}

float vnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(h21(i), h21(i + vec2(1.0, 0.0)), f.x), mix(h21(i + vec2(0.0, 1.0)), h21(i + vec2(1.0, 1.0)), f.x), f.y);
}

float fbm(vec2 p) {
	float v = 0.0;
	float a = 0.5;
	for (int i = 0; i < 5; i++) {
		v += a * vnoise(p);
		p = p * 2.07 + vec2(3.1, 1.7);
		a *= 0.5;
	}
	return v;
}

// One layer of stars: at most one per cell, at a random spot, with a random size, brightness and colour.
vec3 stars(vec2 p, float cell, float chance, float size, float bright) {
	vec2 c = floor(p / cell);
	vec2 r = h22(c + cell);
	if (r.x > chance) return vec3(0.0);
	vec2 at = (c + 0.15 + 0.7 * h22(c + 7.0 + cell)) * cell;
	float d = length(p - at);
	float s = size * (0.5 + 1.2 * r.y * r.y);
	float core = smoothstep(s, 0.0, d);
	float halo = exp(-d * d / (s * s * 9.0)) * 0.25 * step(0.75, r.y);
	float b = bright * (0.35 + 0.65 * fract(r.y * 13.7)) * (core + halo);
	// Colour by temperature: mostly white, some blue, some amber.
	float t = fract(r.x * 91.3);
	vec3 col = t < 0.2 ? vec3(0.7, 0.8, 1.0) : t > 0.85 ? vec3(1.0, 0.82, 0.62) : vec3(0.95, 0.95, 1.0);
	return col * b;
}

void fragment() {
	vec2 px = FRAGCOORD.xy / ui_scale;
	vec3 col = vec3(0.0);

	// Galaxy band: brightest along a line, broken up by dust.
	vec2 bp = px + pan_far * 0.6;
	vec2 n = vec2(-band_dir.y, band_dir.x);
	float across = dot(bp - 0.5 / SCREEN_PIXEL_SIZE / ui_scale, n) - band_offset;
	float along = dot(bp, band_dir);
	float band = exp(-across * across / (2.0 * 170.0 * 170.0));
	float glow = fbm(vec2(along, across) * 0.004 + seed);
	float dust = smoothstep(0.45, 0.75, fbm(vec2(along * 0.006, across * 0.02) + seed * 3.0 + 11.0));
	col += band_tint * band * (0.035 + 0.05 * glow) * (1.0 - 0.75 * dust);
	// The band is thick with faint stars.
	col += stars(bp, 8.0, 0.22 * band, 0.65, 0.28);

	// Two nebula clouds, soft and wispy.
	vec2 q1 = (px + pan_far - neb1) / neb1_size;
	float m1 = exp(-dot(q1, q1) * 1.6) * smoothstep(0.35, 0.85, fbm(q1 * 2.2 + seed * 5.0));
	vec2 q2 = (px + pan_far - neb2) / neb2_size;
	float m2 = exp(-dot(q2, q2) * 1.6) * smoothstep(0.35, 0.85, fbm(q2 * 2.4 + seed * 7.0 + 4.0));
	col += neb1_color * m1 * 0.16 + neb2_color * m2 * 0.14;

	// Three star layers, far to near: more and fainter far away, fewer and brighter close by.
	col += stars(px + pan_far, 26.0, 0.3, 0.7, 0.42);
	col += stars(px + pan_mid, 54.0, 0.32, 0.9, 0.65);
	col += stars(px + pan_near, 130.0, 0.35, 1.15, 0.95) * brightness;

	COLOR = vec4(base + col, 1.0);
}
