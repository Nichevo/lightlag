// A planet or moon drawn as a lit sphere with a noise-made surface. One material per body; the
// PlanetSprite node draws a square from -size to +size and sets the uniforms.
shader_type canvas_item;

uniform vec4 base_color : source_color = vec4(0.5, 0.5, 0.5, 1.0);
uniform vec4 atmo_color : source_color = vec4(0.0);   // alpha 0: no atmosphere
uniform vec2 light_dir = vec2(1.0, 0.0);              // towards the star, on screen
uniform float radius_px = 10.0;                       // the body's radius on screen
uniform int kind = 0;   // 0 rocky, 1 terran, 2 desert, 3 cloudy, 4 gas, 5 ice giant, 6 ice, 7 volcanic, 8 hazy
uniform float seed = 0.0;
uniform float spin = 0.0;                             // turns slowly with game time

varying vec2 local;

void vertex() {
	local = VERTEX;
}

float hash(vec3 p) {
	p = fract(p * 0.3183099 + vec3(0.11, 0.17, 0.13) + seed);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float noise(vec3 x) {
	vec3 i = floor(x);
	vec3 f = fract(x);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(mix(hash(i + vec3(0, 0, 0)), hash(i + vec3(1, 0, 0)), f.x),
	               mix(hash(i + vec3(0, 1, 0)), hash(i + vec3(1, 1, 0)), f.x), f.y),
	           mix(mix(hash(i + vec3(0, 0, 1)), hash(i + vec3(1, 0, 1)), f.x),
	               mix(hash(i + vec3(0, 1, 1)), hash(i + vec3(1, 1, 1)), f.x), f.y), f.z);
}

float fbm(vec3 p, int octaves) {
	float v = 0.0;
	float a = 0.5;
	for (int i = 0; i < 6; i++) {
		if (i >= octaves) break;
		v += a * noise(p);
		p = p * 2.03 + vec3(1.7, 9.2, 3.1);
		a *= 0.5;
	}
	return v;
}

// Distance to the nearest of a scatter of points: craters.
float craters(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	float best = 1.0;
	for (int x = -1; x <= 1; x++)
	for (int y = -1; y <= 1; y++)
	for (int z = -1; z <= 1; z++) {
		vec3 g = vec3(float(x), float(y), float(z));
		vec3 o = vec3(hash(i + g), hash(i + g + 31.0), hash(i + g + 67.0));
		best = min(best, length(g + o - f));
	}
	return best;
}

vec3 surface(vec3 n, float detail) {
	vec3 base = base_color.rgb;
	int oct = int(clamp(detail, 1.0, 6.0));
	vec3 p = n * 2.2 + seed * 7.0;
	float lat = n.y;
	if (kind == 4 || kind == 5) {
		// Gas and ice giants: bands of cloud along the latitude, warped by turbulence.
		float contrast = kind == 4 ? 1.0 : 0.45;
		float warp = fbm(vec3(n.x * 3.0, n.y * 12.0, n.z * 3.0), oct) - 0.5;
		float bands = sin((lat + warp * 0.12) * 22.0 + seed * 3.0) * 0.5 + 0.5;
		float fine = sin((lat + warp * 0.05) * 61.0) * 0.5 + 0.5;
		vec3 light = base * 1.25 + vec3(0.06);
		vec3 dark = base * 0.68;
		vec3 c = mix(dark, light, mix(0.5, bands, contrast));
		c = mix(c, c * (0.88 + 0.24 * fine), contrast);
		if (kind == 4) {
			// A big storm in the southern belt.
			vec3 spot = normalize(vec3(0.5, -0.32, 0.8));
			float s = smoothstep(0.24, 0.12, length((n - spot) * vec3(0.6, 1.4, 0.6)));
			c = mix(c, base * vec3(1.15, 0.72, 0.55), s * 0.8);
		}
		return c;
	}
	if (kind == 3 || kind == 8) {
		// Thick cloud: soft swirls with little contrast.
		float s = fbm(vec3(n.x * 2.0, n.y * 5.0, n.z * 2.0) + seed, oct);
		return base * (0.82 + 0.36 * s);
	}
	float h = fbm(p, oct);
	if (kind == 1) {
		// Terran: sea, land, ice caps and cloud.
		vec3 sea = base * 0.85;
		vec3 land = mix(vec3(0.28, 0.42, 0.22), vec3(0.55, 0.47, 0.32), smoothstep(0.5, 0.7, fbm(p * 2.0, oct)));
		vec3 c = h > 0.52 ? land : sea * (0.8 + 0.4 * h);
		c = mix(c, vec3(0.93, 0.95, 0.98), smoothstep(0.78, 0.86, abs(lat) + h * 0.08));
		float cloud = smoothstep(0.55, 0.75, fbm(vec3(n.x * 3.0, n.y * 6.0, n.z * 3.0) + 5.0 + spin * 0.3, oct));
		return mix(c, vec3(0.97), cloud * 0.8);
	}
	vec3 c = base * (0.75 + 0.5 * h);
	if (kind == 2) {
		// Desert: rusty plains with dark highlands.
		c = mix(c, base * 0.55, smoothstep(0.55, 0.7, fbm(p * 1.7 + 3.0, oct)));
		c = mix(c, vec3(0.92, 0.9, 0.88), smoothstep(0.9, 0.95, abs(lat)));
	} else if (kind == 6) {
		// Ice: pale, with darker cracks.
		float crack = abs(fbm(p * 2.5, oct) - 0.5);
		c = mix(c * 1.1, base * 0.55, smoothstep(0.03, 0.0, crack));
	} else if (kind == 7) {
		// Volcanic: sulphur plains with dark calderas.
		float spots = smoothstep(0.3, 0.2, craters(p * 2.5));
		c = mix(c, vec3(0.25, 0.15, 0.1), spots * 0.8);
	}
	if (kind == 0 || kind == 6 || kind == 2) {
		// Craters: dark floors, bright rims.
		float d = craters(p * 3.0);
		c *= 1.0 - 0.25 * smoothstep(0.32, 0.18, d) * step(0.6, hash(floor(p * 3.0)));
		c *= 1.0 + 0.15 * smoothstep(0.28, 0.33, d) * smoothstep(0.38, 0.33, d);
	}
	return c;
}

void fragment() {
	vec2 q = local / radius_px;          // 1.0 at the surface
	float d = length(q);
	float aa = 1.5 / max(radius_px, 1.0);
	vec3 L = normalize(vec3(light_dir, 0.0));

	vec4 col = vec4(0.0);
	if (d < 1.0) {
		float z = sqrt(max(0.0, 1.0 - d * d));
		vec3 n = vec3(q.x, q.y, z);
		// Turn the sphere slowly about its axis.
		float c = cos(spin), s = sin(spin);
		vec3 m = vec3(n.x * c + n.z * s, n.y, -n.x * s + n.z * c);
		float detail = log2(max(radius_px, 1.0)) - 1.0;
		vec3 albedo = radius_px < 4.0 ? base_color.rgb : surface(m, detail);
		float lit = max(dot(n, L), 0.0);
		float shade = 0.05 + 0.95 * smoothstep(-0.05, 0.25, lit) * (0.55 + 0.45 * lit);
		shade *= mix(0.75, 1.0, z);  // darker towards the limb
		vec3 rgb = albedo * shade;
		if (atmo_color.a > 0.0) {
			float rim = pow(1.0 - z, 3.0) * smoothstep(-0.2, 0.3, dot(vec3(q, 0.0), L));
			rgb += atmo_color.rgb * rim * 0.6;
		}
		col = vec4(rgb, smoothstep(1.0, 1.0 - aa, d));
	}
	if (atmo_color.a > 0.0 && d >= 1.0 - aa) {
		// A thin glow beyond the surface, brightest on the day side.
		float thick = max(0.06, 2.0 / max(radius_px, 1.0));
		float glow = smoothstep(1.0 + thick, 1.0, d) * smoothstep(-0.4, 0.4, dot(normalize(q), light_dir));
		vec4 halo = vec4(atmo_color.rgb, glow * 0.55 * atmo_color.a);
		col = vec4(mix(halo.rgb, col.rgb, col.a), max(col.a, halo.a));
	}
	COLOR = col;
}
