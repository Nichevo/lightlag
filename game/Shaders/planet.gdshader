// A planet or moon drawn as a lit sphere with a noise-made surface. One material per body; the
// PlanetSprite node draws a square from -size to +size and sets the uniforms.
// quality 0 (Low) is the original look. quality 1 (Medium and High) adds a moving cloud layer, city
// lights on the night side of settled worlds, a softer and wider atmosphere, rings on the giants,
// lumpy shapes for small rocks, and the darkening of a moon in its planet's shadow.
shader_type canvas_item;

uniform vec4 base_color : source_color = vec4(0.5, 0.5, 0.5, 1.0);
uniform vec4 atmo_color : source_color = vec4(0.0);   // alpha 0: no atmosphere
uniform vec2 light_dir = vec2(1.0, 0.0);              // towards the star, on screen
uniform float radius_px = 10.0;                       // the body's radius on screen
uniform int kind = 0;   // 0 rocky, 1 terran, 2 desert, 3 cloudy, 4 gas, 5 ice giant, 6 ice, 7 volcanic, 8 hazy, 9 small rock
uniform float seed = 0.0;
uniform float spin = 0.0;                             // turns slowly with game time

uniform int quality = 0;
uniform float cloud_time = 0.0;      // game time for the clouds, which move faster than the ground
uniform float clouds = 0.0;          // cloud cover 0..1
uniform float city = 0.0;            // how bright the night-side lights are (settled worlds)
uniform float eclipse = 0.0;         // 1: wholly in a planet's shadow
uniform float rings = 0.0;           // 1: has rings
uniform vec4 ring_color : source_color = vec4(0.8, 0.75, 0.65, 1.0);
uniform float ring_tilt = 0.45;      // how far the ring plane leans away from the viewer (radians)
uniform float ring_angle = 0.3;      // the rings' turn on screen (radians)

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

vec3 rot_y(vec3 n, float a) {
	float c = cos(a), s = sin(a);
	return vec3(n.x * c + n.z * s, n.y, -n.x * s + n.z * c);
}

vec3 surface(vec3 n, float detail) {
	vec3 base = base_color.rgb;
	int oct = int(clamp(detail, 1.0, 6.0));
	vec3 p = n * 2.2 + seed * 7.0;
	float lat = n.y;
	if (kind == 4 || kind == 5) {
		// Gas and ice giants: bands of cloud along the latitude, warped by turbulence.
		float contrast = kind == 4 ? 1.0 : 0.45;
		vec3 w = n;
		if (quality > 0) {
			// The bands flow at different speeds with latitude.
			w = rot_y(n, cloud_time * 0.02 * sin(lat * 9.0 + seed));
		}
		float warp = fbm(vec3(w.x * 3.0, w.y * 12.0, w.z * 3.0), oct) - 0.5;
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
		// Terran: sea, land, ice caps and (on Low) cloud. The higher settings draw the cloud as its own layer.
		vec3 sea = base * 0.85;
		vec3 land = mix(vec3(0.28, 0.42, 0.22), vec3(0.55, 0.47, 0.32), smoothstep(0.5, 0.7, fbm(p * 2.0, oct)));
		vec3 c = h > 0.52 ? land : sea * (0.8 + 0.4 * h);
		c = mix(c, vec3(0.93, 0.95, 0.98), smoothstep(0.78, 0.86, abs(lat) + h * 0.08));
		if (quality > 0) return c;
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
	if (kind == 0 || kind == 6 || kind == 2 || kind == 9) {
		// Craters: dark floors, bright rims.
		float d = craters(p * 3.0);
		c *= 1.0 - 0.25 * smoothstep(0.32, 0.18, d) * step(0.6, hash(floor(p * 3.0)));
		c *= 1.0 + 0.15 * smoothstep(0.28, 0.33, d) * smoothstep(0.38, 0.33, d);
	}
	return c;
}

// Asteroids and tiny moons are potato-shaped: stretched along one axis, with lumps round the edge.
// 1.0 at most, so the rock always stays inside its circle on the map.
float rock_shape(vec2 q, float d) {
	if (kind != 9) return 1.0;
	vec2 dir = d > 0.0 ? q / d : vec2(1.0, 0.0);
	float a = atan(dir.y, dir.x) + spin * 0.3;
	float lumps = 0.62 + 0.38 * fbm(vec3(cos(a) * 1.4, sin(a) * 1.4, seed), 3);
	float stretch = mix(1.0, 0.7, 0.5 + 0.5 * cos(2.0 * (a - seed * 1.7)));
	return clamp(lumps * stretch / 0.95, 0.4, 1.0);
}

// The original look (Low).
vec4 plain(vec2 q, float d, float aa, vec3 L) {
	vec4 col = vec4(0.0);
	float shape = rock_shape(q, d);
	q /= shape;
	d /= shape;
	if (d < 1.0) {
		float z = sqrt(max(0.0, 1.0 - d * d));
		vec3 n = vec3(q.x, q.y, z);
		vec3 m = rot_y(n, spin);
		float detail = log2(max(radius_px, 1.0)) - 1.0;
		vec3 albedo = radius_px < 4.0 ? base_color.rgb : surface(m, detail);
		float lit = max(dot(n, L), 0.0);
		float shade = 0.05 + 0.95 * smoothstep(-0.05, 0.25, lit) * (0.55 + 0.45 * lit);
		shade *= mix(0.75, 1.0, z);
		vec3 rgb = albedo * shade;
		if (atmo_color.a > 0.0) {
			float rim = pow(1.0 - z, 3.0) * smoothstep(-0.2, 0.3, dot(vec3(q, 0.0), L));
			rgb += atmo_color.rgb * rim * 0.6;
		}
		col = vec4(rgb, smoothstep(1.0, 1.0 - aa, d));
	}
	if (atmo_color.a > 0.0 && d >= 1.0 - aa) {
		float thick = max(0.06, 2.0 / max(radius_px, 1.0));
		float glow = smoothstep(1.0 + thick, 1.0, d) * smoothstep(-0.4, 0.4, dot(normalize(q), light_dir));
		vec4 halo = vec4(atmo_color.rgb, glow * 0.55 * atmo_color.a);
		col = vec4(mix(halo.rgb, col.rgb, col.a), max(col.a, halo.a));
	}
	return col;
}

// Rings: a flat disc around the equator, leaning away by ring_tilt. Returns colour and cover, and
// the ring's depth (towards the viewer) so the planet can hide the far half.
vec4 ring_at(vec2 q, vec3 L, out float depth) {
	depth = -10.0;
	float ca = cos(ring_angle), sa = sin(ring_angle);
	vec2 r = vec2(q.x * ca + q.y * sa, -q.x * sa + q.y * ca);
	float ct = cos(ring_tilt);
	float yy = r.y / ct;
	float rr = length(vec2(r.x, yy));
	const float inner = 1.35;
	const float outer = 2.15;
	if (rr < inner || rr > outer) return vec4(0.0);
	depth = yy * sin(ring_tilt);
	// Ringlets of uneven density, one clear gap, a dense middle band and a faint outer edge.
	float t = (rr - inner) / (outer - inner);
	float ringlets = fbm(vec3(t * 40.0, seed, 0.5), 4);
	float density = 0.25 + 0.75 * smoothstep(0.25, 0.7, ringlets);
	density *= mix(0.45, 1.0, smoothstep(0.1, 0.35, t)) * mix(1.0, 0.35, smoothstep(0.7, 0.95, t));
	density *= 1.0 - smoothstep(0.012, 0.0, abs(t - 0.62) - 0.02);
	// The planet's shadow falls across the ring on the night side.
	vec3 P = vec3(q, depth);
	float along = dot(P, L);
	float off = length(P - L * along);
	float lit = along < 0.0 ? smoothstep(0.95, 1.05, off) : 1.0;
	float aa = 1.5 / max(radius_px, 1.0);
	float edge = smoothstep(inner, inner + aa, rr) * smoothstep(outer, outer - aa, rr);
	vec3 col = ring_color.rgb * (0.8 + 0.4 * ringlets) * (0.12 + 0.88 * lit);
	return vec4(col, density * 0.6 * edge);
}

vec4 fancy(vec2 q, float d, float aa, vec3 L) {
	// Small rocks are lumpy: push the outline in and out with the angle.
	float shape = rock_shape(q, d);
	float ds = d / shape;
	vec4 col = vec4(0.0);
	float zp = -10.0;
	if (ds < 1.0) {
		float z = sqrt(max(0.0, 1.0 - ds * ds));
		zp = z * shape;
		vec3 n = vec3(q.x / shape, q.y / shape, z);
		if (kind == 9) {
			// Bumpy normals so the lumps catch the light.
			vec3 bump = vec3(noise(n * 6.0 + seed) - 0.5, noise(n * 6.0 + seed + 9.0) - 0.5, 0.0);
			n = normalize(n + bump * 0.5);
		}
		vec3 m = rot_y(n, spin);
		float detail = log2(max(radius_px, 1.0)) - 1.0;
		vec3 albedo = radius_px < 4.0 ? base_color.rgb : surface(m, detail);
		float lit = dot(n, L);
		float day = smoothstep(-0.05, 0.25, lit);
		float shade = 0.04 + 0.96 * day * (0.55 + 0.45 * max(lit, 0.0));
		shade *= mix(0.75, 1.0, z);
		// A moon in its planet's shadow goes dark.
		shade *= 1.0 - 0.9 * eclipse;
		vec3 rgb = albedo * shade;

		// Cloud layer: drifts faster than the ground and casts a faint shadow on it.
		if (clouds > 0.0 && radius_px >= 4.0) {
			vec3 cm = rot_y(n, spin * 1.35 + cloud_time * 0.004);
			vec3 cp = vec3(cm.x * 3.0, cm.y * 6.0, cm.z * 3.0) + 5.0;
			cp += vec3(fbm(cm * 4.0 + seed, 3), 0.0, fbm(cm * 4.0 + seed + 3.0, 3)) * 0.8;
			float c = smoothstep(0.52, 0.76, fbm(cp, int(clamp(detail, 2.0, 6.0))));
			float shadowc = smoothstep(0.52, 0.76, fbm(cp + vec3(L.x, L.y, 0.0) * 0.08, 3));
			rgb *= 1.0 - 0.25 * shadowc * clouds;
			rgb = mix(rgb, vec3(0.96) * shade * (1.0 - 0.15 * eclipse), c * clouds * 0.85);
			albedo = mix(albedo, vec3(0.96), c * clouds);
		}

		// City lights: warm specks in clusters on the night side of settled worlds, dimmed under cloud.
		if (city > 0.0 && radius_px >= 6.0) {
			float night = smoothstep(0.08, -0.12, lit);
			vec3 s = m * 2.2 + seed * 7.0;
			float land = kind == 1 ? step(0.52, fbm(s, 5)) : 1.0;
			float cluster = smoothstep(0.55, 0.8, fbm(m * 7.0 + seed * 3.0, 4));
			float specks = smoothstep(0.7, 0.95, noise(m * 90.0 + seed));
			float light = (cluster * 0.6 + specks * cluster * 1.4) * land * night * city;
			float under = clouds > 0.0 ? 1.0 - 0.6 * clouds * smoothstep(0.85, 1.0, length(albedo) / 1.66) : 1.0;
			rgb += vec3(1.0, 0.72, 0.38) * light * under * 0.9;
		}

		if (atmo_color.a > 0.0) {
			// Inner rim: the air seen edge-on, lit on the day side, reddening at the terminator.
			float rim = pow(1.0 - z, 2.4) * smoothstep(-0.3, 0.35, dot(vec3(q, 0.0), L));
			vec3 dusk = mix(atmo_color.rgb, vec3(1.0, 0.55, 0.35), smoothstep(0.3, -0.05, lit) * 0.6);
			rgb += dusk * rim * 0.7;
		}
		col = vec4(rgb, smoothstep(1.0, 1.0 - aa, ds));
	}
	if (atmo_color.a > 0.0 && ds >= 1.0 - aa) {
		// A soft glow beyond the edge, brightest on the day side and wrapping a little round the night side.
		float thick = max(0.12, 4.0 / max(radius_px, 1.0));
		float out_d = (ds - 1.0) / thick;
		float glow = exp(-max(out_d, 0.0) * 3.2) * smoothstep(1.0, 0.0, out_d);
		float side = smoothstep(-0.6, 0.5, dot(normalize(q), light_dir));
		vec4 halo = vec4(atmo_color.rgb, glow * (0.12 + 0.55 * side) * atmo_color.a * (1.0 - 0.8 * eclipse));
		col = vec4(mix(halo.rgb, col.rgb, col.a), max(col.a, halo.a));
	}
	if (rings > 0.0) {
		float depth;
		vec4 r = ring_at(q, L, depth);
		if (r.a > 0.0 && (ds >= 1.0 || depth > zp)) {
			// In front of the planet (or clear of it): laid over the top.
			float a = r.a + col.a * (1.0 - r.a);
			col = vec4((r.rgb * r.a + col.rgb * col.a * (1.0 - r.a)) / max(a, 1e-4), a);
		}
	}
	return col;
}

void fragment() {
	vec2 q = local / radius_px;          // 1.0 at the surface
	float d = length(q);
	float aa = 1.5 / max(radius_px, 1.0);
	vec3 L = normalize(vec3(light_dir, 0.0));
	COLOR = quality == 0 ? plain(q, d, aa, L) : fancy(q, d, aa, L);
}
