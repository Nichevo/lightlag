"""
Raster painter kit for the idea pictures (see paint_ideas.py): value-noise textures, nebula skies, lit planets
with air glow, shaded metal hulls, heat and engine glow, a parametric side-on ship, weapons effects, buildings,
stations, and a filmic finish with bloom. Everything is drawn in float RGB with numpy and saved with PIL.
All randomness comes from the seed given to Canvas, so a picture is the same every run.
"""
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

W, H = 1120, 384
SS = 3  # supersampling for masks


def rng(seed):
    return np.random.default_rng(seed)


def noise(h, w, scale, seed, octaves=5, persist=.5):
    """fBm value noise in [0,1] built from upscaled random grids."""
    r = rng(seed)
    out = np.zeros((h, w), np.float32)
    amp, tot = 1.0, 0.0
    s = scale
    for _ in range(octaves):
        gh, gw = max(2, int(h / s) + 2), max(2, int(w / s) + 2)
        g = (r.random((gh, gw)) * 255).astype(np.uint8)
        im = Image.fromarray(g).resize((int(gw * s), int(gh * s)), Image.BICUBIC)
        a = np.asarray(im, np.float32)[:h, :w] / 255
        if a.shape != (h, w):
            a = np.pad(a, ((0, h - a.shape[0]), (0, w - a.shape[1])), mode="edge")
        out += a * amp
        tot += amp
        amp *= persist
        s /= 2
        if s < 1.5:
            break
    out /= tot
    lo, hi = np.percentile(out, 1), np.percentile(out, 99)
    return np.clip((out - lo) / (hi - lo + 1e-6), 0, 1)


def ss(x, a, b):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def col(h):
    h = h.lstrip("#")
    return np.array([int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)], np.float32)


def grid():
    y, x = np.mgrid[0:H, 0:W].astype(np.float32)
    return x, y


class Canvas:
    def __init__(self, seed):
        self.seed = seed
        self.img = np.zeros((H, W, 3), np.float32)
        self.glow = np.zeros((H, W, 3), np.float32)  # emissive, goes through bloom
        self.x, self.y = grid()

    # ---- blend helpers
    def over(self, rgb, a):
        a = a[..., None] if a.ndim == 2 else a
        self.img = self.img * (1 - a) + rgb * a

    def add(self, rgb, a, bloom=True):
        a = a[..., None] if a.ndim == 2 else a
        self.img += rgb * a
        if bloom:
            self.glow += rgb * a

    def mask(self, draw_fn):
        im = Image.new("L", (W * SS, H * SS), 0)
        d = ImageDraw.Draw(im)
        draw_fn(d, SS)
        im = im.resize((W, H), Image.LANCZOS)
        return np.asarray(im, np.float32) / 255

    # ---- sky
    def sky(self, tint=("#0a1020", "#03050a"), neb=("#3a2a6a", "#1a4a6a"), neb_amt=.55, stars=1400, dust=True):
        x, y = self.x, self.y
        t = y / H
        base = col(tint[0]) * (1 - t[..., None]) + col(tint[1]) * t[..., None]
        self.img = base.copy()
        n1 = noise(H, W, 260, self.seed + 1, 6)
        n2 = noise(H, W, 180, self.seed + 2, 6)
        m = ss(n1, .45, .95) * neb_amt
        neb_c = col(neb[0]) * (1 - n2[..., None]) + col(neb[1]) * n2[..., None]
        self.img += neb_c * m[..., None] * .55
        if dust:
            d = ss(noise(H, W, 120, self.seed + 3, 6), .55, .9) * m
            self.img *= (1 - .55 * d)[..., None]
        r = rng(self.seed + 9)
        n = stars
        sx, sy = r.random(n) * W, r.random(n) * H
        mag = r.random(n) ** 6
        temp = r.random(n)
        star = np.zeros((H, W, 3), np.float32)
        tints = [col("#9fc4ff"), col("#ffffff"), col("#fff1d6"), col("#ffc89a")]
        for i in range(n):
            xi, yi = int(sx[i]), int(sy[i])
            c = tints[min(3, int(temp[i] * 4))]
            star[yi % H, xi % W] += c * (.15 + 1.6 * mag[i])
        soft = np.asarray(Image.fromarray(np.clip(star * 255, 0, 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(.7)), np.float32) / 255
        self.img += star * .6 + soft * 1.4
        # a few bright stars with halo
        for i in range(6):
            bx, by = r.random() * W, r.random() * H
            d2 = (x - bx) ** 2 + (y - by) ** 2
            self.add(col("#cfe0ff"), np.exp(-d2 / 3) * .9 + np.exp(-d2 / 60) * .12)

    # ---- light sources
    def sun(self, cx, cy, r, color="#fff2d0", strength=1.0):
        d = np.sqrt((self.x - cx) ** 2 + (self.y - cy) ** 2)
        core = ss(r - d, -1.5, 1.5)
        halo = np.exp(-(d / (r * 3)) ** 2) * .9 + np.exp(-d / (r * 9)) * .5
        self.add(col(color), (core * 2 + halo) * strength)

    def glow_dot(self, cx, cy, r, color, s=1.0):
        d2 = (self.x - cx) ** 2 + (self.y - cy) ** 2
        self.add(col(color), (np.exp(-d2 / (r * r)) + .25 * np.exp(-d2 / (r * r * 12))) * s)

    # ---- planets
    def planet(self, cx, cy, r, kind="earth", light=(.8, -.35, .5), rot=0.0, atmo="#7fb8ff", atmo_w=.05,
               night_lights=False, terra=None, rings=None, seed=None):
        seed = self.seed + 50 if seed is None else seed
        x, y = self.x, self.y
        dx, dy = (x - cx) / r, (y - cy) / r
        d2 = dx * dx + dy * dy
        inside = d2 < 1
        dz = np.sqrt(np.clip(1 - d2, 0, 1))
        L = np.array(light, np.float32); L /= np.linalg.norm(L)
        lam = dx * L[0] + dy * L[1] + dz * L[2]
        lit = ss(lam, -.08, .35) * (0.25 + 0.75 * np.clip(lam, 0, 1) ** .7)
        lon = np.arctan2(dx, dz) + rot
        lat = np.arcsin(np.clip(dy, -1, 1))
        TW, TH = 1024, 512
        u = ((lon / (2 * np.pi) + .5) % 1 * (TW - 1)).astype(int)
        v = ((lat / np.pi + .5) * (TH - 1)).clip(0, TH - 1).astype(int)
        if rings:
            self.rings(cx, cy, r, *rings, front=False)
        tex = self._planet_tex(kind, TW, TH, seed, terra)
        surf = tex[v, u]
        # clouds
        if kind in ("earth", "terra"):
            cl = noise(TH, TW // 3, 30, seed + 7, 6)
            cl = np.asarray(Image.fromarray((cl * 255).astype(np.uint8)).resize((TW, TH), Image.BICUBIC), np.float32) / 255
            cl = ss(cl, .55, .85)
            if terra is not None:
                uu = np.linspace(0, 1, TW)[None, :]
                cl = cl * ss(terra + .02 - uu, 0, .06) * .8 + cl * .2
            cl = cl[v, u]
            surf = surf * (1 - cl[..., None] * .85) + cl[..., None] * .95
        shade = surf * lit[..., None]
        # limb darkening
        shade *= (.55 + .45 * dz)[..., None]
        if night_lights:
            nl = ss(noise(TH, TW, 12, seed + 11, 3), .72, .9)[v, u] * ss(-lam, .02, .25)
            land = (tex[v, u].sum(-1) > .5)
            self.add(col("#ffb860"), nl * land * inside * .5, bloom=True)
        a = ss(1 - np.sqrt(d2), -.004, .004)
        self.over(shade, a * inside)
        # atmosphere rim
        dist = np.sqrt(d2)
        rim_in = np.exp(-((1 - dist) / atmo_w) ** 2) * inside
        rim_out = np.exp(-((dist - 1) / (atmo_w * .9)) ** 2) * (~inside)
        litr = ss(dx * L[0] + dy * L[1], -.4, .5)
        if atmo:
            self.add(col(atmo), (rim_in * .7 + rim_out * .9) * litr)
        if rings:
            self.rings(cx, cy, r, *rings, front=True, planet_mask=a * inside)

    def _planet_tex(self, kind, TW, TH, seed, terra):
        n = noise(TH, TW, 120, seed, 7)
        n2 = noise(TH, TW, 40, seed + 3, 5)
        yy = np.linspace(-1, 1, TH)[:, None]
        if kind == "earth":
            n = noise(TH, TW, 70, seed, 8, .55)
            land = ss(n, .55, .565)
            shelf = ss(n, .45, .55) * (1 - land)
            ocean = col("#08223f") * (1 - shelf[..., None]) + col("#1f6a90") * shelf[..., None]
            n3 = noise(TH, TW, 25, seed + 4, 5)
            dry = ss(n2 + (1 - np.abs(yy)) * .3 - .2, .45, .75)
            green = col("#2f4a24") * (1 - dry[..., None]) + col("#9a8358") * dry[..., None]
            green = green * (.75 + .5 * n3[..., None])
            t = ocean * (1 - land[..., None]) + green * land[..., None]
            ice = ss(np.abs(yy), .78, .86)
            t = t * (1 - ice[..., None]) + ice[..., None] * .92
            return t
        if kind == "rust":
            t = col("#5a2414") * (1 - n[..., None]) + col("#c0683a") * n[..., None]
            t *= (.8 + .4 * n2[..., None])
            crater = ss(noise(TH, TW, 18, seed + 5, 3), .8, .86)
            return t * (1 - crater[..., None] * .3)
        if kind == "terra":
            # terra = (edge longitude 0..1): left part rust, right part green & sea
            u = np.linspace(0, 1, TW)[None, :]
            front = 1 - ss(u + (n2 - .5) * .14, terra - .02, terra + .02)
            rust = self._planet_tex("rust", TW, TH, seed, None)
            earth = self._planet_tex("earth", TW, TH, seed, None)
            return rust * (1 - front[..., None]) + earth * front[..., None]
        if kind == "gas":
            w = noise(TH, TW // 6, 20, seed + 8, 6)
            w = np.asarray(Image.fromarray((w * 255).astype(np.uint8)).resize((TW, TH), Image.BICUBIC), np.float32) / 255
            b = np.sin(yy * 34 + w * 2.2 + np.sin(yy * 9) * 2) * .5 + .5
            b2 = np.sin(yy * 90 + w * 4) * .5 + .5
            t = col("#7a4a28") * (1 - b[..., None]) + col("#ecd2a2") * b[..., None]
            t = t * (.85 + .2 * b2[..., None])
            return t
        if kind == "moon":
            t = col("#4a4a4e") * (1 - n[..., None]) + col("#a8a6a2") * n[..., None]
            return t
        if kind == "ice":
            return col("#6f8aa6") * (1 - n[..., None]) + col("#e6f0fa") * n[..., None]
        raise ValueError(kind)

    def rings(self, cx, cy, r, tilt=.22, inner=1.4, outer=2.2, color="#c8b89a", front=True, planet_mask=None):
        dx, dy = (self.x - cx) / r, (self.y - cy) / (r * tilt)
        d = np.sqrt(dx * dx + dy * dy)
        k = np.clip((d - inner) / (outer - inner), 0, 1)
        bands = .55 + .45 * np.sin(k * 40) * np.sin(k * 13 + 1)
        band = ss(d, inner, inner + .03) * ss(outer - d, 0, .03) * bands
        band *= (1 - .7 * ss(k, .55, .6) * ss(.66 - k, 0, .05))  # a gap
        sel = (self.y > cy) if front else (self.y <= cy)
        a = band * .75 * sel
        if not front and planet_mask is None:
            pass
        self.over(col(color) * (.6 + .4 * bands[..., None]), a)

    def ground(self, horizon, kind="#3b3329", curve=0.0, seed=0, haze="#6a8ab0", top="#7a6a58"):
        """A planet surface horizon (curved) along the bottom: twilight glow above, lit rough ground below."""
        x, y = self.x, self.y
        hy = horizon + curve * ((x - W / 2) / (W / 2)) ** 2
        # twilight glow in the sky just above the horizon
        self.add(col(haze), np.exp(-np.clip(hy - y, 0, None) / 60) * (y < hy) * .22, bloom=False)
        a = ss(y - hy, -.8, .8)
        n = noise(H, W, 60, self.seed + 31 + seed, 6)
        n2 = noise(H, W, 8, self.seed + 33 + seed, 4)
        depth = np.clip((y - hy) / (H - horizon + 1), 0, 1)
        c = col(top) * (1 - depth[..., None]) + col(kind) * depth[..., None]
        relief = .55 + .6 * n + .35 * (n2 - .5)
        c = c * relief[..., None] * (1 - .55 * depth[..., None])
        self.over(c, a)
        # boulders: small dark lumps with lit tops
        r = rng(self.seed + 40 + seed)
        for i in range(26):
            bx = r.random() * W
            by = horizon + 8 + (r.random() ** 2) * (H - horizon)
            br = (2 + 10 * ((by - horizon) / (H - horizon + 1))) * r.uniform(.6, 1.4)
            m = self.mask(lambda d, s, bx=bx, by=by, br=br: d.ellipse([(bx - br * 1.5) * s, (by - br) * s, (bx + br * 1.5) * s, (by + br * .4) * s], fill=255))
            v = np.clip((y - (by - br)) / (br * 1.4), 0, 1)
            self.over(col(top) * (1.1 - .9 * v[..., None]) * .8, m)
        self.add(col(haze), np.exp(-((y - hy) / 3) ** 2) * .3)

    # ---- hulls (side-on cylinder shading)
    def hull(self, pts, y0, y1, base="#9aa6b8", panel=34, seed=0, light_top=True, grime=.35, spec=.6):
        m = self.mask(lambda d, s: d.polygon([(px * s, py * s) for px, py in pts], fill=255))
        v = np.clip((self.y - y0) / max(1, (y1 - y0)), 0, 1) * 2 - 1
        nz = np.sqrt(np.clip(1 - v * v, 0, 1))
        lam = np.clip(-v * .55 + nz * .75, 0, 1) if light_top else np.clip(v * .55 + nz * .75, 0, 1)
        n = noise(H, W, 14, self.seed + 90 + seed, 4)
        c = col(base) * (.18 + .9 * lam[..., None]) * (1 - grime * n[..., None] * .6)
        lines = ((self.x.astype(int) % panel) < 1.2) * .35 + ((self.y.astype(int) - int(y0)) % max(6, int((y1 - y0) / 3)) < 1) * .18
        c *= (1 - lines[..., None])
        sp = np.exp(-((v + .45) / .12) ** 2) * spec
        c += sp[..., None] * col("#ffffff") * .5
        self.over(c, m)
        # edge outline darkening
        edge = m - np.asarray(Image.fromarray((m * 255).astype(np.uint8)).filter(ImageFilter.MinFilter(3)), np.float32) / 255
        self.over(col("#0a0d12"), edge * .5)
        top = np.clip(m - np.roll(m, 2, axis=0), 0, 1) if light_top else np.clip(m - np.roll(m, -2, axis=0), 0, 1)
        self.add(col("#e8f0ff"), top * .35, bloom=False)
        return m

    def poly(self, pts, color, alpha=1.0, blur=0):
        m = self.mask(lambda d, s: d.polygon([(px * s, py * s) for px, py in pts], fill=255))
        if blur:
            m = self.blur(m, blur)
        self.over(col(color), m * alpha)
        return m

    def ellipse(self, box, color=None, alpha=1.0, glow=None, s=1.0):
        x0, y0, x1, y1 = box
        m = self.mask(lambda d, k: d.ellipse([x0 * k, y0 * k, x1 * k, y1 * k], fill=255))
        if glow:
            self.add(col(glow), m * s)
        else:
            self.over(col(color), m * alpha)
        return m

    def line(self, p0, p1, width, color, glow=False, s=1.0, alpha=1.0):
        m = self.mask(lambda d, k: d.line([p0[0] * k, p0[1] * k, p1[0] * k, p1[1] * k], fill=255, width=max(1, int(width * k))))
        if glow:
            self.add(col(color), m * s)
        else:
            self.over(col(color), m * alpha)
        return m

    def blur(self, a, r):
        im = Image.fromarray(np.clip(a * 255, 0, 255).astype(np.uint8))
        return np.asarray(im.filter(ImageFilter.GaussianBlur(r)), np.float32) / 255

    def blur3(self, a, r):
        return np.stack([self.blur(a[..., i] / max(1e-6, a[..., i].max()), r) * a[..., i].max() for i in range(3)], -1)

    def radiator(self, x0, y0, x1, y1, heat=.8, root="bottom"):
        m = self.mask(lambda d, s: d.rectangle([x0 * s, y0 * s, x1 * s, y1 * s], fill=255))
        fins = ((self.x.astype(int) - int(x0)) % 4 < 1) * .55
        t = np.clip((self.y - y0) / (y1 - y0 + 1e-6), 0, 1)
        near = t if root == "bottom" else 1 - t
        c = col("#3a2418") * (.7 + .5 * near[..., None]) * (1 - fins[..., None])
        self.over(c, m)
        self.add(col("#ff5a1a") * heat, m * (1 - fins) * (.08 + .3 * near ** 2), bloom=True)
        edge = np.clip(m - np.roll(m, 1, axis=1), 0, 1)
        self.over(col("#7a5a48"), edge * .6)

    def plume(self, x, y, length, h, color="#9fd8ff", s=1.0):
        X, Y = self.x, self.y
        t = np.clip((x - X) / length, 0, 1) * (X <= x)
        width = h * (.5 + 1.2 * t)
        prof = np.exp(-((Y - y) / (width / 2 + 1e-3)) ** 2)
        fall = (1 - t) ** 1.6 * (X > x - length) * (X <= x + 1)
        core = np.exp(-((Y - y) / (h * .18)) ** 2) * np.exp(-((x - X) / (length * .12)) ** 2) * (X <= x + 2)
        self.add(col(color), prof * fall * .8 * s)
        self.add(col("#ffffff"), core * 1.6 * s)

    def lights(self, pts, color="#ffd28a", r=1.6, s=1.0):
        for px, py in pts:
            self.glow_dot(px, py, r, color, s)

    def finish(self, exposure=1.0, bloom=1.0, vignette=.35, grain=.02):
        img = np.nan_to_num(self.img) * exposure
        self.glow = np.nan_to_num(self.glow)
        g = np.clip(self.glow + np.clip(img - .9, 0, None), 0, 4)
        b = np.zeros_like(img)
        for r, wgt in ((4, .5), (14, .35), (40, .25)):
            for i in range(3):
                mx = max(1e-6, float(g[..., i].max()))
                b[..., i] += self.blur(g[..., i] / mx, r) * mx * wgt
        img = img + b * bloom * .6
        d = ((self.x - W / 2) / (W / 2)) ** 2 + ((self.y - H / 2) / (H / 2)) ** 2
        img *= (1 - vignette * ss(d, .4, 2.0))[..., None]
        img = 1 - np.exp(-img * 1.15)  # filmic shoulder
        img += (rng(self.seed + 999).random((H, W, 1)) - .5) * grain
        img = np.clip(img, 0, 1) ** (1 / 1.05)
        return Image.fromarray((img * 255).astype(np.uint8))


# ---- Parametric ships ------------------------------------------------------------------------------

SHIPS = {
    # length, thickness, radiator pairs, turrets, bells
    "escort": (230, 26, 1, 2, 1),
    "frigate": (330, 34, 1, 2, 2),
    "picket": (250, 26, 1, 0, 1),
    "cruiser": (450, 48, 2, 3, 2),
    "capital": (640, 66, 3, 5, 3),
    "tanker": (380, 36, 1, 0, 2),
    "freighter": (420, 30, 1, 0, 2),
    "bulk": (560, 30, 1, 0, 3),
    "carrier": (520, 58, 2, 1, 3),
    "monitor": (500, 62, 2, 0, 3),
    "settler": (420, 40, 1, 0, 2),
    "courier": (160, 18, 0, 0, 0),
}


def ship(c, kind, x, y, scale=1.0, plume=None, plume_len=1.0, heat=.6, base="#a9b4c5", lights=True, seed=0,
         tank=None, dark=False):
    """Draws a ship side-on, nose right, stern at x, centre line y. Returns anchor points."""
    L, h, nrad, nturret, nbell = SHIPS[kind]
    L, h = L * scale, h * scale
    r = rng(c.seed + 300 + seed)
    a = {"stern": (x, y), "bow": (x + L, y), "top": [], "turrets": [], "h": h, "L": L}
    hb = base if not dark else "#16191f"
    # engine bells
    bh = h * .62 / max(1, nbell) * (1.2 if nbell == 1 else 1.0)
    for i in range(max(1, nbell)):
        if nbell == 0:
            break
        by = y - h * .31 + (i + .5) * (h * .62 / nbell)
        c.hull([(x - 18 * scale, by - bh * .62), (x + 4, by - bh * .3), (x + 4, by + bh * .3), (x - 18 * scale, by + bh * .62)],
               by - bh * .62, by + bh * .62, base="#5a6270" if not dark else "#121418", panel=40, seed=seed + i)
        if plume:
            c.plume(x - 18 * scale, by, 260 * scale * plume_len, bh * 1.1, plume, 1.0)
    # radiators (behind hull)
    for k in range(nrad):
        rx = x + L * (.22 + k * .16)
        rw = L * .065
        rh = h * (1.0 if kind not in ("courier",) else .7)
        if not dark:
            c.radiator(rx, y - h / 2 - rh, rx + rw, y - h / 2, heat, "bottom")
            c.radiator(rx, y + h / 2, rx + rw, y + h / 2 + rh, heat, "top")
        else:
            c.poly([(rx, y - h / 2 - rh), (rx + rw, y - h / 2 - rh), (rx + rw, y - h / 2), (rx, y - h / 2)], "#0c0e12")
            c.poly([(rx, y + h / 2), (rx + rw, y + h / 2), (rx + rw, y + h / 2 + rh), (rx, y + h / 2 + rh)], "#0c0e12")
    # main hull
    nose = {"escort": .16, "frigate": .14, "picket": .2, "cruiser": .12, "capital": .1, "tanker": .1, "freighter": .08,
            "bulk": .06, "carrier": .12, "monitor": .06, "settler": .1, "courier": .3}[kind]
    if kind == "freighter" or kind == "bulk":
        spine = h * .3
        c.hull([(x, y - spine), (x + L * .92, y - spine), (x + L, y - spine * .4), (x + L, y + spine * .4), (x + L * .92, y + spine), (x, y + spine)],
               y - spine, y + spine, base=hb, panel=int(30 * scale) + 6, seed=seed)
        n = 6 if kind == "freighter" else 10
        cw = L * .7 / n
        cols = ["#7a5a3a", "#3a5a6a", "#6a6a4a", "#5a3a3a", "#4a5a4a", "#6a5a6a"]
        for i in range(n):
            cx0 = x + L * .16 + i * cw
            for sgn in (-1, 1):
                y0, y1 = (y - h * .9, y - spine) if sgn < 0 else (y + spine, y + h * .9)
                c.hull([(cx0 + 2, y0), (cx0 + cw - 2, y0), (cx0 + cw - 2, y1), (cx0 + 2, y1)], y0, y1,
                       base=cols[(i * 3 + (sgn > 0)) % len(cols)], panel=max(5, int(cw / 3)), seed=seed + i, grime=.5)
        c.hull([(x + L * .88, y - h * .55), (x + L * .97, y - h * .4), (x + L * .97, y), (x + L * .88, y)], y - h * .55, y, base=hb, panel=12)
    else:
        pts = [(x, y - h / 2), (x + L * (1 - nose), y - h / 2), (x + L, y - h * .12), (x + L, y + h * .12),
               (x + L * (1 - nose), y + h / 2), (x, y + h / 2)]
        if dark:
            m = c.mask(lambda d, s: d.polygon([(px * s, py * s) for px, py in pts], fill=255))
            c.over(col("#08090c") * (1 + .5 * noise(H, W, 10, c.seed + 5, 3)[..., None]), m)
        else:
            c.hull(pts, y - h / 2, y + h / 2, base=hb, panel=int(34 * scale) + 6, seed=seed)
            c.hull([(x, y - h / 2), (x + L * .15, y - h / 2), (x + L * .15, y + h / 2), (x, y + h / 2)], y - h / 2, y + h / 2,
                   base="#6c7484", panel=int(12 * scale) + 4, seed=seed + 1)
            if kind in ("frigate", "cruiser", "capital", "escort", "settler"):
                bx0 = x + L * (.4 if kind != "escort" else .3)
                c.hull([(bx0, y - h * .36), (bx0 + L * .14, y - h * .36), (bx0 + L * .14, y + h * .36), (bx0, y + h * .36)], y - h * .36, y + h * .36,
                       base="#a8d4ec", panel=int(L * .07), seed=seed + 2, spec=.9, grime=.15)
    if kind == "tanker" or tank:
        n = 3 if kind == "tanker" else 1
        tw = L * .16
        for i in range(n):
            tx = x + L * (.42 + i * .17) if kind == "tanker" else x + L * .45
            tr = h * (.95 if kind == "tanker" else .38)
            c.ellipse((tx, y - tr, tx + tw, y + tr), "#9fd8f5", 1.0)
            v = np.clip((c.y - (y - tr)) / (2 * tr), 0, 1)
            m = c.mask(lambda d, s: d.ellipse([tx * s, (y - tr) * s, (tx + tw) * s, (y + tr) * s], fill=255))
            c.over(col("#d8f0ff") * (1.05 - v[..., None]) + col("#1f4b63") * v[..., None], m)
            c.add(col("#ffffff"), m * np.exp(-((v - .22) / .07) ** 2) * .35, bloom=False)
    if kind == "settler":
        for i in range(5):
            hx = x + L * (.3 + i * .1)
            c.hull([(hx, y - h * .75), (hx + L * .08, y - h * .75), (hx + L * .08, y - h / 2), (hx, y - h / 2)], y - h * .75, y - h / 2, base="#c8ccd2", panel=8)
            c.lights([(hx + L * .04, y - h * .62)], "#ffd28a", 1.2, .5)
    if kind == "picket":
        dx, dy = x + L * .62, y - h / 2 - 34 * scale
        c.line((dx, y - h / 2), (dx, dy + 8 * scale), 2 * scale, "#8a94a4")
        c.ellipse((dx - 30 * scale, dy - 8 * scale, dx + 30 * scale, dy + 10 * scale), "#d8dee8", 1.0)
        c.ellipse((dx - 24 * scale, dy - 3 * scale, dx + 24 * scale, dy + 9 * scale), "#7d8796", 1.0)
        a["dish"] = (dx, dy)
    if kind == "carrier":
        c.poly([(x + L * .5, y + h * .05), (x + L * .82, y + h * .05), (x + L * .82, y + h * .38), (x + L * .5, y + h * .38)], "#0a0c10")
        c.add(col("#ffb860"), c.mask(lambda d, s: d.rectangle([(x + L * .5) * s, (y + h * .3) * s, (x + L * .82) * s, (y + h * .38) * s], fill=255)) * .6)
        a["bay"] = (x + L * .82, y + h * .22)
    if kind == "monitor":
        c.hull([(x + L * .3, y - h * .12), (x + L * 1.18, y - h * .12), (x + L * 1.18, y + h * .12), (x + L * .3, y + h * .12)],
               y - h * .12, y + h * .12, base="#7a8494", panel=20)
        for k in range(8):
            cx0 = x + L * (.4 + k * .1)
            c.hull([(cx0, y - h * .2), (cx0 + 10 * scale, y - h * .2), (cx0 + 10 * scale, y + h * .2), (cx0, y + h * .2)], y - h * .2, y + h * .2, base="#c08a4a", panel=40)
        a["muzzle"] = (x + L * 1.18, y)
    # superstructure + turrets
    if kind not in ("courier", "freighter", "bulk"):
        sx = x + L * .62
        c.hull([(sx, y - h / 2 - h * .22), (sx + L * .14, y - h / 2 - h * .22), (sx + L * .17, y - h / 2), (sx - L * .02, y - h / 2)],
               y - h / 2 - h * .22, y - h / 2, base=hb if not dark else "#0e1014", panel=14)
        a["bridge"] = (sx + L * .07, y - h / 2 - h * .22)
    for k in range(nturret):
        tx = x + L * (.48 + k * (.4 / max(1, nturret)))
        if kind in ("cruiser", "capital", "escort", "frigate"):
            for sgn in (-1, 1):
                ty = y + sgn * h / 2
                c.ellipse((tx - 9 * scale, ty - 6 * scale, tx + 9 * scale, ty + 6 * scale), "#4a5262")
                bx = tx + 22 * scale
                c.line((tx, ty + sgn * 2 * scale), (bx, ty + sgn * 4 * scale), 2.4 * scale, "#2a2f38")
                a["turrets"].append((bx, ty + sgn * 4 * scale))
    if lights:
        n = int(L / 28)
        c.lights([(x + 20 + i * (L - 40) / max(1, n), y + h * .32) for i in range(n) if r.random() < .6],
                 "#ffd28a", 1.1 * max(.7, scale), .5)
        c.glow_dot(x + L - 3, y, 2.2, "#ff4a3a", .7)
    return a


# ---- Effects ---------------------------------------------------------------------------------------

def streak(c, p0, p1, color="#bfeaff", width=2.0, s=1.0, head=True):
    c.line(p0, p1, width * 3, color, glow=True, s=.15 * s)
    c.line(p0, p1, width, color, glow=True, s=.9 * s)
    if head:
        c.glow_dot(p1[0], p1[1], width * 2, "#ffffff", 1.2 * s)


def trail(c, pts, color="#c8d4e0", s=.35, width=2.5):
    for i in range(len(pts) - 1):
        f = (i + 1) / len(pts)
        c.line(pts[i], pts[i + 1], width * (.4 + f), color, glow=True, s=s * f)


def missile(c, x, y, ang=0.0, size=1.0, trail_len=120, color="#ffd08a", curve=0.0):
    ca, sa = np.cos(ang), np.sin(ang)
    pts = []
    for k in range(12):
        t = k / 11
        d = trail_len * (1 - t)
        pts.append((x - ca * d - sa * curve * np.sin(t * np.pi) , y - sa * d + ca * curve * np.sin(t * np.pi)))
    trail(c, pts, "#9aa8bc", .25)
    body = 14 * size
    c.line((x - ca * body, y - sa * body), (x, y), 3.2 * size, "#d8dee8")
    c.glow_dot(x - ca * body, y - sa * body, 3 * size, color, 1.2)


def explosion(c, x, y, r=30, seed=0, color="#ff8a3c", sparks=30):
    c.glow_dot(x, y, r * .35, "#fff2d0", 1.8)
    c.glow_dot(x, y, r, color, .8)
    rr = rng(c.seed + 700 + seed)
    for i in range(sparks):
        a = rr.uniform(0, 2 * np.pi)
        L = rr.uniform(r * .4, r * 2.2)
        c.line((x + np.cos(a) * r * .2, y + np.sin(a) * r * .2), (x + np.cos(a) * L, y + np.sin(a) * L), .8, "#ffb060", glow=True, s=.5)
    smoke = noise(H, W, 14, c.seed + 710 + seed, 4) * np.exp(-((c.x - x) ** 2 + (c.y - y) ** 2) / (r * r * 3))
    c.over(col("#2a2420"), smoke * .5)


def debris(c, x, y, spread=60, n=30, seed=0):
    rr = rng(c.seed + 800 + seed)
    for i in range(n):
        px, py = x + rr.normal(0, spread), y + rr.normal(0, spread * .5)
        s = rr.uniform(1.5, 5)
        a = rr.uniform(0, np.pi)
        pts = [(px + np.cos(a + k * 2.1) * s * rr.uniform(.6, 1.2), py + np.sin(a + k * 2.1) * s * rr.uniform(.6, 1.2)) for k in range(3)]
        c.poly(pts, "#6a6e76" if rr.random() < .7 else "#a0a6b0")


def shield(c, cx, cy, rx, ry, color="#7fd0ff", s=1.0, hit=None):
    dx, dy = (c.x - cx) / rx, (c.y - cy) / ry
    d = np.sqrt(dx * dx + dy * dy)
    hexn = noise(H, W, 7, c.seed + 900, 2)
    rim = np.exp(-((d - 1) / .035) ** 2)
    fill = ss(1 - d, 0, .3) * (d < 1) * .08
    c.add(col(color), (rim * .55 + fill * (.5 + hexn)) * s)
    if hit:
        hx, hy = hit
        dd = np.sqrt((c.x - hx) ** 2 + (c.y - hy) ** 2)
        c.add(col("#ffffff"), np.exp(-(dd / 10) ** 2) * 1.5 * s)
        c.add(col(color), np.exp(-(dd / 60) ** 2) * rim * 2.5 * s + np.exp(-(dd / 40) ** 2) * (d < 1.02) * .3 * s)


def tracers(c, x, y, tx, ty, n=6, color="#ffe08a", seed=0, spread=14):
    rr = rng(c.seed + 950 + seed)
    for i in range(n):
        ex, ey = tx + rr.normal(0, spread), ty + rr.normal(0, spread)
        t0 = rr.uniform(.1, .7)
        p0 = (x + (ex - x) * t0, y + (ey - y) * t0)
        p1 = (x + (ex - x) * (t0 + .12), y + (ey - y) * (t0 + .12))
        c.line(p0, p1, 1.2, color, glow=True, s=.9)


def beam(c, p0, p1, color="#9fd8ff", width=3, s=1.0):
    c.line(p0, p1, width * 5, color, glow=True, s=.12 * s)
    c.line(p0, p1, width * 2, color, glow=True, s=.4 * s)
    c.line(p0, p1, width * .6, "#ffffff", glow=True, s=1.0 * s)


def waves(c, x, y, r0, n=5, step=26, color="#9fd8ff", ang=0.0, spread=.7, s=.4):
    dd = np.sqrt((c.x - x) ** 2 + (c.y - y) ** 2)
    aa = np.arctan2(c.y - y, c.x - x) - ang
    aa = (aa + np.pi) % (2 * np.pi) - np.pi
    sector = ss(spread - np.abs(aa), 0, .15)
    for i in range(n):
        rr = r0 + i * step
        c.add(col(color), np.exp(-((dd - rr) / 1.3) ** 2) * sector * s * (1 - i / (n + 1)))


def dashed(c, p0, p1, color="#7cc4ff", dash=10, gap=8, width=1.4, s=.7):
    L = np.hypot(p1[0] - p0[0], p1[1] - p0[1])
    n = int(L / (dash + gap))
    for i in range(n + 1):
        t0, t1 = i * (dash + gap) / L, min(1, (i * (dash + gap) + dash) / L)
        if t0 >= 1:
            break
        c.line((p0[0] + (p1[0] - p0[0]) * t0, p0[1] + (p1[1] - p0[1]) * t0), (p0[0] + (p1[0] - p0[0]) * t1, p0[1] + (p1[1] - p0[1]) * t1), width, color, glow=True, s=s)


# ---- Structures ------------------------------------------------------------------------------------

def building(c, x, y_ground, w, h, base="#6a7078", windows=True, seed=0, roof=None, lit=.6):
    c.hull([(x, y_ground - h), (x + w, y_ground - h), (x + w, y_ground), (x, y_ground)], y_ground - h, y_ground, base=base, panel=max(8, int(w / 4)), seed=seed, grime=.5)
    if roof == "dome":
        c.ellipse((x, y_ground - h - w * .3, x + w, y_ground - h + w * .3), base)
        m = c.mask(lambda d, s: d.ellipse([x * s, (y_ground - h - w * .3) * s, (x + w) * s, (y_ground - h + w * .3) * s], fill=255))
        v = np.clip((c.y - (y_ground - h - w * .3)) / (w * .3), 0, 1)
        c.over(col(base) * (1.2 - .6 * v[..., None]), m * (c.y < y_ground - h))
    if windows:
        rr = rng(c.seed + 1000 + seed)
        pts = []
        for wx in np.arange(x + 5, x + w - 4, 7):
            for wy in np.arange(y_ground - h + 6, y_ground - 4, 8):
                if rr.random() < lit:
                    pts.append((wx, wy))
        for px, py in pts:
            c.add(col("#ffcf8a"), c.mask(lambda d, s: d.rectangle([px * s, py * s, (px + 2.5) * s, (py + 1.6) * s], fill=255)) * .9)


def dome(c, cx, y_ground, r, glass="#9fd8ff", inner=None, s=1.0):
    dx, dy = (c.x - cx) / r, (c.y - y_ground) / r
    d = np.sqrt(dx * dx + dy * dy)
    m = ss(1 - d, 0, .01) * (c.y < y_ground)
    if inner:
        inner()
    rim = np.exp(-((d - 1) / .02) ** 2) * (c.y < y_ground)
    c.add(col(glass), (m * .08 + rim * .5) * s, bloom=False)
    hl = np.exp(-(((dx + .4) / .15) ** 2 + ((dy + .55) / .25) ** 2)) * m
    c.add(col("#ffffff"), hl * .25 * s, bloom=False)


def tower(c, x, y_ground, w, h, base="#8a8e94"):
    c.hull([(x, y_ground - h), (x + w, y_ground - h), (x + w * 1.15, y_ground), (x - w * .15, y_ground)], y_ground - h, y_ground, base=base, panel=60, grime=.4)


def dish(c, x, y_ground, r, base="#c8ccd2", ang=-.6):
    c.line((x, y_ground), (x, y_ground - r * .8), 3, "#5a6068")
    cx, cy = x + np.cos(ang) * r * .1, y_ground - r * .9
    ca, sa = np.cos(ang), np.sin(ang)
    pts = [(cx - ca * r + sa * 0, cy - sa * r), (cx + ca * r, cy + sa * r), (cx + ca * r * .6 - sa * r * .45, cy + sa * r * .6 + ca * r * .45 * -1 * -1),
           (cx - ca * r * .6 - sa * r * .45, cy - sa * r * .6 + ca * r * .45)]
    c.ellipse((cx - r, cy - r * .32, cx + r, cy + r * .32), "#d8dee8")
    c.ellipse((cx - r * .85, cy - r * .2, cx + r * .85, cy + r * .28), "#7d8796")
    c.line((cx, cy), (cx, cy - r * .5), 1.5, "#c8ccd2")
    return (cx, cy - r * .5)


def solar_panel(c, x0, y0, x1, y1):
    c.poly([(x0, y0), (x1, y0), (x1, y1), (x0, y1)], "#16324a")
    n = noise(H, W, 3, c.seed + 1200, 2)
    m = c.mask(lambda d, s: d.rectangle([x0 * s, y0 * s, x1 * s, y1 * s], fill=255))
    c.add(col("#3a7aa8"), m * n * .25, bloom=False)
    for gx in np.arange(x0, x1, 9):
        c.line((gx, y0), (gx, y1), .6, "#0a1622", alpha=.8)


def station(c, cx, cy, r, ring=True, panels=True, seed=0):
    if panels:
        for sgn in (-1, 1):
            solar_panel(c, cx + sgn * r * 1.2 - (r * 1.3 if sgn < 0 else 0), cy - r * .18, cx + sgn * r * 1.2 + (0 if sgn < 0 else r * 1.3), cy + r * .18)
            c.line((cx, cy), (cx + sgn * r * 1.2, cy), 2, "#8a94a4")
    if ring:
        R = r
        dx, dy = (c.x - cx) / R, (c.y - cy) / (R * .32)
        d = np.sqrt(dx * dx + dy * dy)
        tube = np.exp(-((d - 1) / .09) ** 2)
        lit = ss(-dy, -1.5, 1.5)
        c.over(col("#b8c0cc") * (.3 + .7 * lit[..., None]), ss(tube, .3, .5))
        c.add(col("#ffd28a"), ss(tube, .3, .5) * (np.sin(np.arctan2(dy, dx) * 24) > .9) * .4)
    c.hull([(cx - r * .18, cy - r * .3), (cx + r * .18, cy - r * .3), (cx + r * .18, cy + r * .3), (cx - r * .18, cy + r * .3)], cy - r * .3, cy + r * .3, base="#c8ccd2", panel=8, seed=seed)
    c.lights([(cx, cy - r * .3), (cx, cy + r * .3)], "#ff4a3a", 1.4, .6)


def satellite(c, cx, cy, s=1.0, dish_up=True):
    c.poly([(cx - 36 * s, cy - 5 * s), (cx - 10 * s, cy - 5 * s), (cx - 10 * s, cy + 5 * s), (cx - 36 * s, cy + 5 * s)], "#1d3d58")
    c.poly([(cx + 10 * s, cy - 5 * s), (cx + 36 * s, cy - 5 * s), (cx + 36 * s, cy + 5 * s), (cx + 10 * s, cy + 5 * s)], "#1d3d58")
    c.hull([(cx - 9 * s, cy - 8 * s), (cx + 9 * s, cy - 8 * s), (cx + 9 * s, cy + 8 * s), (cx - 9 * s, cy + 8 * s)], cy - 8 * s, cy + 8 * s, base="#d8c890", panel=6)
    if dish_up:
        c.ellipse((cx - 8 * s, cy - 16 * s, cx + 8 * s, cy - 10 * s), "#e0e4ea")
    c.glow_dot(cx, cy + 8 * s, 1.5 * s, "#5cff9a", .6)


def person(c, x, y_feet, h, color="#0c1218", alpha=.9):
    """A space-suited figure in silhouette."""
    hr = h * .11
    c.ellipse((x - hr, y_feet - h, x + hr, y_feet - h + hr * 2), color, alpha)
    c.poly([(x - h * .14, y_feet - h + hr * 2), (x + h * .14, y_feet - h + hr * 2), (x + h * .12, y_feet - h * .45), (x - h * .12, y_feet - h * .45)], color, alpha)
    c.poly([(x - h * .12, y_feet - h * .45), (x - h * .02, y_feet - h * .45), (x - h * .04, y_feet), (x - h * .13, y_feet)], color, alpha)
    c.poly([(x + h * .02, y_feet - h * .45), (x + h * .12, y_feet - h * .45), (x + h * .13, y_feet), (x + h * .04, y_feet)], color, alpha)


def interior(c, tint="#0c1016", light="#bfe6ff", vx=560, vy=190, panels=10, seed=0):
    """A corridor seen down its length: walls, floor, ceiling lights. Returns the projection function."""
    c.img[:] = col("#05070b")
    def P(z, yy, side):
        return (vx + side * 560 / z, vy + yy / z)
    for side in (-1, 1):
        for i in range(panels):
            z0, z1 = 1 + i * .8, 1 + (i + 1) * .8
            c.poly([P(z0, -160, side), P(z1, -160, side), P(z1, 170, side), P(z0, 170, side)], tint if i % 2 else "#10151c")
    c.poly([(0, H), (W, H), (vx + 40, vy + 22), (vx - 40, vy + 22)], "#0a0d12")
    c.poly([(0, 0), (W, 0), (vx + 40, vy - 22), (vx - 40, vy - 22)], "#080a0e")
    for i in range(panels):
        z = 1 + i * .8
        c.line((vx - 110 / z, vy - 155 / z), (vx + 110 / z, vy - 155 / z), 5 / z, light, glow=True, s=.7 / (1 + i * .3))
    c.glow_dot(vx, vy, 70, light, .12)
    c.img = c.img * 1.7
    c.add(col(light), np.exp(-((c.x - vx) / 260) ** 2) * ss(c.y, vy + 30, H) * .06, bloom=False)
    return P
