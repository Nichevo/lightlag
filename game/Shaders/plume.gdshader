// Engine plumes (Medium and High), added onto the map. Each plume is one quad from FxQuads: UV.x runs
// from the engine bell (0) to the tail (1), UV.y across. The vertex colour carries the strength (r), a
// per-ship seed (g) and how hot the drive runs (b: 0 blue-white, 1 violet-white). Bright sparks stream
// down it, so it reads as a jet of particles.
shader_type canvas_item;
render_mode blend_add;

uniform float boost = 1.0;   // above 1 on High so the core blooms

float h21(vec2 p) {
	p = fract(p * vec2(233.34, 851.73));
	p += dot(p, p + 23.45);
	return fract(p.x * p.y);
}

float vnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(h21(i), h21(i + vec2(1.0, 0.0)), f.x), mix(h21(i + vec2(0.0, 1.0)), h21(i + vec2(1.0, 1.0)), f.x), f.y);
}

void fragment() {
	float strength = COLOR.r;
	float s = COLOR.g * 97.0;
	float x = UV.x;
	float y = (UV.y - 0.5) * 2.0;
	float t = TIME;
	float width = mix(0.22, 1.0, pow(x, 0.55));
	float across = abs(y) / width;
	float flick = vnoise(vec2(x * 9.0 - t * 16.0, s)) * 0.5 + vnoise(vec2(x * 23.0 - t * 31.0, s + 4.0)) * 0.5;
	float core = exp(-across * across * 9.0) * pow(1.0 - x, 2.2) * smoothstep(0.0, 0.04, x);
	float body = exp(-across * across * 2.6) * pow(1.0 - x, 1.1) * (0.65 + 0.5 * flick);
	// Shock diamonds near the bell.
	float diamonds = (0.85 + 0.15 * sin(x * 70.0 - t * 4.0)) ;
	// Sparks: points that stream out along the plume.
	vec2 cell = vec2((x - t * 1.6) * 34.0, y * 5.0 + s);
	vec2 ci = floor(cell);
	float spark = step(0.93, h21(ci + s)) * smoothstep(0.35, 0.0, length(fract(cell) - 0.5)) * (1.0 - x) * step(across, 1.0);
	vec3 drive = mix(vec3(0.45, 0.68, 1.0), vec3(0.75, 0.55, 1.0), COLOR.b);
	vec3 col = vec3(1.0, 0.97, 0.92) * core * 1.4 * diamonds + drive * body * 0.8 + vec3(0.9, 0.95, 1.0) * spark * 0.8;
	COLOR = vec4(col * strength * boost, 1.0);
}
