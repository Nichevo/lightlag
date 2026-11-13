#!/usr/bin/env python3
"""
Draws the idea card pictures (game/Art/Ideas/<idea id>.svg, 560 x 192) in the approved style: a dark star field,
side-on metal hulls, orange radiators, pale blue tanks and small warm lights. Each picture is put together from a
small kit of parts (hulls, planets, stations, beams, missiles, domes...), one scene per idea.

Run from the repo root:  python3 tools/idea-art/make_ideas.py  [--only id,id]
Ideas listed in KEEP use the older shared drawings (escort, tanker...) and are left alone.
The game looks for Ideas/<id>.svg first and falls back to the idea's "picture" in economy.json.
"""
import json, math, os, random, sys

W, H = 560, 192
OUT = os.path.join("game", "Art", "Ideas")

DEFS = """<defs>
<linearGradient id="sky" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#0b1222"/><stop offset="1" stop-color="#05070d"/></linearGradient>
<linearGradient id="hs" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#e2e8f1"/><stop offset=".18" stop-color="#b3bfcf"/><stop offset=".55" stop-color="#6f7c90"/><stop offset="1" stop-color="#232a37"/></linearGradient>
<linearGradient id="dk" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#7d899c"/><stop offset=".5" stop-color="#3d4656"/><stop offset="1" stop-color="#151a24"/></linearGradient>
<linearGradient id="ar" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#cdbfa6"/><stop offset=".45" stop-color="#8a7d68"/><stop offset="1" stop-color="#2e2820"/></linearGradient>
<linearGradient id="rd" x1="0" y1="1" x2="0" y2="0"><stop offset="0" stop-color="#ff7a3c" stop-opacity=".9"/><stop offset="1" stop-color="#5a2a18" stop-opacity=".8"/></linearGradient>
<linearGradient id="tk" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#eef9ff"/><stop offset=".35" stop-color="#9fd8f5"/><stop offset="1" stop-color="#1f4b63"/></linearGradient>
<linearGradient id="bell" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#9aa3b2"/><stop offset=".5" stop-color="#4a5262"/><stop offset="1" stop-color="#14181f"/></linearGradient>
<linearGradient id="sail" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#fff6dc"/><stop offset=".5" stop-color="#cfd8e6"/><stop offset="1" stop-color="#6f7c90"/></linearGradient>
<linearGradient id="panel" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#2d5f7a"/><stop offset="1" stop-color="#12293a"/></linearGradient>
<linearGradient id="ground" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#6b5e4e"/><stop offset=".3" stop-color="#3b3329"/><stop offset="1" stop-color="#15120e"/></linearGradient>
<linearGradient id="groundg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#4f6a4a"/><stop offset=".3" stop-color="#2a3a28"/><stop offset="1" stop-color="#0e140d"/></linearGradient>
<linearGradient id="groundh" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#8a4a2a"/><stop offset=".3" stop-color="#4a2416"/><stop offset="1" stop-color="#160a06"/></linearGradient>
<linearGradient id="groundi" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#c9d6e2"/><stop offset=".3" stop-color="#6e8295"/><stop offset="1" stop-color="#1c2530"/></linearGradient>
<linearGradient id="beam" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#bfeaff" stop-opacity=".1"/><stop offset=".5" stop-color="#bfeaff" stop-opacity=".9"/><stop offset="1" stop-color="#ffffff"/></linearGradient>
<linearGradient id="exh" x1="1" y1="0" x2="0" y2="0"><stop offset="0" stop-color="#ffffff" stop-opacity=".95"/><stop offset=".25" stop-color="#bfeaff" stop-opacity=".7"/><stop offset="1" stop-color="#5cc8ff" stop-opacity="0"/></linearGradient>
<linearGradient id="exhw" x1="1" y1="0" x2="0" y2="0"><stop offset="0" stop-color="#ffffff" stop-opacity=".95"/><stop offset=".25" stop-color="#ffd08a" stop-opacity=".7"/><stop offset="1" stop-color="#ff7a3c" stop-opacity="0"/></linearGradient>
<linearGradient id="exhv" x1="1" y1="0" x2="0" y2="0"><stop offset="0" stop-color="#ffffff" stop-opacity=".95"/><stop offset=".25" stop-color="#e2b8ff" stop-opacity=".7"/><stop offset="1" stop-color="#a56bff" stop-opacity="0"/></linearGradient>
<radialGradient id="sun" cx=".5" cy=".5" r=".5"><stop offset="0" stop-color="#ffffff"/><stop offset=".3" stop-color="#fff2c4"/><stop offset=".6" stop-color="#ffc35c" stop-opacity=".6"/><stop offset="1" stop-color="#ff8a3c" stop-opacity="0"/></radialGradient>
<radialGradient id="glow" cx=".5" cy=".5" r=".5"><stop offset="0" stop-color="#bfeaff" stop-opacity=".8"/><stop offset="1" stop-color="#5cc8ff" stop-opacity="0"/></radialGradient>
<radialGradient id="glowo" cx=".5" cy=".5" r=".5"><stop offset="0" stop-color="#ffe2a8" stop-opacity=".9"/><stop offset=".4" stop-color="#ff9a3c" stop-opacity=".5"/><stop offset="1" stop-color="#ff7a3c" stop-opacity="0"/></radialGradient>
<radialGradient id="glowr" cx=".5" cy=".5" r=".5"><stop offset="0" stop-color="#ffd0c0" stop-opacity=".9"/><stop offset=".4" stop-color="#ff5c3c" stop-opacity=".5"/><stop offset="1" stop-color="#ff3c3c" stop-opacity="0"/></radialGradient>
<radialGradient id="glowv" cx=".5" cy=".5" r=".5"><stop offset="0" stop-color="#f0dcff" stop-opacity=".9"/><stop offset=".4" stop-color="#a56bff" stop-opacity=".5"/><stop offset="1" stop-color="#7a3cff" stop-opacity="0"/></radialGradient>
<radialGradient id="glowg" cx=".5" cy=".5" r=".5"><stop offset="0" stop-color="#d8ffe8" stop-opacity=".9"/><stop offset=".4" stop-color="#5cff9a" stop-opacity=".4"/><stop offset="1" stop-color="#3cff7a" stop-opacity="0"/></radialGradient>
<radialGradient id="shade" cx=".3" cy=".3" r=".9"><stop offset="0" stop-color="#000" stop-opacity="0"/><stop offset=".55" stop-color="#000" stop-opacity=".15"/><stop offset="1" stop-color="#000" stop-opacity=".85"/></radialGradient>
<radialGradient id="pl-earth" cx=".35" cy=".35" r=".7"><stop offset="0" stop-color="#9fd0e8"/><stop offset=".5" stop-color="#3a7aa0"/><stop offset="1" stop-color="#0f2a3e"/></radialGradient>
<radialGradient id="pl-rock" cx=".35" cy=".35" r=".7"><stop offset="0" stop-color="#c8b8a0"/><stop offset=".5" stop-color="#7a6a58"/><stop offset="1" stop-color="#2a221a"/></radialGradient>
<radialGradient id="pl-gas" cx=".35" cy=".35" r=".7"><stop offset="0" stop-color="#f2d8a8"/><stop offset=".5" stop-color="#c08a52"/><stop offset="1" stop-color="#4a2a14"/></radialGradient>
<radialGradient id="pl-ice" cx=".35" cy=".35" r=".7"><stop offset="0" stop-color="#ffffff"/><stop offset=".5" stop-color="#a9c8de"/><stop offset="1" stop-color="#2a4054"/></radialGradient>
<radialGradient id="pl-hot" cx=".35" cy=".35" r=".7"><stop offset="0" stop-color="#ffc08a"/><stop offset=".5" stop-color="#a8462a"/><stop offset="1" stop-color="#2e0e08"/></radialGradient>
<radialGradient id="pl-green" cx=".35" cy=".35" r=".7"><stop offset="0" stop-color="#c8e8b0"/><stop offset=".5" stop-color="#4a8a5a"/><stop offset="1" stop-color="#10281a"/></radialGradient>
</defs>"""


class Pic:
    def __init__(self, seed):
        self.parts = []
        self.rng = random.Random(seed)

    def add(self, s):
        self.parts.append(s)
        return self

    def svg(self):
        return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}">' + DEFS
                + "".join(self.parts) + "</svg>\n")


# ---- Parts ---------------------------------------------------------------------------------------

def f(x):
    return f"{x:.1f}".rstrip("0").rstrip(".")


def sky(p, stars=90):
    out = [f'<rect width="{W}" height="{H}" fill="url(#sky)"/>']
    for _ in range(stars):
        out.append(f'<circle cx="{f(p.rng.uniform(0, W))}" cy="{f(p.rng.uniform(0, H))}" r="{f(p.rng.choice([.5, .7, .9, 1.1]))}" '
                   f'fill="#fff" fill-opacity="{f(p.rng.uniform(.2, .8))}"/>')
    p.add("".join(out))


def plume(p, x, y, length, h, kind="exh"):
    p.add(f'<path d="M{f(x)} {f(y - h / 2)} L{f(x - length)} {f(y - h * .15)} L{f(x - length)} {f(y + h * .15)} L{f(x)} {f(y + h / 2)} Z" fill="url(#{kind})"/>')
    p.add(f'<ellipse cx="{f(x - length * .12)}" cy="{f(y)}" rx="{f(length * .14)}" ry="{f(h * .22)}" fill="#fff" opacity=".75"/>')


def hull(p, x, y, length, h, radiators=True, tanks=0, armour=False, burn=0, nose=True, windows=True, plume_kind="exh",
         turrets=0, hull_fill="hs", dish=False, cargo=0):
    """A side-on ship from x (stern) to x + length, centred on y."""
    s = []
    bell = h * .55
    if burn:
        plume(p, x, y, burn, h * .9, plume_kind)
    s.append(f'<path d="M{f(x + bell * .7)} {f(y - h * .3)} L{f(x)} {f(y - h * .45)} L{f(x)} {f(y + h * .45)} L{f(x + bell * .7)} {f(y + h * .3)} Z" fill="url(#bell)" stroke="#a9b3c2" stroke-width=".6"/>')
    body_x = x + bell * .7
    end = x + length - (h * .6 if nose else 0)
    if radiators:
        rx = body_x + length * .08
        rw = length * .14
        for sgn in (-1, 1):
            y0 = y + sgn * h / 2
            y1 = y + sgn * (h / 2 + h * .75)
            s.append(f'<path d="M{f(rx)} {f(y0)} L{f(rx + rw * .25)} {f(y1)} L{f(rx + rw)} {f(y1)} L{f(rx + rw)} {f(y0)} Z" fill="url(#rd)" stroke="#c98a6a" stroke-width=".6"/>')
            for k in range(1, 4):
                xx = rx + rw * .25 + (rw * .75) * k / 4
                s.append(f'<path d="M{f(xx)} {f(y0)} V{f(y1)}" stroke="#2b1c18" stroke-opacity=".5" stroke-width=".6"/>')
    if cargo:
        cw = (end - body_x - length * .3) / cargo
        cols = ["#8a5a3a", "#3a6a8a", "#7a7a3a", "#5a3a7a", "#3a7a5a", "#8a3a3a"]
        for k in range(cargo):
            for sgn in (-1, 1):
                yy = y - h / 2 - h * .9 if sgn < 0 else y + h / 2
                s.append(f'<rect x="{f(body_x + length * .22 + k * cw)}" y="{f(yy)}" width="{f(cw - 1.5)}" height="{f(h * .9)}" fill="{cols[(k * 2 + (sgn > 0)) % 6]}" stroke="#1c2230" stroke-width=".6"/>')
    nose_path = f'L{f(x + length)} {f(y)} ' if nose else ''
    s.append(f'<path d="M{f(body_x)} {f(y - h / 2)} L{f(end)} {f(y - h / 2)} {nose_path}L{f(end)} {f(y + h / 2)} L{f(body_x)} {f(y + h / 2)} Z" fill="url(#{hull_fill})" stroke="#e6ecf5" stroke-opacity=".6" stroke-width=".7"/>')
    n = max(3, int((end - body_x) / 22))
    for k in range(1, n):
        xx = body_x + (end - body_x) * k / n
        s.append(f'<path d="M{f(xx)} {f(y - h / 2 + 1)} V{f(y + h / 2 - 1)}" stroke="#3b4454" stroke-opacity=".5" stroke-width=".6"/>')
    if armour:
        s.append(f'<path d="M{f(end - length * .3)} {f(y - h / 2 - h * .18)} L{f(end)} {f(y - h / 2 - h * .18)} L{f(x + length + 4)} {f(y)} L{f(end)} {f(y + h / 2 + h * .18)} L{f(end - length * .3)} {f(y + h / 2 + h * .18)} Z" fill="url(#ar)" stroke="#efe3cc" stroke-opacity=".4" stroke-width=".6"/>')
    for k in range(tanks):
        tw = length * .5 / max(tanks, 1)
        tx = body_x + length * .25 + k * tw
        s.append(f'<rect x="{f(tx)}" y="{f(y - h * .28)}" width="{f(tw - 3)}" height="{f(h * .56)}" rx="{f(h * .28)}" fill="url(#tk)" stroke="#d6f3ff" stroke-opacity=".5" stroke-width=".6"/>')
    if windows:
        wx0 = body_x + (end - body_x) * .62
        for k in range(7):
            s.append(f'<rect x="{f(wx0 + k * 6)}" y="{f(y - h * .25)}" width="3.6" height="2.4" rx=".8" fill="#ffe2a8"/>')
    for k in range(turrets):
        tx = body_x + (end - body_x) * (.45 + .2 * k)
        for sgn in (-1, 1):
            ty = y + sgn * (h / 2 + 3)
            s.append(f'<circle cx="{f(tx)}" cy="{f(ty)}" r="4" fill="url(#dk)" stroke="#aeb9cb" stroke-width=".6"/>'
                     f'<path d="M{f(tx)} {f(ty)} l12 {f(sgn * -4)}" stroke="#c3cddc" stroke-width="1.6"/>')
    if dish:
        dx = body_x + (end - body_x) * .4
        s.append(f'<path d="M{f(dx)} {f(y - h / 2)} V{f(y - h / 2 - 12)}" stroke="#aeb9cb" stroke-width="1.2"/>'
                 f'<path d="M{f(dx - 12)} {f(y - h / 2 - 12)} Q{f(dx)} {f(y - h / 2 - 24)} {f(dx + 12)} {f(y - h / 2 - 12)} Z" fill="#c3cddc"/>')
    p.add("".join(s))


def planet(p, cx, cy, r, kind="earth", ring=False, atmo=None):
    s = []
    if atmo:
        s.append(f'<circle cx="{f(cx)}" cy="{f(cy)}" r="{f(r * 1.08)}" fill="none" stroke="{atmo}" stroke-opacity=".35" stroke-width="{f(r * .08)}"/>')
    if ring:
        s.append(f'<ellipse cx="{f(cx)}" cy="{f(cy)}" rx="{f(r * 1.9)}" ry="{f(r * .35)}" fill="none" stroke="#d8c8a8" stroke-opacity=".45" stroke-width="{f(r * .12)}"/>')
    s.append(f'<circle cx="{f(cx)}" cy="{f(cy)}" r="{f(r)}" fill="url(#pl-{kind})"/>')
    if kind == "gas":
        for k in range(-3, 4):
            yy = cy + k * r * .22
            half = math.sqrt(max(r * r - (yy - cy) ** 2, 0))
            s.append(f'<path d="M{f(cx - half)} {f(yy)} H{f(cx + half)}" stroke="#7a4a24" stroke-opacity=".35" stroke-width="{f(r * .07)}"/>')
    if kind in ("rock", "ice"):
        for _ in range(5):
            a = p.rng.uniform(0, 6.28)
            d = p.rng.uniform(0, r * .7)
            s.append(f'<circle cx="{f(cx + math.cos(a) * d)}" cy="{f(cy + math.sin(a) * d)}" r="{f(r * p.rng.uniform(.05, .12))}" fill="#000" fill-opacity=".18"/>')
    s.append(f'<circle cx="{f(cx)}" cy="{f(cy)}" r="{f(r)}" fill="url(#shade)"/>')
    if ring:
        s.append(f'<path d="M{f(cx - r * 1.9)} {f(cy)} A{f(r * 1.9)} {f(r * .35)} 0 0 0 {f(cx + r * 1.9)} {f(cy)}" fill="none" stroke="#d8c8a8" stroke-opacity=".6" stroke-width="{f(r * .12)}"/>')
    p.add("".join(s))


def sun(p, cx, cy, r):
    p.add(f'<circle cx="{f(cx)}" cy="{f(cy)}" r="{f(r * 2.4)}" fill="url(#sun)" opacity=".7"/><circle cx="{f(cx)}" cy="{f(cy)}" r="{f(r)}" fill="#fff6dc"/>')


def glow(p, cx, cy, r, kind="glow"):
    p.add(f'<circle cx="{f(cx)}" cy="{f(cy)}" r="{f(r)}" fill="url(#{kind})"/>')


def ground(p, y=150, kind="ground", bulge=10):
    p.add(f'<path d="M0 {f(y + bulge)} Q{W / 2} {f(y - bulge)} {W} {f(y + bulge)} L{W} {H} L0 {H} Z" fill="url(#{kind})"/>'
          f'<path d="M0 {f(y + bulge)} Q{W / 2} {f(y - bulge)} {W} {f(y + bulge)}" fill="none" stroke="#bfeaff" stroke-opacity=".25" stroke-width="1.5"/>')


def ground_y(x, y=150, bulge=10):
    t = x / W
    return (1 - t) ** 2 * (y + bulge) + 2 * (1 - t) * t * (y - bulge) + t * t * (y + bulge)


def dome(p, x, r, tint="#9fd8f5", y=None, lights=True):
    y = ground_y(x) if y is None else y
    s = f'<path d="M{f(x - r)} {f(y)} A{f(r)} {f(r)} 0 0 1 {f(x + r)} {f(y)} Z" fill="{tint}" fill-opacity=".35" stroke="#d6f3ff" stroke-opacity=".7" stroke-width=".8"/>'
    s += f'<path d="M{f(x - r * .6)} {f(y - r * .8)} Q{f(x)} {f(y - r * 1.02)} {f(x + r * .3)} {f(y - r * .95)}" stroke="#fff" stroke-opacity=".5" fill="none" stroke-width="1"/>'
    if lights:
        for k in range(-2, 3):
            s += f'<rect x="{f(x + k * r * .3 - 1.5)}" y="{f(y - r * .3)}" width="3" height="2" fill="#ffe2a8"/>'
    p.add(s)


def block(p, x, w, h, lights=True, fill="dk", y=None, chimney=False, smoke=False):
    y = ground_y(x + w / 2) if y is None else y
    s = f'<rect x="{f(x)}" y="{f(y - h)}" width="{f(w)}" height="{f(h + 4)}" fill="url(#{fill})" stroke="#8a96ab" stroke-width=".6"/>'
    if lights:
        for row in range(int(h // 9)):
            for k in range(int(w // 8)):
                if (row * 7 + k * 3 + int(x)) % 5:
                    s += f'<rect x="{f(x + 3 + k * 8)}" y="{f(y - h + 4 + row * 9)}" width="3.5" height="2.4" fill="#ffe2a8" fill-opacity=".85"/>'
    if chimney:
        s += f'<rect x="{f(x + w * .7)}" y="{f(y - h - 18)}" width="6" height="18" fill="url(#dk)" stroke="#8a96ab" stroke-width=".5"/>'
        if smoke:
            s += f'<ellipse cx="{f(x + w * .7 + 8)}" cy="{f(y - h - 24)}" rx="10" ry="5" fill="#cfd8e6" fill-opacity=".18"/>'
    p.add(s)


def panel(p, x, y, w, h):
    s = f'<rect x="{f(x)}" y="{f(y)}" width="{f(w)}" height="{f(h)}" fill="url(#panel)" stroke="#5a9ab8" stroke-opacity=".7" stroke-width=".6"/>'
    for k in range(1, int(w // 10)):
        s += f'<path d="M{f(x + k * 10)} {f(y)} V{f(y + h)}" stroke="#0a1620" stroke-opacity=".6" stroke-width=".5"/>'
    s += f'<path d="M{f(x)} {f(y + h / 2)} H{f(x + w)}" stroke="#0a1620" stroke-opacity=".6" stroke-width=".5"/>'
    p.add(s)


def station(p, cx, cy, sc=1.0, panels=True, ring=True):
    s = []
    if panels:
        panel(p, cx - 120 * sc, cy - 14 * sc, 70 * sc, 28 * sc)
        panel(p, cx + 50 * sc, cy - 14 * sc, 70 * sc, 28 * sc)
        s.append(f'<rect x="{f(cx - 50 * sc)}" y="{f(cy - 2 * sc)}" width="{f(100 * sc)}" height="{f(4 * sc)}" fill="#aeb9cb"/>')
    if ring:
        s.append(f'<ellipse cx="{f(cx)}" cy="{f(cy)}" rx="{f(42 * sc)}" ry="{f(42 * sc)}" fill="none" stroke="url(#hs)" stroke-width="{f(7 * sc)}"/>')
        for a in range(0, 360, 45):
            r = math.radians(a)
            s.append(f'<path d="M{f(cx)} {f(cy)} L{f(cx + math.cos(r) * 40 * sc)} {f(cy + math.sin(r) * 40 * sc)}" stroke="#7d899c" stroke-width="{f(1.4 * sc)}"/>')
    s.append(f'<circle cx="{f(cx)}" cy="{f(cy)}" r="{f(14 * sc)}" fill="url(#hs)" stroke="#e6ecf5" stroke-opacity=".6" stroke-width=".7"/>')
    for k in range(-2, 3):
        s.append(f'<rect x="{f(cx + k * 5 * sc - 1.5)}" y="{f(cy - 1)}" width="3" height="2" fill="#ffe2a8"/>')
    p.add("".join(s))


def dish(p, x, y, size=18, up=True, angle=0):
    s = f'<g transform="rotate({f(angle)} {f(x)} {f(y)})"><path d="M{f(x)} {f(y)} V{f(y - size * .8)}" stroke="#aeb9cb" stroke-width="1.4"/>'
    s += f'<path d="M{f(x - size)} {f(y - size * .8)} Q{f(x)} {f(y - size * 1.6)} {f(x + size)} {f(y - size * .8)} Z" fill="#c3cddc" stroke="#7f8ea6" stroke-width=".6"/>'
    s += f'<path d="M{f(x)} {f(y - size * .9)} V{f(y - size * 1.5)}" stroke="#aeb9cb" stroke-width=".8"/></g>'
    p.add(s)


def beam(p, x1, y1, x2, y2, color="#bfeaff", width=3, glow_width=10):
    p.add(f'<path d="M{f(x1)} {f(y1)} L{f(x2)} {f(y2)}" stroke="{color}" stroke-opacity=".25" stroke-width="{f(glow_width)}" stroke-linecap="round"/>'
          f'<path d="M{f(x1)} {f(y1)} L{f(x2)} {f(y2)}" stroke="{color}" stroke-width="{f(width)}" stroke-linecap="round"/>'
          f'<path d="M{f(x1)} {f(y1)} L{f(x2)} {f(y2)}" stroke="#fff" stroke-width="{f(width * .35)}" stroke-linecap="round"/>')


def missile(p, x, y, angle=0, size=1.0, trail=60, kind="exhw"):
    s = f'<g transform="translate({f(x)} {f(y)}) rotate({f(angle)}) scale({f(size)})">'
    if trail:
        s += f'<path d="M-10 -2 L{f(-10 - trail)} -.5 L{f(-10 - trail)} .5 L-10 2 Z" fill="url(#{kind})"/>'
    s += '<path d="M-10 -2.6 H8 L14 0 L8 2.6 H-10 Z" fill="url(#hs)" stroke="#e6ecf5" stroke-opacity=".5" stroke-width=".4"/>'
    s += '<path d="M-10 -2.6 L-13 -6 L-6 -2.6 Z M-10 2.6 L-13 6 L-6 2.6 Z" fill="#7d899c"/><rect x="5" y="-1" width="3" height="2" fill="#ff7a3c"/></g>'
    p.add(s)


def slug(p, x, y, angle=0, trail=80, color="#bfeaff"):
    r = math.radians(angle)
    p.add(f'<path d="M{f(x)} {f(y)} L{f(x - math.cos(r) * trail)} {f(y - math.sin(r) * trail)}" stroke="{color}" stroke-opacity=".5" stroke-width="2"/>'
          f'<circle cx="{f(x)}" cy="{f(y)}" r="2.4" fill="#fff"/>')


def mine(p, x, y, r=6, lit=True):
    s = f'<circle cx="{f(x)}" cy="{f(y)}" r="{f(r)}" fill="url(#dk)" stroke="#aeb9cb" stroke-width=".6"/>'
    for a in range(0, 360, 60):
        rr = math.radians(a)
        s += f'<path d="M{f(x + math.cos(rr) * r)} {f(y + math.sin(rr) * r)} L{f(x + math.cos(rr) * r * 1.6)} {f(y + math.sin(rr) * r * 1.6)}" stroke="#aeb9cb" stroke-width="1"/>'
    if lit:
        s += f'<circle cx="{f(x)}" cy="{f(y)}" r="1.6" fill="#ff5c3c"/>'
    p.add(s)


def rock(p, x, y, r, fill="#6b5e4e"):
    pts = []
    for k in range(9):
        a = k / 9 * math.tau
        d = r * p.rng.uniform(.7, 1.15)
        pts.append(f"{f(x + math.cos(a) * d)},{f(y + math.sin(a) * d)}")
    p.add(f'<polygon points="{" ".join(pts)}" fill="{fill}" stroke="#2a221a" stroke-width=".6"/>'
          f'<polygon points="{" ".join(pts)}" fill="url(#shade)"/>')


def bubble(p, cx, cy, rx, ry, color="#5cc8ff", op=.5):
    p.add(f'<ellipse cx="{f(cx)}" cy="{f(cy)}" rx="{f(rx)}" ry="{f(ry)}" fill="{color}" fill-opacity=".08" stroke="{color}" stroke-opacity="{op}" stroke-width="1.6"/>'
          f'<ellipse cx="{f(cx)}" cy="{f(cy)}" rx="{f(rx * .96)}" ry="{f(ry * .9)}" fill="none" stroke="{color}" stroke-opacity="{op / 3}" stroke-width="4"/>')


def waves(p, x, y, n=4, color="#5cc8ff", start=10, step=12, spread=40, direction=0):
    s = f'<g transform="rotate({f(direction)} {f(x)} {f(y)})">'
    for k in range(n):
        r = start + k * step
        a = math.radians(spread)
        s += (f'<path d="M{f(x + r * math.cos(-a))} {f(y + r * math.sin(-a))} A{f(r)} {f(r)} 0 0 1 {f(x + r * math.cos(a))} {f(y + r * math.sin(a))}" '
              f'fill="none" stroke="{color}" stroke-opacity="{f(.8 - k * .15)}" stroke-width="1.4"/>')
    p.add(s + "</g>")


def sat(p, x, y, sc=1.0):
    panel(p, x - 26 * sc, y - 5 * sc, 18 * sc, 10 * sc)
    panel(p, x + 8 * sc, y - 5 * sc, 18 * sc, 10 * sc)
    p.add(f'<rect x="{f(x - 8 * sc)}" y="{f(y - 7 * sc)}" width="{f(16 * sc)}" height="{f(14 * sc)}" rx="2" fill="url(#hs)" stroke="#e6ecf5" stroke-opacity=".5" stroke-width=".5"/>'
          f'<circle cx="{f(x)}" cy="{f(y - 9 * sc)}" r="{f(1.6 * sc)}" fill="#5cc8ff"/>')


def drone(p, x, y, sc=1.0, light="#5cc8ff"):
    p.add(f'<path d="M{f(x - 10 * sc)} {f(y)} L{f(x)} {f(y - 4 * sc)} L{f(x + 12 * sc)} {f(y)} L{f(x)} {f(y + 4 * sc)} Z" fill="url(#dk)" stroke="#aeb9cb" stroke-width=".5"/>'
          f'<circle cx="{f(x + 2 * sc)}" cy="{f(y)}" r="{f(1.4 * sc)}" fill="{light}"/>')


def crate_stack(p, x, y, cols=4, rows=2, w=14, h=10):
    c = ["#8a5a3a", "#3a6a8a", "#7a7a3a", "#5a3a7a", "#3a7a5a", "#8a3a3a"]
    s = ""
    for r in range(rows):
        for k in range(cols):
            s += f'<rect x="{f(x + k * (w + 1))}" y="{f(y - (r + 1) * (h + 1))}" width="{f(w)}" height="{f(h)}" fill="{c[(r * 3 + k) % 6]}" stroke="#1c2230" stroke-width=".6"/>'
    p.add(s)


def pod(p, x, y, sc=1.0, light=True):
    s = f'<g transform="translate({f(x)} {f(y)}) scale({f(sc)})"><path d="M-8 -6 H4 Q10 -6 10 0 Q10 6 4 6 H-8 Z" fill="url(#hs)" stroke="#e6ecf5" stroke-opacity=".5" stroke-width=".5"/>'
    s += '<circle cx="3" cy="0" r="2.2" fill="#ffe2a8"/><path d="M-8 -4 L-12 -6 V6 L-8 4 Z" fill="url(#bell)"/>'
    if light:
        s += '<circle cx="-2" cy="-7" r="1.4" fill="#ff5c3c"/>'
    p.add(s + "</g>")


def label(p, x, y, text, size=9, color="#e6ecf5", op=.75, anchor="start", weight=600):
    p.add(f'<text x="{f(x)}" y="{f(y)}" font-size="{size}" font-family="sans-serif" font-weight="{weight}" fill="{color}" fill-opacity="{op}" text-anchor="{anchor}">{text}</text>')


def arc_path(p, d, color="#9be3bd", width=1.4, dash=None, op=.8):
    da = f' stroke-dasharray="{dash}"' if dash else ""
    p.add(f'<path d="{d}" fill="none" stroke="{color}" stroke-opacity="{op}" stroke-width="{width}"{da}/>')


def arm(p, x, y, sc=1.0):
    """A robot arm on a base."""
    p.add(f'<g transform="translate({f(x)} {f(y)}) scale({f(sc)})"><rect x="-8" y="-6" width="16" height="6" fill="url(#dk)" stroke="#8a96ab" stroke-width=".5"/>'
          '<path d="M0 -6 L-6 -30 L18 -42" fill="none" stroke="#c3cddc" stroke-width="4" stroke-linejoin="round"/>'
          '<circle cx="0" cy="-6" r="3" fill="#7d899c"/><circle cx="-6" cy="-30" r="3" fill="#7d899c"/>'
          '<path d="M18 -42 l8 -4 M18 -42 l8 4" stroke="#aeb9cb" stroke-width="2"/><circle cx="27" cy="-44" r="1.5" fill="#ffb36b"/></g>')


def chip(p, x, y, sc=1.0, color="#5cc8ff"):
    s = f'<g transform="translate({f(x)} {f(y)}) scale({f(sc)})"><rect x="-16" y="-16" width="32" height="32" rx="2" fill="url(#dk)" stroke="{color}" stroke-opacity=".8" stroke-width="1"/>'
    for k in range(-12, 13, 6):
        s += f'<path d="M{k} -16 V-22 M{k} 16 V22 M-16 {k} H-22 M16 {k} H22" stroke="#aeb9cb" stroke-width="1.2"/>'
    s += f'<rect x="-8" y="-8" width="16" height="16" fill="{color}" fill-opacity=".35"/><circle cx="0" cy="0" r="3" fill="{color}"/></g>'
    p.add(s)


def person(p, x, y, sc=1.0, color="#cfd8e6"):
    """A suited figure (spacesuit), for crews, marines and people."""
    p.add(f'<g transform="translate({f(x)} {f(y)}) scale({f(sc)})"><circle cx="0" cy="-22" r="5" fill="{color}"/><rect x="-2.6" y="-23" width="5.2" height="2.4" rx="1" fill="#1f4b63"/>'
          f'<path d="M-6 -16 H6 L5 -4 H-5 Z" fill="{color}"/><path d="M-5 -4 L-5 6 M5 -4 L5 6" stroke="{color}" stroke-width="3.4"/>'
          f'<path d="M-6 -15 L-9 -6 M6 -15 L9 -6" stroke="{color}" stroke-width="2.6"/><rect x="-4" y="-14" width="8" height="5" fill="#3d4656"/></g>')


def heat_haze(p, cx, cy, rx, ry):
    p.add(f'<ellipse cx="{f(cx)}" cy="{f(cy)}" rx="{f(rx)}" ry="{f(ry)}" fill="url(#glowo)" opacity=".7"/>')


def flash(p, x, y, r=10, kind="glowo"):
    p.add(f'<circle cx="{f(x)}" cy="{f(y)}" r="{f(r)}" fill="url(#{kind})"/><circle cx="{f(x)}" cy="{f(y)}" r="{f(r * .18)}" fill="#fff"/>')


def tower(p, x, h, w=10, y=None, top_light=True):
    y = ground_y(x) if y is None else y
    s = f'<path d="M{f(x - w / 2)} {f(y)} L{f(x - w / 4)} {f(y - h)} L{f(x + w / 4)} {f(y - h)} L{f(x + w / 2)} {f(y)} Z" fill="url(#dk)" stroke="#8a96ab" stroke-width=".5"/>'
    if top_light:
        s += f'<circle cx="{f(x)}" cy="{f(y - h - 2)}" r="1.8" fill="#ff5c3c"/>'
    p.add(s)


def pipes(p, x1, x2, y, n=2):
    s = ""
    for k in range(n):
        s += f'<path d="M{f(x1)} {f(y + k * 4)} H{f(x2)}" stroke="#7d899c" stroke-width="2"/>'
    p.add(s)


def grid_web(p, cx, cy, r, color="#5cc8ff", n=5):
    pts = [(cx + math.cos(k / n * math.tau) * r, cy + math.sin(k / n * math.tau) * r * .6) for k in range(n)]
    s = ""
    for i in range(n):
        for j in range(i + 1, n):
            s += f'<path d="M{f(pts[i][0])} {f(pts[i][1])} L{f(pts[j][0])} {f(pts[j][1])}" stroke="{color}" stroke-opacity=".35" stroke-width="1"/>'
    for x, y in pts:
        s += f'<circle cx="{f(x)}" cy="{f(y)}" r="3" fill="{color}"/>'
    p.add(s)


# ---- Scenes, one per idea -------------------------------------------------------------------------

def engine_scene(p, burn, kind="exh", label_text=None, glow_kind=None):
    if glow_kind:
        glow(p, 150, 96, 60, glow_kind)
    hull(p, 150, 96, 330, 30, burn=burn, plume_kind=kind, tanks=2)
    if label_text:
        label(p, 300, 120, label_text, 10, "#1c2230", .8)


def s_efficient_drives(p):
    hull(p, 160, 96, 320, 28, burn=110, tanks=3)
    label(p, 250, 87, "−15 %", 11, "#1f4b63", .9)


def s_engine_mk2(p):
    engine_scene(p, 170, "exh", "MK II")


def s_engine_mk3(p):
    engine_scene(p, 200, "exh", "MK III")


def s_engine_mk4(p):
    engine_scene(p, 220, "exhw", "FUSION", "glowo")


def s_engine_mk5(p):
    engine_scene(p, 240, "exhw", "MK V", "glowo")
    glow(p, 70, 96, 40, "glowo")


def s_antimatter_drive(p):
    glow(p, 100, 96, 90, "glowv")
    engine_scene(p, 250, "exhv", "AM")


def s_large_tanks(p):
    hull(p, 120, 96, 380, 34, tanks=4, burn=50)
    label(p, 260, 116, "+25 %", 10, "#1c2230", .7)


def s_quiet_drives(p):
    hull(p, 200, 96, 300, 26, burn=40)
    for k in range(4):
        r = 30 + k * 26
        p.add(f'<circle cx="200" cy="96" r="{r}" fill="none" stroke="#5cc8ff" stroke-opacity="{.25 - k * .05:.2f}" stroke-dasharray="3 5"/>')


def s_gravity_assist(p):
    planet(p, 300, 230, 120, "gas", atmo="#f2d8a8")
    arc_path(p, "M20 40 Q300 160 540 30", "#9be3bd", 1.6, "6 4")
    hull(p, 440, 58, 70, 10, radiators=False, burn=40, windows=False)


def s_orbital_tugs(p):
    hull(p, 60, 100, 160, 22, tanks=1, burn=60)
    p.add('<path d="M226 100 L300 104" stroke="#aeb9cb" stroke-width="1.6" stroke-dasharray="4 2"/>')
    hull(p, 300, 104, 220, 26, radiators=False, windows=False, hull_fill="dk")
    p.add('<path d="M420 92 l14 10 l-8 6 l10 8" stroke="#ff7a3c" stroke-width="1.4" fill="none"/>')
    flash(p, 470, 110, 8)


def s_picket_hull(p):
    hull(p, 200, 100, 180, 18, burn=60, dish=True)
    waves(p, 384, 100, 4, start=12, step=12)


def s_solar_sails(p):
    sun(p, 30, 40, 18)
    p.add('<path d="M260 20 L470 40 L470 170 L260 150 Z" fill="url(#sail)" fill-opacity=".9" stroke="#fff" stroke-opacity=".6"/>')
    for k in range(4):
        p.add(f'<path d="M{260 + k * 70} {20 + k * 6.7} L{360} 110" stroke="#aeb9cb" stroke-opacity=".5" stroke-width=".6"/>')
    hull(p, 320, 110, 90, 10, radiators=False, windows=False)
    for k in range(5):
        p.add(f'<path d="M60 {50 + k * 22} L250 {40 + k * 26}" stroke="#fff6dc" stroke-opacity=".12" stroke-width="5"/>')


def s_laser_sails(p):
    station(p, 70, 140, .5, ring=False)
    beam(p, 100, 136, 300, 96, "#7fe0a8", 3, 14)
    p.add('<path d="M300 30 L480 50 L480 160 L300 150 Z" fill="url(#sail)" fill-opacity=".9" stroke="#fff" stroke-opacity=".6"/>')
    glow(p, 300, 96, 30, "glowg")
    hull(p, 360, 110, 90, 10, radiators=False, windows=False)


def s_underway_replenishment(p):
    hull(p, 60, 70, 260, 22, tanks=3, burn=50)
    hull(p, 160, 130, 330, 26, turrets=1, burn=50)
    p.add('<path d="M230 81 C240 100 260 105 280 117" stroke="#9fd8f5" stroke-width="2" fill="none"/>')
    label(p, 360, 70, "FUEL + AMMO", 10, "#9fd8f5", .8)


def s_bulk_freighter_hull(p):
    hull(p, 40, 96, 480, 18, cargo=10, burn=40, radiators=False)


def s_carrier_hull(p):
    hull(p, 60, 110, 420, 40, turrets=1, burn=40, armour=True)
    p.add('<rect x="250" y="88" width="80" height="14" fill="#151a24" stroke="#5cc8ff" stroke-opacity=".6"/>')
    for k, (x, y) in enumerate([(380, 40), (420, 56), (460, 34), (500, 50)]):
        drone(p, x, y, 1.1, "#ffb36b")


def s_compact_engineering(p):
    for k, sc in enumerate([1.0, .72, .5]):
        hull(p, 40 + k * 170, 96, 150 * sc, 30 * sc, radiators=k == 0, windows=False)
    heat_haze(p, 400, 96, 50, 20)
    label(p, 500, 150, "smaller, hotter", 10, "#ffb36b", .8, "end")


def s_siege_monitor_hull(p):
    hull(p, 60, 100, 420, 50, armour=True, turrets=2, burn=20, hull_fill="ar")
    slug(p, 540, 70, -10, 60)


def s_slingshot_chains(p):
    planet(p, 140, 120, 34, "rock")
    planet(p, 330, 70, 22, "ice")
    planet(p, 480, 130, 40, "gas")
    arc_path(p, "M10 40 Q140 160 200 110 Q300 30 360 60 Q440 120 550 160", "#9be3bd", 1.6, "6 4")


def s_capital_hull(p):
    hull(p, 20, 100, 520, 54, armour=True, turrets=3, tanks=3, burn=10)


def s_fast_railguns(p):
    hull(p, 20, 120, 260, 26, turrets=2)
    for k in range(4):
        slug(p, 330 + k * 55, 92 - k * 6, -6, 50)


def s_missile_brain_2(p):
    missile(p, 200, 120, -12, 2.2, 80)
    arc_path(p, "M240 110 Q330 60 380 100 T520 70", "#ff9a3c", 1.4, "4 4")
    flash(p, 420, 140, 10, "glow")


def brain_scene(p, n, color):
    missile(p, 220, 110, -8, 2.4, 100)
    chip(p, 400, 90, 1.4, color)
    label(p, 400, 150, f"BRAIN {n}", 12, color, .9, "middle")


def s_missile_brain_3(p):
    brain_scene(p, 3, "#ffb36b")


def s_missile_brain_4(p):
    for k in range(5):
        missile(p, 220 + k * 30, 50 + k * 22, 4, 1.3, 70)
    chip(p, 470, 96, 1.1, "#ff9a3c")
    label(p, 470, 150, "PACK", 11, "#ff9a3c", .9, "middle")


def s_missile_brain_5(p):
    missile(p, 160, 120, -20, 2, 80)
    arc_path(p, "M200 104 Q300 40 360 90 Q420 140 480 70", "#ff5c3c", 1.6, "5 4")
    hull(p, 470, 64, 70, 12, radiators=False, windows=False)
    p.add('<path d="M330 60 l10 10 m0 -10 l-10 10" stroke="#ff5c3c" stroke-width="2"/>')


def s_railgun_mk3(p):
    hull(p, 20, 110, 280, 30, turrets=2)
    slug(p, 420, 80, -8, 110)
    flash(p, 520, 66, 22, "glowo")


def s_railgun_mk4(p):
    p.add('<rect x="40" y="88" width="320" height="16" fill="url(#hs)"/>')
    for k in range(10):
        p.add(f'<rect x="{60 + k * 30}" y="82" width="10" height="28" rx="2" fill="#b87333" stroke="#5a3a1a" stroke-width=".6"/>')
    slug(p, 520, 96, 0, 150)
    glow(p, 360, 96, 26)


def s_railgun_mk5(p):
    hull(p, 20, 110, 420, 46, turrets=2, armour=True, hull_fill="ar")
    slug(p, 545, 70, -12, 90, "#ffb36b")


def s_boarding_marines(p):
    hull(p, 260, 100, 300, 36, radiators=False, windows=True, hull_fill="dk")
    pod(p, 230, 92, 2.2, light=False)
    person(p, 120, 128, 1.6)
    person(p, 160, 132, 1.6)
    flash(p, 262, 100, 14)


def s_capacitor_banks(p):
    for k in range(5):
        x = 70 + k * 60
        p.add(f'<rect x="{x}" y="60" width="40" height="72" rx="4" fill="url(#dk)" stroke="#5cc8ff" stroke-opacity=".6"/>'
              f'<rect x="{x + 6}" y="{120 - k * 10}" width="28" height="{6 + k * 10}" fill="#5cc8ff" fill-opacity=".6"/>')
    beam(p, 380, 96, 540, 96, "#5cc8ff", 2, 8)


def s_heavy_warheads(p):
    missile(p, 150, 100, 0, 3.2, 100)
    flash(p, 440, 96, 70, "glowo")
    flash(p, 440, 96, 30, "glowr")


def s_precision_strikes(p):
    ground(p, 150, "ground")
    block(p, 140, 40, 30)
    block(p, 260, 50, 24, chimney=True)
    dome(p, 400, 22)
    p.add('<path d="M265 20 L285 125" stroke="#ff5c3c" stroke-width="1" stroke-dasharray="3 3"/><circle cx="285" cy="126" r="9" fill="none" stroke="#ff5c3c" stroke-width="1.4"/>')
    slug(p, 280, 100, 78, 70, "#ffb36b")


def s_special_ammo(p):
    for k, (c, n) in enumerate([("#8a8f99", "TUNGSTEN"), ("#c9a37a", "FLECHETTE"), ("#d9805f", "SHAPED")]):
        x = 110 + k * 160
        p.add(f'<rect x="{x - 14}" y="50" width="28" height="70" rx="3" fill="{c}" stroke="#1c2230"/><path d="M{x - 14} 50 Q{x} 22 {x + 14} 50 Z" fill="url(#hs)"/>')
        label(p, x, 145, n, 10, "#e6ecf5", .8, "middle")


def s_defensive_mines(p):
    planet(p, 90, 96, 60, "earth", atmo="#9fd8f5")
    for (x, y) in [(200, 40), (240, 90), (210, 150), (300, 60), (320, 130), (380, 96), (420, 40), (440, 150)]:
        mine(p, x, y, 6)
    hull(p, 470, 100, 70, 12, radiators=False, windows=False)


def s_free_electron_lasers(p):
    hull(p, 30, 120, 250, 28, turrets=1)
    beam(p, 290, 104, 470, 50, "#a56bff", 2.4, 10)
    missile(p, 480, 48, 180, 1.2, 40)
    flash(p, 472, 50, 16, "glowv")


def s_ion_disruptor(p):
    hull(p, 280, 110, 260, 34, windows=False)
    for k in range(6):
        x = 300 + k * 38
        p.add(f'<path d="M{x} 92 l6 -12 l-4 0 l8 -14" stroke="#9fd8ff" stroke-width="1.6" fill="none"/>')
    glow(p, 120, 96, 40)
    p.add('<path d="M120 96 C180 60 220 140 280 104" stroke="#9fd8ff" stroke-width="2" fill="none"/>')


def s_plasma_cannon(p):
    hull(p, 20, 110, 260, 32, turrets=1)
    p.add('<ellipse cx="380" cy="86" rx="40" ry="12" fill="url(#glowv)"/><circle cx="410" cy="86" r="8" fill="#f0dcff"/>')
    p.add('<path d="M470 40 L540 50 L540 130 L470 140 Z" fill="url(#ar)"/>')
    flash(p, 470, 90, 30, "glowv")


def s_pulse_lance(p):
    hull(p, 20, 110, 240, 30, turrets=1)
    beam(p, 270, 96, 540, 80, "#ff5c9a", 3, 16)
    label(p, 400, 130, "25,000 km", 10, "#ffb3cc", .8, "middle")


def s_heavy_armour(p):
    hull(p, 60, 100, 420, 40, armour=True, burn=30)
    p.add('<path d="M140 74 H440 V80 H140 Z M140 120 H440 V126 H140 Z" fill="url(#ar)"/>')


def s_debris_shielding(p):
    hull(p, 160, 110, 320, 30)
    p.add('<path d="M500 70 Q520 110 500 150" stroke="#cfd8e6" stroke-width="3" fill="none"/><path d="M512 70 Q532 110 512 150" stroke="#cfd8e6" stroke-opacity=".6" stroke-width="2" fill="none"/>')
    for (x, y) in [(540, 80), (548, 120), (530, 100)]:
        rock(p, x, y, 4)
    flash(p, 514, 96, 10)


def pd_scene(p, kills, color="#5cc8ff"):
    hull(p, 20, 130, 240, 26, turrets=2)
    for k in range(kills):
        x = 340 + k * 50
        y = 50 + (k % 2) * 40
        beam(p, 230, 116, x, y, color, 1, 4)
        flash(p, x, y, 12, "glowo")
    missile(p, 540, 80, 180, 1, 40)


def s_point_defence_2(p):
    pd_scene(p, 3)


def s_point_defence_3(p):
    pd_scene(p, 4)


def s_damage_control(p):
    hull(p, 60, 110, 420, 36, radiators=False)
    p.add('<path d="M260 92 l10 8 l-6 4 l8 6" stroke="#ff7a3c" stroke-width="1.6" fill="none"/>')
    person(p, 240, 90, 1.1)
    person(p, 300, 90, 1.1)
    flash(p, 266, 100, 7, "glow")


def s_radiator_fins(p):
    hull(p, 120, 96, 340, 30, radiators=True)
    for k in range(4):
        p.add(f'<path d="M{200 + k * 20} 40 q-6 -10 0 -20 q6 -10 0 -20" stroke="#ff9a3c" stroke-opacity=".5" fill="none"/>')
    p.add('<path d="M330 66 L340 30 L380 30 L380 66 Z M330 126 L340 162 L380 162 L380 126 Z" fill="url(#rd)" stroke="#c98a6a"/>')


def s_armour_mk3(p):
    hull(p, 60, 100, 420, 40, armour=True, hull_fill="ar")
    for k in range(8):
        p.add(f'<path d="M{150 + k * 36} 80 l18 0 l9 9 l-9 9 l-18 0 l-9 -9 Z" fill="#d8ccb8" fill-opacity=".35" stroke="#2e2820" stroke-width=".6"/>')


def s_counter_missiles(p):
    hull(p, 20, 130, 240, 26, turrets=1)
    for k in range(3):
        missile(p, 330 + k * 40, 100 - k * 20, -20, .8, 50, "exh")
    for k in range(3):
        missile(p, 520 - k * 20, 40 + k * 20, 160, 1, 30)
    flash(p, 470, 62, 12)


def s_deflector_grid(p):
    hull(p, 120, 96, 320, 32)
    bubble(p, 280, 96, 220, 70)
    for k in range(-3, 4):
        p.add(f'<path d="M{280 + k * 50} 28 Q{280 + k * 60} 96 {280 + k * 50} 164" stroke="#5cc8ff" stroke-opacity=".2" fill="none"/>')


def s_ground_launchers(p):
    ground(p, 155, "ground")
    for x in (150, 230, 310):
        p.add(f'<rect x="{x - 12}" y="{ground_y(x) - 8}" width="24" height="12" fill="url(#dk)" stroke="#8a96ab"/>')
    missile(p, 260, 80, -60, 1.4, 70)
    missile(p, 340, 50, -50, 1.4, 70)
    hull(p, 420, 30, 110, 14, radiators=False, windows=False)


def s_escape_pods(p):
    hull(p, 40, 90, 260, 32, windows=False, hull_fill="dk")
    flash(p, 200, 90, 30, "glowo")
    for (x, y, a) in [(330, 50, -20), (370, 110, 15), (420, 70, -5), (470, 130, 25)]:
        pod(p, x, y, 1.4)


def s_hardened_bunkers(p):
    ground(p, 140, "ground", 6)
    for x in (120, 280, 440):
        y = ground_y(x, 140, 6)
        p.add(f'<path d="M{x - 50} {y + 2} L{x - 36} {y - 22} L{x + 36} {y - 22} L{x + 50} {y + 2} Z" fill="url(#ar)" stroke="#efe3cc" stroke-opacity=".4"/>'
              f'<rect x="{x - 14}" y="{y - 14}" width="28" height="5" fill="#151a24"/>')
    flash(p, 300, 60, 18, "glowo")


def s_heat_sink_tanks(p):
    hull(p, 60, 96, 420, 34, tanks=0)
    for k in range(4):
        p.add(f'<rect x="{180 + k * 50}" y="84" width="44" height="24" rx="12" fill="#ffb36b" fill-opacity=".85" stroke="#ffe2a8"/>')
    heat_haze(p, 280, 96, 140, 30)


def s_predictive_fire_control(p):
    hull(p, 20, 140, 220, 24, turrets=1)
    arc_path(p, "M540 40 Q460 70 420 50 T330 80", "#ff9a3c", 1.4)
    arc_path(p, "M330 80 Q300 90 280 110", "#5cc8ff", 1.2, "3 3")
    p.add('<circle cx="300" cy="96" r="10" fill="none" stroke="#5cc8ff" stroke-width="1.4"/><path d="M286 96 h28 M300 82 v28" stroke="#5cc8ff"/>')
    beam(p, 220, 128, 300, 96, "#5cc8ff", 1, 4)


def s_shields_mk3(p):
    hull(p, 140, 96, 300, 30)
    bubble(p, 290, 96, 200, 62, "#7fe0ff", .8)
    bubble(p, 290, 96, 180, 52, "#7fe0ff", .4)
    flash(p, 470, 70, 10, "glow")


def s_planet_shields(p):
    ground(p, 150, "ground")
    dome(p, 280, 120, "#5cc8ff", lights=False)
    block(p, 230, 40, 30)
    block(p, 290, 30, 42)
    slug(p, 360, 52, 60, 50, "#ffb36b")
    flash(p, 362, 48, 12, "glow")


def s_strike_craft(p):
    planet(p, 60, 150, 100, "earth", atmo="#9fd8f5")
    for (x, y) in [(260, 60), (290, 80), (320, 50), (380, 110), (410, 90)]:
        drone(p, x, y, 1.4, "#ffb36b")
    hull(p, 450, 40, 100, 16, radiators=False, windows=False)


def s_spoofing(p):
    hull(p, 30, 120, 220, 24)
    arc_path(p, "M540 60 Q420 90 330 100 Q290 104 270 70 T220 30", "#ff9a3c", 1.4, "4 3")
    missile(p, 220, 30, -150, 1, 0)
    waves(p, 250, 116, 4, "#c08cff", 10, 14, 50, -30)


def s_ejectable_heat_sinks(p):
    hull(p, 220, 96, 300, 30)
    for (x, y) in [(150, 110), (100, 130), (60, 150)]:
        p.add(f'<rect x="{x - 14}" y="{y - 8}" width="28" height="16" rx="8" fill="#ffb36b" stroke="#ffe2a8"/>')
        heat_haze(p, x, y, 22, 14)
    missile(p, 40, 60, 30, 1, 40)


def s_heat_sink_armour(p):
    hull(p, 60, 100, 420, 40, armour=True, hull_fill="ar")
    heat_haze(p, 420, 100, 120, 40)


def s_repair_drones(p):
    hull(p, 60, 110, 420, 36)
    for (x, y) in [(200, 70), (260, 64), (330, 150), (400, 72)]:
        drone(p, x, y, 1.1, "#9be3bd")
        p.add(f'<path d="M{x} {y} L{x} {110 + (-18 if y < 110 else 18)}" stroke="#9be3bd" stroke-opacity=".7" stroke-dasharray="2 2"/>')


def s_adaptive_shields(p):
    hull(p, 140, 96, 300, 30)
    p.add('<path d="M290 34 A200 62 0 0 1 490 96" fill="none" stroke="#ff9a5c" stroke-width="4" stroke-opacity=".8"/>')
    bubble(p, 290, 96, 200, 62, "#5cc8ff", .5)
    flash(p, 470, 60, 12, "glowo")


def s_self_mending_armour(p):
    hull(p, 60, 100, 420, 40, armour=True, hull_fill="ar")
    p.add('<path d="M380 82 l10 10 l-6 6 l10 10" stroke="#9be3bd" stroke-width="2" fill="none"/>')
    glow(p, 390, 96, 24, "glowg")


def s_command_uplink(p):
    hull(p, 60, 120, 260, 30, dish=True)
    for r in (60, 100, 140):
        p.add(f'<circle cx="190" cy="96" r="{r}" fill="none" stroke="#9be3bd" stroke-opacity=".4" stroke-dasharray="6 6"/>')
    for (x, y) in [(400, 40), (460, 130), (520, 70)]:
        hull(p, x, y, 50, 8, radiators=False, windows=False)


def s_long_sensors(p):
    hull(p, 30, 120, 220, 24, dish=True)
    waves(p, 160, 90, 6, "#5cc8ff", 30, 50, 25, -10)


def s_sun_watch(p):
    sun(p, 80, 96, 50)
    p.add('<path d="M120 60 Q170 20 150 80" stroke="#ffb36b" stroke-width="4" fill="none" stroke-opacity=".7"/>')
    sat(p, 420, 96, 1.6)
    dish(p, 420, 80, 18, angle=-90)
    label(p, 420, 150, "9 h warning", 11, "#ffd27a", .85, "middle")


def s_rock_mapping(p):
    for k in range(14):
        rock(p, p.rng.uniform(220, 540), p.rng.uniform(20, 170), p.rng.uniform(4, 12))
    sat(p, 110, 96, 1.4)
    p.add('<path d="M140 96 L540 30 M140 96 L540 170" stroke="#5cc8ff" stroke-opacity=".3"/>')
    grid_web(p, 380, 96, 90, "#9be3bd", 6)


def s_concealed_mounts(p):
    hull(p, 40, 100, 440, 18, cargo=8, radiators=False)
    p.add('<rect x="260" y="68" width="40" height="15" fill="#151a24" stroke="#ff9a3c"/>')
    p.add('<circle cx="280" cy="76" r="4" fill="url(#dk)"/><path d="M280 76 l18 -8" stroke="#c3cddc" stroke-width="2"/>')


def s_signals_analysis(p):
    hull(p, 30, 130, 200, 22, dish=True)
    for k in range(40):
        x = 260 + k * 7
        h = 6 + abs(math.sin(k * .7) * 30) + (k % 5) * 2
        p.add(f'<rect x="{x}" y="{70 - h / 2}" width="4" height="{h:.1f}" fill="#5cc8ff" fill-opacity=".7"/>')
    label(p, 400, 140, "ID: RIVAL FRIGATE", 11, "#ff9a5c", .9, "middle")


def s_tight_beam(p):
    sat(p, 90, 96, 1.4)
    sat(p, 290, 60, 1.1)
    sat(p, 480, 110, 1.4)
    beam(p, 110, 92, 270, 62, "#9be3bd", 1.4, 5)
    beam(p, 310, 62, 460, 106, "#9be3bd", 1.4, 5)


def s_ai_cores(p):
    chip(p, 180, 96, 2.2)
    hull(p, 300, 110, 220, 26)
    p.add('<path d="M226 96 L300 108" stroke="#5cc8ff" stroke-dasharray="3 3"/>')


def s_deep_space_telescopes(p):
    p.add('<path d="M200 140 L360 60 L380 90 L220 170 Z" fill="url(#hs)" stroke="#e6ecf5" stroke-opacity=".5"/>'
          '<ellipse cx="370" cy="75" rx="10" ry="20" transform="rotate(-27 370 75)" fill="#1f4b63"/>')
    panel(p, 160, 110, 50, 24)
    p.add('<circle cx="500" cy="30" r="4" fill="#fff"/><path d="M500 30 L540 10" stroke="#bfeaff" stroke-opacity=".5" stroke-width="3"/>')


def s_satellites(p):
    planet(p, 280, 260, 170, "earth", atmo="#9fd8f5")
    arc_path(p, "M20 120 Q280 30 540 120", "#9be3bd", 1, "4 6", .6)
    sat(p, 120, 88, 1.1)
    sat(p, 300, 58, 1.1)
    sat(p, 470, 92, 1.1)


def s_fleet_data_links(p):
    pts = [(80, 60), (200, 130), (330, 70), (460, 130)]
    for i in range(len(pts)):
        for j in range(i + 1, len(pts)):
            p.add(f'<path d="M{pts[i][0] + 40} {pts[i][1]} L{pts[j][0] + 40} {pts[j][1]}" stroke="#5cc8ff" stroke-opacity=".4" stroke-dasharray="4 3"/>')
    for (x, y) in pts:
        hull(p, x, y, 90, 14, radiators=False, windows=False)


def s_masked_emissions(p):
    hull(p, 120, 96, 340, 34, armour=True, burn=20)
    p.add('<rect x="100" y="40" width="380" height="112" fill="#0b1222" fill-opacity=".45"/>')
    label(p, 290, 160, "FLAGSHIP?", 12, "#aab4c3", .8, "middle")


def s_stealth_drones(p):
    drone(p, 300, 96, 3)
    for k in range(3):
        p.add(f'<circle cx="300" cy="96" r="{40 + k * 22}" fill="none" stroke="#5cc8ff" stroke-opacity="{.15 - k * .04:.2f}" stroke-dasharray="2 6"/>')


def s_decode_signals(p):
    dish(p, 120, 170, 40)
    waves(p, 160, 80, 5, "#c08cff", 30, 30, 30, 0)
    label(p, 420, 70, "◇ ▷ ◈ ◁ ◇", 18, "#c08cff", .9, "middle")
    label(p, 420, 110, "HELLO?", 14, "#e6ecf5", .7, "middle")


def s_iff_codes(p):
    hull(p, 60, 100, 220, 26)
    for (x, y) in [(330, 50), (380, 110), (440, 70), (500, 140)]:
        mine(p, x, y, 6)
        p.add(f'<circle cx="{x}" cy="{y}" r="14" fill="none" stroke="#9be3bd" stroke-opacity=".6" stroke-dasharray="2 3"/>')
    label(p, 160, 150, "FRIEND", 11, "#9be3bd", .9, "middle")


def s_missile_hijack(p):
    hull(p, 30, 120, 200, 24, dish=True)
    waves(p, 200, 80, 4, "#c08cff", 14, 16, 30, -10)
    arc_path(p, "M540 50 Q400 70 380 100 Q370 130 500 150", "#ff9a3c", 1.4, "4 3")
    missile(p, 500, 150, 10, 1.2, 0)


def s_jamming(p):
    hull(p, 100, 96, 300, 30)
    for k in range(30):
        x = p.rng.uniform(60, 520)
        y = p.rng.uniform(20, 170)
        p.add(f'<path d="M{x:.0f} {y:.0f} l{p.rng.uniform(-12, 12):.0f} {p.rng.uniform(-3, 3):.0f}" stroke="#c08cff" stroke-opacity=".5"/>')
    bubble(p, 250, 96, 220, 80, "#c08cff", .3)


def s_stealth_coatings(p):
    hull(p, 120, 96, 340, 30, radiators=False, hull_fill="dk", windows=False)
    p.add('<rect x="110" y="70" width="360" height="52" fill="#05070d" fill-opacity=".55"/>')
    label(p, 290, 150, "COLD", 11, "#5cc8ff", .6, "middle")


def s_cyber_attack(p):
    hull(p, 300, 96, 240, 30, windows=False)
    chip(p, 120, 96, 1.6, "#ff5c3c")
    for k in range(5):
        p.add(f'<path d="M150 {80 + k * 8} H{300 + k * 10}" stroke="#ff5c3c" stroke-opacity=".5" stroke-dasharray="2 4"/>')
    label(p, 420, 60, "01101", 11, "#ff5c3c", .8, "middle")


def s_automation(p):
    ground(p, 150, "ground", 4)
    for x in (120, 250, 380):
        arm(p, x, ground_y(x, 150, 4), 1.4)
    crate_stack(p, 430, ground_y(450, 150, 4), 3, 2)


def s_precision_workshops(p):
    ground(p, 150, "ground", 4)
    block(p, 80, 120, 50)
    arm(p, 280, ground_y(280, 150, 4), 1.5)
    p.add('<circle cx="350" cy="80" r="16" fill="none" stroke="#c3cddc" stroke-width="5" stroke-dasharray="5 4"/>')
    crate_stack(p, 400, ground_y(420, 150, 4), 4, 2)


def s_geothermal_power(p):
    ground(p, 140, "groundh", 6)
    p.add('<path d="M380 150 L420 96 L460 150 Z" fill="#4a2416"/><ellipse cx="420" cy="96" rx="12" ry="4" fill="#ff7a3c"/>')
    glow(p, 420, 90, 30, "glowo")
    block(p, 120, 70, 34, chimney=True, smoke=True)
    pipes(p, 190, 380, 124, 2)


def s_rare_element_assays(p):
    planet(p, 140, 96, 70, "rock")
    for (x, y, c) in [(120, 80, "#c08cff"), (160, 110, "#ffd27a"), (110, 120, "#7fe0a8")]:
        p.add(f'<path d="M{x} {y - 6} l5 6 l-5 6 l-5 -6 Z" fill="{c}"/>')
    sat(p, 330, 70, 1.2)
    p.add('<path d="M320 80 L150 96 M330 82 L170 120" stroke="#5cc8ff" stroke-opacity=".35"/>')
    label(p, 440, 130, "+1 DEPOSIT", 12, "#ffd27a", .85, "middle")


def s_deep_boring(p):
    ground(p, 90, "ground", 0)
    p.add('<rect x="270" y="90" width="20" height="100" fill="#15120e" stroke="#6b5e4e"/>')
    tower(p, 280, 60, 30)
    p.add('<path d="M270 186 l10 6 l10 -6" fill="#c3cddc"/>')
    for (x, y) in [(200, 150), (360, 170)]:
        p.add(f'<path d="M{x} {y - 6} l6 6 l-6 6 l-6 -6 Z" fill="#ffd27a"/>')


def s_efficient_upkeep(p):
    hull(p, 60, 100, 300, 28)
    station(p, 460, 96, .6, panels=False)
    label(p, 210, 160, "−25 % upkeep", 12, "#9be3bd", .9, "middle")


def s_fast_shipyard(p):
    p.add('<rect x="80" y="40" width="400" height="8" fill="url(#dk)"/><rect x="80" y="144" width="400" height="8" fill="url(#dk)"/>')
    for k in range(6):
        p.add(f'<rect x="{90 + k * 76}" y="40" width="6" height="112" fill="url(#dk)"/>')
    hull(p, 120, 96, 320, 34, radiators=False, windows=False)
    for k in range(3):
        flash(p, 200 + k * 90, 80, 8)
    label(p, 280, 176, "+30 %", 11, "#ffd27a", .8, "middle")


def s_breeder_reactors(p):
    ground(p, 150, "ground", 4)
    for x in (180, 280):
        y = ground_y(x, 150, 4)
        p.add(f'<path d="M{x - 30} {y} Q{x - 20} {y - 40} {x - 22} {y - 70} H{x + 22} Q{x + 20} {y - 40} {x + 30} {y} Z" fill="url(#dk)" stroke="#8a96ab"/>')
    glow(p, 420, 100, 36, "glow")
    p.add('<circle cx="420" cy="100" r="8" fill="#bfeaff"/>')
    for a in (0, 120, 240):
        p.add(f'<ellipse cx="420" cy="100" rx="30" ry="10" transform="rotate({a} 420 100)" fill="none" stroke="#5cc8ff" stroke-opacity=".6"/>')


def s_deuterium_extraction(p):
    ground(p, 150, "groundi", 4)
    block(p, 120, 80, 36, chimney=True)
    for k in range(3):
        p.add(f'<rect x="{260 + k * 40}" y="{ground_y(280 + k * 40, 150, 4) - 50}" width="30" height="50" rx="14" fill="url(#tk)" stroke="#d6f3ff" stroke-opacity=".5"/>')
    label(p, 470, 80, "D₂", 22, "#9fd8f5", .85, "middle")


def s_electronics_fabs(p):
    block(p, 60, 160, 70, y=160)
    chip(p, 360, 90, 1.4)
    chip(p, 470, 70, .9)
    chip(p, 470, 130, .9)


def s_orbital_docks(p):
    planet(p, 60, 170, 120, "earth", atmo="#9fd8f5")
    p.add('<rect x="220" y="50" width="300" height="6" fill="url(#dk)"/><rect x="220" y="140" width="300" height="6" fill="url(#dk)"/>')
    for k in range(5):
        p.add(f'<rect x="{230 + k * 70}" y="50" width="5" height="96" fill="url(#dk)"/>')
    hull(p, 250, 98, 250, 28, radiators=False)
    flash(p, 340, 84, 7)


def s_recycling(p):
    for k in range(3):
        a = k * 120
        r = math.radians(a)
        x = 280 + math.cos(r) * 60
        y = 96 + math.sin(r) * 50
        p.add(f'<path d="M{x:.0f} {y:.0f} A60 50 0 0 1 {280 + math.cos(r + 1.6) * 60:.0f} {96 + math.sin(r + 1.6) * 50:.0f}" fill="none" stroke="#9be3bd" stroke-width="5"/>')
    crate_stack(p, 250, 120, 4, 2)
    label(p, 280, 180, "−30 % parts", 11, "#9be3bd", .85, "middle")


def s_colony_shipyards(p):
    ground(p, 160, "groundg", 6)
    p.add('<rect x="150" y="60" width="260" height="5" fill="url(#dk)"/>')
    tower(p, 150, 100, 12, top_light=True)
    tower(p, 410, 100, 12)
    hull(p, 180, 110, 210, 26, radiators=False, windows=False)


def s_fusion_power(p):
    ground(p, 150, "ground", 4)
    y = ground_y(280, 150, 4)
    p.add(f'<ellipse cx="280" cy="{y - 40}" rx="70" ry="40" fill="url(#dk)" stroke="#8a96ab"/>')
    p.add(f'<ellipse cx="280" cy="{y - 40}" rx="42" ry="14" fill="none" stroke="#bfeaff" stroke-width="5" stroke-opacity=".8"/>')
    glow(p, 280, y - 40, 40, "glow")
    label(p, 280, 36, "POWER 40", 12, "#bfeaff", .85, "middle")


def s_helium_3_skimming(p):
    planet(p, 280, 300, 230, "gas")
    hull(p, 160, 70, 160, 20, tanks=2)
    p.add('<path d="M250 81 Q260 100 255 120" stroke="#f2d8a8" stroke-width="3" stroke-opacity=".6" fill="none"/>')
    label(p, 470, 50, "He-3", 18, "#f2d8a8", .85, "middle")


def s_mass_driver(p):
    planet(p, 70, 150, 90, "rock")
    p.add('<path d="M120 90 L300 40" stroke="url(#hs)" stroke-width="8"/>')
    for k in range(6):
        p.add(f'<rect x="{140 + k * 28}" y="{82 - k * 7.6:.1f}" width="6" height="14" transform="rotate(-15 {143 + k * 28} {89 - k * 7.6:.1f})" fill="#b87333"/>')
    crate_stack(p, 380, 40, 1, 1)
    arc_path(p, "M310 38 Q420 20 500 100", "#ffd27a", 1, "3 4")
    planet(p, 510, 120, 26, "ice")


def s_helium_3_reactors(p):
    s_fusion_power(p)
    label(p, 460, 150, "He-3 +50 %", 12, "#f2d8a8", .85, "middle")


def s_power_beaming(p):
    planet(p, 60, 96, 60, "hot")
    planet(p, 500, 96, 50, "ice")
    station(p, 160, 60, .35, ring=False)
    beam(p, 180, 60, 450, 90, "#ffd27a", 2.4, 12)


def s_space_elevator(p):
    planet(p, 280, 330, 210, "earth", atmo="#9fd8f5")
    p.add('<path d="M280 120 L280 20" stroke="#c3cddc" stroke-width="2"/>')
    station(p, 280, 20, .35, ring=False)
    p.add('<rect x="274" y="70" width="12" height="10" fill="#ffd27a"/>')


def s_skyhook(p):
    planet(p, 280, 320, 200, "rock")
    p.add('<path d="M180 30 L380 150" stroke="#c3cddc" stroke-width="2.4"/>')
    station(p, 280, 90, .3, ring=False)
    arc_path(p, "M120 170 Q140 120 180 140 Q240 160 370 145", "#ffd27a", 1, "3 3")
    p.add('<circle cx="280" cy="90" r="120" fill="none" stroke="#5cc8ff" stroke-opacity=".2" stroke-dasharray="4 6"/>')


def s_self_replicating_miners(p):
    for k in range(8):
        rock(p, 60 + k * 64, 60 + (k % 3) * 40, p.rng.uniform(14, 24))
    for (x, y) in [(120, 120), (240, 70), (360, 140), (470, 80)]:
        drone(p, x, y, 1.4, "#ffd27a")
        drone(p, x + 20, y + 14, .9, "#ffd27a")


def s_food_recyclers(p):
    hull(p, 60, 96, 420, 34)
    p.add('<rect x="200" y="84" width="140" height="24" rx="4" fill="#3a7a5a" fill-opacity=".8" stroke="#9be3bd"/>')
    for k in range(10):
        p.add(f'<path d="M{208 + k * 13} 104 q4 -14 8 0" stroke="#9be3bd" fill="none"/>')


def s_public_services(p):
    ground(p, 150, "groundg", 6)
    block(p, 180, 120, 40)
    p.add(f'<rect x="234" y="{ground_y(240) - 34}" width="12" height="4" fill="#ff5c3c"/><rect x="238" y="{ground_y(240) - 38}" width="4" height="12" fill="#ff5c3c"/>')
    dome(p, 400, 30)
    person(p, 120, ground_y(120) - 2, 1.2)
    person(p, 140, ground_y(140) - 2, 1.0)


def s_scientist_training(p):
    ground(p, 150, "ground", 4)
    block(p, 140, 120, 46)
    dish(p, 370, ground_y(370, 150, 4), 20)
    for k in range(3):
        person(p, 450 + k * 22, ground_y(460, 150, 4) - 2, 1.1, "#e6ecf5")


def s_sealed_habitats(p):
    ground(p, 150, "ground", 6)
    dome(p, 160, 46, "#7fe0a8")
    dome(p, 300, 36, "#7fe0a8")
    dome(p, 420, 50, "#9fd8f5")
    pipes(p, 206, 264, ground_y(230) - 6, 1)


def s_radiation_shielding(p):
    sun(p, 40, 50, 24)
    for k in range(6):
        p.add(f'<path d="M80 {40 + k * 18} L300 {40 + k * 20}" stroke="#ffd27a" stroke-opacity=".25" stroke-width="2"/>')
    p.add('<path d="M300 30 Q330 96 300 162" stroke="#cfd8e6" stroke-width="6" fill="none"/>')
    hull(p, 330, 100, 200, 26)


def s_acceleration_couches(p):
    hull(p, 160, 96, 340, 30, burn=140)
    for k in range(3):
        p.add(f'<path d="M{340 + k * 26} 96 q6 -12 14 -12" stroke="#ffe2a8" stroke-width="2" fill="none"/>')
    label(p, 330, 150, "3 g", 14, "#ffe2a8", .9, "middle")


def s_closed_loop(p):
    hull(p, 60, 96, 420, 34)
    arc_path(p, "M240 60 A40 30 0 1 1 239 60", "#9be3bd", 3)
    label(p, 280, 160, "+50 % days", 11, "#9be3bd", .85, "middle")


def s_food_vats(p):
    ground(p, 150, "ground", 4)
    for k in range(4):
        x = 120 + k * 90
        y = ground_y(x + 25, 150, 4)
        p.add(f'<rect x="{x}" y="{y - 60}" width="50" height="60" rx="8" fill="#3a7a5a" fill-opacity=".7" stroke="#9be3bd"/>'
              f'<rect x="{x + 6}" y="{y - 40}" width="38" height="34" rx="4" fill="#7fe0a8" fill-opacity=".4"/>')
    pipes(p, 100, 470, ground_y(280, 150, 4) - 66, 1)


def s_low_g_medicine(p):
    rock(p, 280, 110, 70, "#7a6a58")
    dome(p, 270, 18, y=48)
    person(p, 200, 60, 1.3)
    p.add('<path d="M195 70 q5 -20 10 0" stroke="#9be3bd" fill="none"/>')


def s_marines(p):
    ground(p, 160, "ground", 4)
    for k in range(6):
        person(p, 120 + k * 50, ground_y(120 + k * 50, 160, 4) - 4, 1.5, "#b8a88a")
    p.add('<path d="M440 40 L500 120" stroke="url(#exhw)" stroke-width="10"/>')
    pod(p, 440, 40, 2, light=False)


def s_cryo_sleep(p):
    for k in range(5):
        x = 70 + k * 96
        p.add(f'<rect x="{x}" y="60" width="80" height="72" rx="30" fill="url(#tk)" stroke="#d6f3ff" stroke-opacity=".6"/>')
        person(p, x + 40, 112, 1.1, "#e6f6ff")
    label(p, 280, 170, "×2 people", 12, "#9fd8f5", .85, "middle")


def s_hospital_ships(p):
    hull(p, 60, 100, 420, 36)
    p.add('<rect x="270" y="88" width="24" height="8" fill="#ff5c3c"/><rect x="278" y="80" width="8" height="24" fill="#ff5c3c"/>')
    pod(p, 520, 60, 1.4)


def s_underground_habitats(p):
    ground(p, 70, "ground", 0)
    p.add('<path d="M100 70 L180 20 L240 70 Z M300 70 L380 30 L460 70 Z" fill="#4a3e32"/>')
    for k in range(3):
        y = 110 + k * 26
        p.add(f'<rect x="{150 + k * 40}" y="{y}" width="{220 - k * 30}" height="16" rx="8" fill="#15120e" stroke="#6b5e4e"/>')
        for j in range(6 - k):
            p.add(f'<rect x="{164 + k * 40 + j * 30}" y="{y + 6}" width="4" height="3" fill="#ffe2a8"/>')


def s_arcologies(p):
    ground(p, 160, "groundg", 4)
    y = ground_y(280, 160, 4)
    p.add(f'<path d="M180 {y} L240 {y - 130} L320 {y - 130} L380 {y} Z" fill="url(#dk)" stroke="#8a96ab"/>')
    for r in range(12):
        for k in range(10 - abs(r - 6) // 2):
            if (r + k) % 3:
                p.add(f'<rect x="{230 + k * 10 - r * 0.0}" y="{y - 120 + r * 9}" width="4" height="3" fill="#ffe2a8"/>')
    dome(p, 280, 30, "#7fe0a8", y=y - 130, lights=False)


def s_g_softening(p):
    hull(p, 120, 96, 340, 30, burn=100)
    bubble(p, 340, 96, 80, 26, "#c08cff", .6)
    person(p, 340, 104, .8, "#f0dcff")


def s_terraforming(p):
    p.add('<defs><clipPath id="half"><rect x="280" y="0" width="300" height="192"/></clipPath></defs>')
    planet(p, 280, 96, 80, "rock")
    p.add('<g clip-path="url(#half)">')
    planet(p, 280, 96, 80, "green", atmo="#9fd8f5")
    p.add('</g>')
    station(p, 470, 40, .3, ring=False)
    beam(p, 450, 44, 340, 80, "#9be3bd", 1.2, 6)


SCENES = {k[2:].replace("_", "-"): v for k, v in globals().items() if k.startswith("s_") and callable(v)}

# Ideas whose older shared drawing is exactly right: left alone.
KEEP = {"escort-hull", "tanker-hull", "cruiser-hull", "freighter-hull"}


def main():
    only = None
    if "--only" in sys.argv:
        only = set(sys.argv[sys.argv.index("--only") + 1].split(","))
    data = json.load(open(os.path.join("src", "Lightlag.Sim", "Data", "economy.json")))
    ids = [r["id"] for r in data["research"]]
    missing = [i for i in ids if i not in SCENES and i not in KEEP]
    made = 0
    for i in ids:
        if i in KEEP or i not in SCENES or (only and i not in only):
            continue
        p = Pic(i)
        sky(p)
        SCENES[i](p)
        with open(os.path.join(OUT, i + ".svg"), "w") as fh:
            fh.write(p.svg())
        made += 1
    print(f"drew {made} pictures; {len(missing)} ideas without a scene (they keep their shared picture): {', '.join(missing)}")


if __name__ == "__main__":
    main()
