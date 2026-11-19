#!/usr/bin/env python3
"""
Paints the idea card pictures: game/Art/Ideas/<idea id>.webp, 1120 x 384 (twice the card's 560 x 192, so they
stay sharp in big views). Each idea is a short scene put together from the kit in painter.py: realistic hard
sci-fi, dark space, side-on metal hulls, orange radiators, pale blue tanks, small warm lights, glow and bloom.

Run from the repo root (needs numpy and Pillow):
    python3 tools/idea-art/paint_ideas.py              all ideas
    python3 tools/idea-art/paint_ideas.py --only a,b   just these
    python3 tools/idea-art/paint_ideas.py --sheet x.png   also a contact sheet of what was painted
A new idea in economy.json needs a scene here (def s_<id with _ for ->); until then the game shows the older
SVG drawing (make_ideas.py) or the shared picture.
"""
import json, math, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from painter import *

OUT = os.path.join("game", "Art", "Ideas")
PI = math.pi


# ---- Backdrops -------------------------------------------------------------------------------------

def space(c, neb=("#3a2a6a", "#1a4a6a"), amt=.45, stars=1400):
    c.sky(neb=neb, neb_amt=amt, stars=stars)


def surface(c, horizon=300, ground="#1a1612", top="#6a6258", haze="#8aa0c0", neb=("#2a3a6a", "#4a2a5a"), body=None):
    c.sky(tint=("#121828", "#06080e"), neb=neb, neb_amt=.3, stars=900)
    if body:
        body()
    c.ground(horizon, kind=ground, curve=-10, top=top, haze=haze)


# ---- Drives and hulls -------------------------------------------------------------------------------

def s_efficient_drives(c):
    space(c, ("#1a3a6a", "#2a1a4a"))
    c.planet(980, 520, 300, "rust", light=(-.4, -.8, .4), atmo="#ffb08a", atmo_w=.015)
    a = ship(c, "frigate", 380, 190, 1.1, plume="#9fd8ff", plume_len=1.3)
    # fuel gauge style: thin efficient blue cone, long and narrow
    c.glow_dot(380, 190, 4, "#bfeaff", .8)


def s_engine_mk2(c):
    space(c, ("#1a3a6a", "#2a1a4a"), .4)
    c.planet(300, 620, 420, "moon", light=(.5, -.8, .3), atmo=None)
    ship(c, "cruiser", 470, 176, 1.0, plume="#bfe6ff", plume_len=1.4, heat=.9)


def s_engine_mk3(c):
    space(c, ("#2a2a6a", "#1a4a5a"))
    c.planet(160, 100, 70, "ice", light=(.6, .2, .6), atmo="#bfe6ff", atmo_w=.05)
    ship(c, "cruiser", 520, 200, 1.05, plume="#d8ecff", plume_len=1.8, heat=1.0)
    c.glow_dot(470, 200, 30, "#9fd8ff", .25)


def s_engine_mk4(c):
    space(c, ("#3a1a5a", "#1a3a6a"), .55)
    ship(c, "cruiser", 520, 200, 1.05, plume="#e8d8ff", plume_len=2.0, heat=1.0)
    c.glow_dot(460, 200, 40, "#c8a8ff", .35)
    c.planet(1010, 330, 120, "gas", light=(-.7, -.4, .5), atmo="#ffd8a0", atmo_w=.03)


def s_engine_mk5(c):
    space(c, ("#3a1a5a", "#5a2a2a"), .55)
    c.planet(900, 470, 300, "gas", light=(-.5, -.6, .5), atmo="#ffd8a0", atmo_w=.03, rot=2)
    ship(c, "capital", 380, 170, .95, plume="#f0e8ff", plume_len=2.2, heat=1.0)
    c.glow_dot(330, 170, 55, "#d0b8ff", .35)


def s_antimatter_drive(c):
    space(c, ("#4a1a6a", "#1a1a4a"), .6)
    ship(c, "cruiser", 600, 190, 1.0, plume="#d8a8ff", plume_len=2.4, heat=1.0, tank=True)
    c.glow_dot(560, 190, 70, "#a56bff", .5)
    c.glow_dot(560, 190, 16, "#ffffff", 1.4)
    for k in range(5):
        c.ellipse((300 - k * 60, 150 - k * 8, 330 - k * 60, 230 + k * 8), glow="#c08aff", s=.05)


def s_escort_hull(c):
    space(c, ("#1a3a6a", "#3a2a5a"))
    c.planet(160, 330, 150, "earth", light=(.6, -.6, .5), atmo="#7fc0ff", atmo_w=.03, rot=2)
    ship(c, "cruiser", 520, 230, .7, plume="#9fd8ff", plume_len=.6)
    ship(c, "escort", 690, 120, 1.0, plume="#9fd8ff", plume_len=.7)
    ship(c, "escort", 640, 320, .9, plume="#9fd8ff", plume_len=.7, seed=2)


def s_tanker_hull(c):
    space(c, ("#1a4a6a", "#2a2a5a"))
    c.planet(980, 90, 60, "gas", light=(-.7, .2, .6), atmo="#ffd8a0", atmo_w=.03)
    ship(c, "tanker", 330, 200, 1.15, plume="#9fd8ff", plume_len=.8)


def s_cruiser_hull(c):
    space(c, ("#2a2a5a", "#1a3a5a"))
    c.planet(1000, 420, 230, "rust", light=(-.6, -.6, .5), atmo="#ffb08a", atmo_w=.015)
    ship(c, "cruiser", 300, 190, 1.25, plume="#9fd8ff", plume_len=.8)


def s_freighter_hull(c):
    space(c, ("#1a3a5a", "#3a3a4a"))
    c.planet(170, 470, 320, "earth", light=(.4, -.8, .4), atmo="#7fc0ff", atmo_w=.012, rot=.4, night_lights=True)
    ship(c, "freighter", 420, 170, 1.2, plume="#9fd8ff", plume_len=.7)


def s_bulk_freighter_hull(c):
    space(c, ("#2a3a5a", "#3a2a3a"))
    ship(c, "bulk", 260, 200, 1.25, plume="#9fd8ff", plume_len=.6)
    c.planet(1060, 80, 50, "moon", light=(-.5, .2, .6), atmo=None)


def s_large_tanks(c):
    space(c, ("#1a4a6a", "#2a2a4a"))
    ship(c, "frigate", 330, 200, 1.4, plume="#9fd8ff", plume_len=.7, tank=True)
    for k in range(2):
        tx = 600 + k * 90
        c.ellipse((tx, 140, tx + 80, 260), "#9fd8f5")
        m = c.mask(lambda d, s, tx=tx: d.ellipse([tx * s, 140 * s, (tx + 80) * s, 260 * s], fill=255))
        v = np.clip((c.y - 140) / 120, 0, 1)
        c.over(col("#e0f4ff") * (1.05 - v[..., None]) + col("#1f4b63") * v[..., None], m)


def s_quiet_drives(c):
    space(c, ("#1a2a4a", "#2a1a3a"), .5, 1800)
    ship(c, "frigate", 420, 200, 1.3, plume="#5a7a9a", plume_len=.35, heat=.15, dark=False, base="#5a6474")
    waves(c, 380, 200, 60, 6, 30, "#5a7a9a", PI, .5, .12)


def s_gravity_assist(c):
    space(c, ("#2a2a5a", "#3a2a4a"))
    c.planet(560, 420, 210, "gas", light=(-.4, -.8, .4), atmo="#ffd8a0", atmo_w=.03, rot=1.4)
    pts = [(80 + t * 9.6, 120 + 140 * math.sin(t / 100 * PI) ** 2 * 0 + (t - 50) ** 2 * .035) for t in range(101)]
    dashed(c, pts[0], pts[40], "#7cc4ff")
    for i in range(40, 99, 3):
        c.line(pts[i], pts[i + 2], 1.6, "#7cc4ff", glow=True, s=.6)
    ship(c, "frigate", 860, 92, .55, plume="#9fd8ff", plume_len=.6)


def s_orbital_tugs(c):
    space(c, ("#1a3a5a", "#2a2a4a"))
    ship(c, "frigate", 520, 200, 1.0, base="#6a6458", lights=False, seed=4)
    debris(c, 760, 220, 40, 25)
    explosion(c, 700, 190, 10, sparks=4)
    ship(c, "escort", 160, 200, .8, plume="#9fd8ff", plume_len=.6, seed=9)
    c.line((350, 200), (520, 200), 1.6, "#9aa4b2")


def s_picket_hull(c):
    space(c, ("#1a4a5a", "#3a2a5a"))
    a = ship(c, "picket", 380, 220, 1.4, plume="#9fd8ff", plume_len=.6)
    waves(c, a["dish"][0], a["dish"][1], 30, 6, 30, "#5cff9a", -1.2, .5, .25)


def s_solar_sails(c):
    space(c, ("#3a2a5a", "#1a3a5a"))
    c.sun(70, 80, 18, strength=1.2)
    pts = [(560, 40), (900, 80), (860, 330), (520, 300)]
    m = c.poly(pts, "#cfd8e6", .9)
    v = np.clip(((c.x - 520) / 380 + (c.y - 40) / 290) / 2, 0, 1)
    wr = noise(H, W, 40, 5, 4)
    c.over(col("#fff6dc") * (1 - v[..., None]) + col("#4a5668") * v[..., None], m * .92)
    c.add(col("#fff2d0"), m * (wr - .5) * .08, bloom=False)
    for k in range(1, 6):
        t = k / 6
        c.line((560 + 340 * t, 40 + 40 * t), (520 + 340 * t, 300 + 30 * t), .7, "#8a94a4", alpha=.5)
        c.line((560 - 40 * t, 40 + 260 * t), (900 - 40 * t, 80 + 250 * t), .7, "#8a94a4", alpha=.5)
    for p in pts:
        c.line(p, (420, 190), .8, "#c8ccd2", alpha=.7)
    ship(c, "courier", 320, 190, 1.2)


def s_underway_replenishment(c):
    space(c, ("#1a3a6a", "#2a2a4a"))
    ship(c, "tanker", 300, 120, .9, plume="#9fd8ff", plume_len=.5)
    ship(c, "cruiser", 360, 290, .95, plume="#9fd8ff", plume_len=.5, seed=3)
    c.line((520, 140), (560, 262), 2, "#c8ccd2")
    c.line((640, 140), (690, 262), 2, "#c8ccd2")
    c.glow_dot(560, 262, 3, "#ffd28a", .8)
    c.glow_dot(690, 262, 3, "#ffd28a", .8)


def s_carrier_hull(c):
    space(c, ("#2a2a5a", "#1a3a5a"))
    a = ship(c, "carrier", 220, 200, 1.1, plume="#9fd8ff", plume_len=.5)
    bx, by = a["bay"]
    for k in range(4):
        fx, fy = bx + 80 + k * 70, by + 40 - k * 30
        c.poly([(fx, fy), (fx + 22, fy + 4), (fx, fy + 8)], "#c8ccd2")
        c.plume(fx, fy + 4, 40, 4, "#9fd8ff", .8)


def s_compact_engineering(c):
    space(c, ("#3a2a4a", "#1a3a5a"))
    ship(c, "frigate", 240, 200, 1.2, plume="#9fd8ff", plume_len=.4, heat=1.2)
    ship(c, "escort", 760, 200, 1.4, plume="#9fd8ff", plume_len=.4, heat=1.4, seed=5)
    c.glow_dot(850, 200, 60, "#ff6a2a", .15)


def s_siege_monitor_hull(c):
    space(c, ("#3a2a3a", "#2a2a5a"))
    c.planet(1080, 220, 160, "rust", light=(-.8, -.3, .5), atmo="#ffb08a", atmo_w=.02)
    ship(c, "monitor", 180, 200, 1.05, plume="#9fd8ff", plume_len=.4)


def s_slingshot_chains(c):
    space(c, ("#2a2a5a", "#1a3a4a"))
    c.planet(330, 300, 90, "gas", light=(-.3, -.8, .4), atmo="#ffd8a0", atmo_w=.03)
    c.planet(780, 120, 40, "moon", light=(-.4, .4, .6), atmo=None)
    c.planet(560, 250, 26, "ice", light=(-.4, .4, .6), atmo=None, seed=7)
    pts = []
    for t in np.linspace(0, 1, 120):
        x = 40 + t * 1060
        y = 330 - 60 * math.sin(t * PI * 2.2) - t * 150
        pts.append((x, y))
    for i in range(0, 118, 2):
        c.line(pts[i], pts[i + 2], 1.6, "#7cc4ff", glow=True, s=.5)
    ship(c, "frigate", pts[-12][0] - 80, pts[-12][1], .35, plume="#9fd8ff", plume_len=.5)


def s_capital_hull(c):
    space(c, ("#2a2a5a", "#3a1a3a"), .5)
    c.planet(1050, 70, 40, "moon", light=(-.5, .2, .6), atmo=None)
    ship(c, "capital", 180, 200, 1.25, plume="#bfe6ff", plume_len=.4)


def s_laser_sails(c):
    space(c, ("#1a3a6a", "#3a2a5a"))
    c.planet(160, 330, 160, "earth", light=(.6, -.6, .5), atmo="#7fc0ff", atmo_w=.03)
    station(c, 260, 140, 40, ring=False)
    pts = [(820, 80), (1010, 110), (990, 300), (800, 280)]
    m = c.poly(pts, "#cfd8e6", .9)
    c.add(col("#9fd8ff"), m * .2)
    beam(c, (300, 140), (820, 190), "#7fd0ff", 2.5)
    ship(c, "courier", 700, 190, 1.0)


def s_cryo_sleep(c):
    c.img[:] = col("#05070c")
    vx, vy = 560, 180
    X, Y = c.x, c.y
    for side in (-1, 1):
        for i in range(9):
            z0, z1 = 1 + i * .9, 1 + (i + 1) * .9
            def P(z, yy):
                return (vx + side * 520 / z, vy + yy / z)
            pz, pw = z0 + .15, .65
            c.poly([P(z0, -150), P(z1, -150), P(z1, 150), P(z0, 150)], "#1a2028" if i % 2 else "#161b22")
            pod = []
            for k in range(25):
                t = k / 24 * PI
                pod.append(P(pz + pw / 2 - math.cos(t) * pw / 2, -105 - math.sin(t) * 18))
            for k in range(25):
                t = k / 24 * PI
                pod.append(P(pz + pw / 2 + math.cos(t) * pw / 2, 105 + math.sin(t) * 18))
            fade = 1 / (1 + i * .45)
            m = c.poly(pod, "#06141c", .95)
            mi = c.blur(m, 6 / pz + 1)
            fr = c.blur(noise(H, W, 5, 300 + i * 2 + side, 3), 2.5)
            vgrad = ss(Y, P(pz, -105)[1], P(pz, 105)[1])
            c.add(col("#3aa8e0"), m * mi * (.10 + .22 * vgrad) * fade)
            c.add(col("#cdefff"), m * ss(fr, .68, .9) * (1 - mi * .7) * .22 * fade, bloom=False)
            c.add(col("#9fdcff"), np.clip(m - c.blur(m, 1.2), 0, 1) * .6 * fade)
            hx, hy = P(pz + pw * .5, -60)
            c.poly([P(pz + pw * .32, -40), P(pz + pw * .68, -40), P(pz + pw * .62, 80), P(pz + pw * .38, 80)], "#020a10", .5 * fade, blur=6)
            hr = 36 / pz
            c.ellipse((hx - hr * .55, hy - hr * .8, hx + hr * .55, hy + hr * .2), "#020a10", .5 * fade)
            lx, ly = P(pz + pw * .5, 125)
            c.glow_dot(lx, ly, 4 / pz * 3, "#5cff9a" if (i + side) % 4 else "#ffb860", .7 * fade)
    c.poly([(0, 384), (1120, 384), (vx + 30, vy + 20), (vx - 30, vy + 20)], "#0c1016", .9)
    c.poly([(0, 0), (1120, 0), (vx + 30, vy - 20), (vx - 30, vy - 20)], "#0a0d12", .9)
    for i in range(10):
        z = 1 + i * .9
        c.line((vx - 120 / z, vy - 150 / z), (vx + 120 / z, vy - 150 / z), 3 / z * 2, "#bfe6ff", glow=True, s=.7 / (1 + i * .3))
    c.add(col("#6fcfff"), noise(H, W, 90, 5, 5) * ss(Y, 200, 384) * .06, bloom=False)
    c.glow_dot(vx, vy, 60, "#9fd8ff", .15)



# ---- Weapons ---------------------------------------------------------------------------------------

def rival(c, kind="frigate", x=760, y=200, scale=1.0, seed=50):
    """An enemy ship facing left (drawn mirrored by placing it nose-left with a darker, warmer hull)."""
    L = SHIPS[kind][0] * scale
    # mirror: draw into a temp canvas and flip
    t = Canvas(c.seed + seed)
    t.img[:] = 0
    a = ship(t, kind, W - x - L, y, scale, plume="#ffb07a", plume_len=.6, base="#a89a88", seed=seed)
    alpha = np.clip(t.img.max(-1) * 4, 0, 1)
    c.img = c.img * (1 - alpha[:, ::-1, None]) + t.img[:, ::-1]
    c.glow += t.glow[:, ::-1]
    bow = (W - a["bow"][0], a["bow"][1])
    return {"bow": bow, "mid": (x + L * .5, y), "stern": (x + L, y)}


def s_fast_railguns(c):
    space(c, ("#2a2a5a", "#1a3a5a"))
    a = ship(c, "frigate", 140, 200, 1.1, plume="#9fd8ff", plume_len=.4)
    for k, dy in enumerate((-30, 0, 30)):
        streak(c, (560 + k * 120, 200 + dy * .2), (640 + k * 120, 200 + dy * .2), "#bfeaff", 1.6)
    c.glow_dot(a["bow"][0] + 6, 200, 14, "#bfeaff", .8)
    rival(c, "frigate", 900, 200, .8)


def s_capacitor_banks(c):
    space(c, ("#1a3a6a", "#2a2a4a"))
    ship(c, "cruiser", 200, 200, 1.2, plume="#9fd8ff", plume_len=.3)
    for k in range(6):
        x = 420 + k * 34
        c.hull([(x, 170), (x + 26, 170), (x + 26, 230), (x, 230)], 170, 230, base="#4a5a6a", panel=30)
        c.add(col("#7fd0ff"), c.mask(lambda d, s, x=x: d.rectangle([(x + 4) * s, (176 + k * 3) * s, (x + 22) * s, 224 * s], fill=255)) * .5)
    for k in range(10):
        x0 = 420 + k * 22
        c.line((x0, 166), (x0 + 12, 150 - (k % 3) * 8), 1, "#bfeaff", glow=True, s=.6)


def s_missile_brain_2(c):
    space(c, ("#2a1a4a", "#1a3a5a"))
    rival(c, "cruiser", 760, 220, .8)
    for k, (x, y, cu) in enumerate(((560, 150, 40), (520, 230, -50), (470, 300, 30))):
        missile(c, x, y, 0, 1.2, 220, curve=cu)
    tracers(c, 760, 220, 520, 230, 10, seed=1, spread=40)


def s_missile_brain_3(c):
    space(c, ("#3a1a4a", "#1a2a5a"))
    rival(c, "cruiser", 760, 200, .8)
    pts = []
    for k in range(5):
        x0, y0 = 300 + k * 50, 120 + k * 40
        missile(c, x0 + 220, y0 + 10 * math.sin(k), .1 * math.sin(k * 2), 1.1, 260, curve=40 * (-1) ** k)
    tracers(c, 760, 200, 560, 200, 14, seed=2, spread=60)


def s_missile_brain_4(c):
    space(c, ("#4a1a3a", "#1a2a5a"))
    rival(c, "capital", 700, 200, .6)
    for k in range(6):
        ang = (k - 2.5) * .12
        missile(c, 560 + abs(k - 2.5) * 20, 200 + (k - 2.5) * 34, ang, 1.0, 260, curve=30 * (-1) ** k)
    tracers(c, 700, 200, 560, 200, 12, seed=3, spread=60)
    explosion(c, 640, 120, 14, seed=2, sparks=10)


def s_missile_brain_5(c):
    space(c, ("#4a1a3a", "#3a1a5a"), .55)
    rival(c, "cruiser", 760, 140, .7)
    debris(c, 900, 300, 30, 20)
    explosion(c, 900, 300, 26, seed=3)
    # a missile that missed turns round and hunts the cruiser
    pts = [(500 + 300 * math.sin(t), 320 - 200 * (1 - math.cos(t)) * .5) for t in np.linspace(0, PI * .9, 30)]
    trail(c, pts, "#9aa8bc", .3)
    missile(c, 720, 170, -.3, 1.2, 60)


def s_railgun_mk3(c):
    space(c, ("#2a2a5a", "#3a1a2a"))
    ship(c, "cruiser", 120, 200, 1.0, plume="#9fd8ff", plume_len=.3)
    r = rival(c, "cruiser", 760, 200, .75)
    streak(c, (640, 200), (790, 200), "#ffffff", 2.2)
    explosion(c, 790, 200, 26, seed=1)


def s_railgun_mk4(c):
    space(c, ("#2a2a5a", "#1a2a4a"))
    ship(c, "cruiser", 80, 200, .9, plume="#9fd8ff", plume_len=.3)
    for k in range(3):
        streak(c, (520 + k * 180, 200), (680 + k * 180, 200), "#d8f0ff", 1.8, s=1 - k * .2)
    rival(c, "escort", 1010, 196, .6)


def s_railgun_mk5(c):
    space(c, ("#3a2a3a", "#2a2a5a"))
    ship(c, "monitor", 60, 200, .95, plume="#9fd8ff", plume_len=.3)
    c.glow_dot(645, 200, 30, "#bfeaff", 1.0)
    streak(c, (660, 200), (960, 200), "#ffffff", 3.4)
    rival(c, "capital", 960, 200, .5)
    explosion(c, 965, 200, 40, seed=4)


def s_deep_magazines(c):
    space(c, ("#1a2a4a", "#2a2a3a"))
    ship(c, "frigate", 120, 300, .9, plume=None)
    for row in range(3):
        for k in range(10):
            x, y = 420 + k * 56, 80 + row * 58
            c.hull([(x, y), (x + 40, y), (x + 50, y + 6), (x + 40, y + 12), (x, y + 12)], y, y + 12, base="#c8ccd2", panel=40)
            c.poly([(x + 40, y), (x + 50, y + 6), (x + 40, y + 12)], "#c08a4a")


def s_heavy_warheads(c):
    space(c, ("#3a1a2a", "#2a2a4a"))
    x, y = 360, 190
    c.hull([(x, y - 22), (x + 380, y - 22), (x + 440, y), (x + 380, y + 22), (x, y + 22)], y - 22, y + 22, base="#c8ccd2", panel=60)
    c.hull([(x + 300, y - 22), (x + 380, y - 22), (x + 440, y), (x + 380, y + 22), (x + 300, y + 22)], y - 22, y + 22, base="#c0603a", panel=200)
    for k in (-1, 1):
        c.poly([(x, y + k * 22), (x + 50, y + k * 22), (x + 10, y + k * 52), (x - 10, y + k * 52)], "#8a94a4")
    c.plume(x, y, 260, 30, "#ffd08a", 1.0)


def s_precision_strikes(c):
    space(c, ("#1a2a3a", "#2a1a3a"), .3)
    c.planet(560, 980, 820, "rust", light=(.2, -.9, .3), atmo="#ffb08a", atmo_w=.01)
    for k, (x, y) in enumerate(((420, 200), (600, 210), (760, 196))):
        c.line((x - 40, y - 140), (x, y - 10), 1.2, "#ffd08a", glow=True, s=.7)
        c.glow_dot(x, y - 10, 6, "#ffb060", 1.0)
        c.ellipse((x - 26, y - 6, x + 26, y + 8), None, glow="#ff4a3a", s=.25)
    ship(c, "frigate", 140, 60, .6)


def s_special_ammo(c):
    space(c, ("#2a2a3a", "#1a2a4a"))
    kinds = [("#8a8e96", "Tungsten"), ("#c8a060", "Flechette"), ("#c06040", "Shaped")]
    for k, (cc, _) in enumerate(kinds):
        x = 270 + k * 230
        c.hull([(x, 120), (x + 120, 120), (x + 120, 270), (x, 270)], 120, 270, base="#4a5260", panel=40)
        for j in range(3):
            sx = x + 18 + j * 34
            c.hull([(sx, 150), (sx + 20, 150), (sx + 20, 250), (sx, 250)], 150, 250, base=cc, panel=200, spec=.9)
            c.poly([(sx, 150), (sx + 10, 132), (sx + 20, 150)], cc)


def s_boarding_marines(c):
    space(c, ("#2a2a4a", "#3a1a2a"))
    rival(c, "cruiser", 560, 220, 1.0)
    ship(c, "escort", 180, 120, .9, plume="#9fd8ff", plume_len=.5)
    c.line((420, 120), (600, 200), 2, "#9aa4b2")
    for k in range(4):
        person(c, 470 + k * 30, 140 + k * 13, 26, "#d8dee8", .9)
    explosion(c, 600, 205, 10, sparks=8)


def s_defensive_mines(c):
    space(c, ("#1a2a4a", "#3a1a2a"))
    c.planet(160, 280, 150, "earth", light=(.6, -.6, .5), atmo="#7fc0ff", atmo_w=.03)
    rr = rng(5)
    for k in range(22):
        x, y = 400 + rr.random() * 680, 40 + rr.random() * 300
        r = 5 + rr.random() * 3
        c.ellipse((x - r, y - r, x + r, y + r), "#4a5262")
        for a in range(6):
            c.line((x, y), (x + math.cos(a) * r * 1.8, y + math.sin(a) * r * 1.8), 1, "#6a7282")
        c.glow_dot(x, y, 1.6, "#ff4a3a", .8)
    rival(c, "escort", 960, 240, .7)
    explosion(c, 930, 236, 22, seed=1)


def s_free_electron_lasers(c):
    space(c, ("#1a3a6a", "#2a2a4a"))
    ship(c, "cruiser", 160, 220, 1.0)
    for k, (tx, ty) in enumerate(((820, 90), (900, 160), (760, 300))):
        beam(c, (520, 196), (tx, ty), "#9fffd0", 1.4)
        explosion(c, tx, ty, 10, seed=k, sparks=8)
        missile(c, tx + 30, ty + 6, PI, .9, 80)


def s_ion_disruptor(c):
    space(c, ("#1a2a5a", "#2a1a4a"))
    rival(c, "cruiser", 640, 200, 1.0)
    for k in range(18):
        rr = rng(k)
        x0, y0 = 640 + rr.random() * 450, 170 + rr.random() * 60
        pts = [(x0, y0)]
        for j in range(5):
            pts.append((pts[-1][0] + rr.normal(0, 14), pts[-1][1] + rr.normal(0, 12)))
        for j in range(5):
            c.line(pts[j], pts[j + 1], 1, "#9fd8ff", glow=True, s=.8)
    a = ship(c, "frigate", 40, 200, .9)
    c.glow_dot(a["bow"][0] + 4, 200, 16, "#9fd8ff", 1.0)
    beam(c, (a["bow"][0] + 4, 200), (640, 200), "#7fb0ff", 2)


def s_plasma_cannon(c):
    space(c, ("#4a1a5a", "#1a2a5a"), .5)
    c.planet(980, 470, 260, "gas", light=(-.6, -.5, .5), rot=.4, atmo="#ffd8a0", atmo_w=.03)
    ship(c, "cruiser", 60, 218, .95, plume="#9fd8ff", plume_len=.4)
    X, Y = c.x, c.y
    for k in range(3):
        bx = 700 - k * 70
        t = np.clip((bx - X) / 160, 0, 1) * (X <= bx + 6)
        prof = np.exp(-((Y - 218) / (2 + 8 * (1 - t))) ** 2) * (1 - t) ** 1.2 * (X > bx - 160)
        c.add(col("#c08aff"), prof * (1.0 if k == 0 else .25))
    c.glow_dot(700, 218, 7, "#ffffff", 2.0)
    c.glow_dot(700, 218, 20, "#b07aff", .8)
    tgt = [(860, 180), (1120, 170), (1120, 260), (870, 250)]
    c.hull(tgt, 170, 260, base="#8d8676", panel=26)
    explosion(c, 870, 214, 30, seed=5, sparks=40)


def s_pulse_lance(c):
    space(c, ("#1a3a6a", "#3a1a4a"))
    ship(c, "cruiser", 80, 200, .95)
    rival(c, "cruiser", 760, 200, .8)
    for k in range(5):
        x0 = 520 + k * 60
        c.line((x0, 200), (x0 + 36, 200), 6, "#ffd0f0", glow=True, s=.9)
    beam(c, (512, 200), (790, 200), "#ff9ad8", 1.2, .5)
    shield(c, 920, 200, 200, 70, "#7fd0ff", .7, hit=(780, 200))


# ---- Defence ---------------------------------------------------------------------------------------

def s_heavy_armour(c):
    space(c, ("#2a2a4a", "#3a2a2a"))
    ship(c, "cruiser", 180, 200, 1.3, base="#b8a888")
    for k in range(5):
        x = 330 + k * 92
        c.hull([(x, 160), (x + 84, 160), (x + 84, 238), (x, 238)], 160, 238, base="#c4b494", panel=21, spec=.4)


def s_armour_mk3(c):
    space(c, ("#3a2a2a", "#2a2a4a"))
    ship(c, "cruiser", 180, 200, 1.3, base="#a89a7a")
    for k in range(6):
        x = 310 + k * 80
        for j, y in enumerate((150, 196)):
            c.hull([(x + j * 40, y), (x + 74 + j * 40, y), (x + 74 + j * 40, y + 44), (x + j * 40, y + 44)], y, y + 44, base="#8a8a7a", panel=18)
    streak(c, (900, 120), (760, 168), "#ffffff", 1.6)
    explosion(c, 760, 168, 12, sparks=16)


def s_debris_shielding(c):
    space(c, ("#2a2a3a", "#1a2a4a"))
    ship(c, "frigate", 140, 210, 1.3)
    c.poly([(660, 140), (680, 140), (690, 280), (670, 280)], "#c8ccd2", .8)
    c.poly([(700, 130), (712, 130), (722, 290), (710, 290)], "#c8ccd2", .6)
    debris(c, 900, 200, 60, 40, seed=2)
    for k in range(6):
        explosion(c, 712, 160 + k * 22, 5, seed=k, sparks=4)


def s_point_defence_2(c):
    space(c, ("#2a1a3a", "#1a2a5a"))
    a = ship(c, "cruiser", 140, 220, 1.0)
    for k, (mx, my) in enumerate(((760, 110), (840, 200), (720, 300))):
        missile(c, mx, my, PI + (my - 220) * .002, 1.0, 120)
        tracers(c, a["turrets"][k % len(a["turrets"])][0], a["turrets"][k % len(a["turrets"])][1], mx - 20, my, 8, seed=k)
    explosion(c, 700, 150, 16, seed=1, sparks=12)


def s_point_defence_3(c):
    space(c, ("#3a1a3a", "#1a2a5a"))
    a = ship(c, "capital", 80, 220, .85)
    for k in range(7):
        mx, my = 700 + (k % 3) * 120, 80 + k * 40
        if k % 2:
            explosion(c, mx, my, 12, seed=k, sparks=10)
        else:
            missile(c, mx, my, PI, .9, 100)
        t = a["turrets"][k % len(a["turrets"])]
        tracers(c, t[0], t[1], mx, my, 8, seed=k)


def s_tracking_radars(c):
    space(c, ("#1a2a4a", "#2a1a3a"))
    a = ship(c, "cruiser", 140, 230, 1.0)
    dx, dy = a["bridge"]
    waves(c, dx, dy, 20, 8, 28, "#5cff9a", -.3, .45, .35)
    for k, (mx, my) in enumerate(((800, 100), (880, 170), (760, 60))):
        missile(c, mx, my, PI, .9, 90)
        r = 14
        c.line((mx - 18 - r, my - r), (mx - 18 + r, my - r), 1, "#5cff9a", glow=True, s=.7)
        c.line((mx - 18 - r, my + r), (mx - 18 + r, my + r), 1, "#5cff9a", glow=True, s=.7)


def s_counter_missiles(c):
    space(c, ("#3a1a2a", "#1a2a5a"))
    ship(c, "cruiser", 120, 230, 1.0)
    for k in range(4):
        sx, sy = 560, 200 - k * 6
        tx, ty = 820 + k * 40, 80 + k * 70
        pts = [(sx + (tx - sx) * t, sy + (ty - sy) * t - 50 * math.sin(t * PI)) for t in np.linspace(0, 1, 20)]
        trail(c, pts[:-2], "#9aa8bc", .3)
        explosion(c, tx, ty, 14, seed=k, sparks=10)


def s_damage_control(c):
    space(c, ("#2a2a4a", "#1a3a4a"))
    ship(c, "cruiser", 180, 200, 1.3, base="#a8a8a8")
    for k, x in enumerate((470, 560, 650)):
        c.poly([(x, 168), (x + 30, 160), (x + 40, 175), (x + 10, 182)], "#1a1410")
        c.glow_dot(x + 20, 172, 4, "#ffb060", .9)
        person(c, x + 20, 158, 22, "#e0e4ea", .95)


def s_repair_drones(c):
    space(c, ("#1a3a5a", "#2a2a4a"))
    ship(c, "cruiser", 180, 200, 1.3)
    for k, (x, y) in enumerate(((440, 130), (560, 120), (680, 140), (520, 268), (720, 262))):
        c.ellipse((x - 8, y - 6, x + 8, y + 6), "#d8dee8")
        c.line((x - 14, y), (x + 14, y), 1.2, "#8a94a4")
        c.glow_dot(x, y + 10 * (1 if y < 200 else -1), 2.5, "#9fffd0", 1.0)
        c.line((x, y + 6 * (1 if y < 200 else -1)), (x, 200 + 30 * (-1 if y < 200 else 1)), 1, "#9fffd0", glow=True, s=.4)


def s_self_mending_armour(c):
    space(c, ("#1a3a3a", "#2a2a4a"))
    ship(c, "cruiser", 180, 200, 1.3, base="#9aa4a0")
    X, Y = c.x, c.y
    n = noise(H, W, 8, 3, 4)
    m = np.exp(-(((X - 600) / 120) ** 2 + ((Y - 200) / 40) ** 2))
    vein = np.exp(-((n - .5) / .03) ** 2) * m
    c.add(col("#5cffb0"), vein * .8)


def s_radiator_fins(c):
    space(c, ("#3a1a1a", "#1a2a4a"))
    x, y = 300, 200
    ship(c, "frigate", x, y, 1.3, heat=1.2)
    for k in range(4):
        rx = x + 120 + k * 50
        c.radiator(rx, y - 140, rx + 30, y - 24, 1.2, "bottom")
        c.radiator(rx, y + 24, rx + 30, y + 140, 1.2, "top")


def s_heat_sink_tanks(c):
    space(c, ("#3a1a2a", "#1a2a4a"))
    ship(c, "frigate", 200, 200, 1.4, heat=.8)
    for k in range(3):
        tx = 520 + k * 70
        m = c.ellipse((tx, 166, tx + 60, 234), "#5a3a2a")
        c.add(col("#ff6a2a"), m * (.3 + .2 * k))


def s_ejectable_heat_sinks(c):
    space(c, ("#3a1a1a", "#1a2a4a"))
    ship(c, "frigate", 420, 180, 1.2)
    for k, (x, y) in enumerate(((300, 260), (220, 300), (140, 330))):
        c.ellipse((x - 14, y - 14, x + 14, y + 14), "#8a4a2a")
        c.glow_dot(x, y, 18 - k * 4, "#ff6a2a", 1.0 - k * .2)


def s_sink_lockers(c):
    space(c, ("#3a1a2a", "#1a2a4a"))
    ship(c, "frigate", 140, 300, .8)
    for row in range(2):
        for k in range(7):
            x, y = 400 + k * 80, 90 + row * 100
            c.hull([(x, y), (x + 64, y), (x + 64, y + 80), (x, y + 80)], y, y + 80, base="#4a4e56", panel=70)
            m = c.ellipse((x + 12, y + 14, x + 52, y + 66), "#6a3a22")
            c.add(col("#ff6a2a"), m * .2)


def s_heat_sink_armour(c):
    space(c, ("#3a1a1a", "#2a1a3a"))
    ship(c, "cruiser", 180, 200, 1.3, base="#9a8a7a")
    X, Y = c.x, c.y
    m = np.exp(-(((X - 640) / 220) ** 2 + ((Y - 200) / 34) ** 4))
    c.add(col("#ff5a1a"), m * .35 * noise(H, W, 20, 9, 4))


def s_deflector_grid(c):
    space(c, ("#1a3a6a", "#2a2a4a"))
    ship(c, "cruiser", 300, 200, 1.0)
    shield(c, 530, 200, 300, 90, "#7fd0ff", 1.0, hit=(820, 160))
    streak(c, (1100, 120), (830, 160), "#ffffff", 1.6)


def s_shield_harmonics(c):
    space(c, ("#1a3a6a", "#3a2a5a"))
    ship(c, "cruiser", 300, 200, 1.0)
    shield(c, 530, 200, 300, 90, "#7fd0ff", .8)
    shield(c, 530, 200, 320, 100, "#9f8aff", .6)
    shield(c, 530, 200, 340, 110, "#7fd0ff", .4, hit=(860, 140))


def s_shields_mk3(c):
    space(c, ("#1a4a6a", "#2a2a4a"))
    ship(c, "capital", 170, 200, .95)
    shield(c, 480, 200, 360, 110, "#9fe0ff", 1.3, hit=(820, 130))
    for k in range(3):
        streak(c, (1100, 60 + k * 20), (840 - k * 10, 135 + k * 8), "#ffffff", 1.2, s=.7)


def s_adaptive_shields(c):
    space(c, ("#2a1a5a", "#1a4a5a"), .55)
    ship(c, "capital", 170, 200, .95)
    shield(c, 480, 200, 360, 110, "#7fd0ff", .6)
    X, Y = c.x, c.y
    dx, dy = (X - 480) / 360, (Y - 200) / 110
    d = np.sqrt(dx * dx + dy * dy)
    rim = np.exp(-((d - 1) / .05) ** 2) * ss(dx, .4, .9)
    c.add(col("#c08aff"), rim * 1.6)
    beam(c, (1120, 120), (820, 160), "#ff9ad8", 1.5)


def s_planet_shields(c):
    surface(c, 320, body=lambda: c.planet(900, 80, 60, "gas", light=(-.6, .3, .6), atmo="#ffd8a0", atmo_w=.03))
    for k, (x, w, h) in enumerate(((440, 60, 70), (510, 80, 110), (600, 50, 60), (660, 70, 90))):
        building(c, x, 320, w, h, seed=k)
    dome(c, 560, 320, 230, "#7fd0ff", s=1.4)
    c.line((900, 0), (700, 110), 1.4, "#ffb060", glow=True, s=.8)
    explosion(c, 705, 108, 18, seed=3, sparks=14)


def s_ground_launchers(c):
    surface(c, 310, body=lambda: c.planet(240, 90, 50, "moon", light=(.5, .3, .6), atmo=None))
    for k, x in enumerate((460, 560, 660)):
        c.hull([(x, 280), (x + 40, 280), (x + 46, 312), (x - 6, 312)], 280, 312, base="#6a7078", panel=50)
        c.ellipse((x + 6, 270, x + 34, 290), "#2a2e34")
    missile(c, 700, 120, -1.2, 1.4, 160)
    missile(c, 600, 60, -1.2, 1.4, 200)
    station(c, 940, 80, 40, ring=False)


def s_strike_craft(c):
    surface(c, 310, body=lambda: c.planet(940, 70, 46, "moon", light=(-.5, .3, .6), atmo=None))
    building(c, 300, 310, 200, 40, seed=2)
    c.poly([(330, 270), (470, 270), (460, 300), (340, 300)], "#0a0c10")
    c.add(col("#ffb860"), c.mask(lambda d, s: d.rectangle([340 * s, 296 * s, 460 * s, 300 * s], fill=255)) * .6)
    for k in range(5):
        fx, fy = 560 + k * 90, 230 - k * 34
        c.poly([(fx, fy), (fx + 30, fy - 6), (fx + 4, fy + 8)], "#c8ccd2")
        c.plume(fx + 2, fy + 2, 60, 5, "#9fd8ff", .8)


def s_escape_pods(c):
    space(c, ("#3a1a1a", "#1a2a4a"))
    ship(c, "frigate", 140, 220, 1.0, base="#8a8478", lights=False)
    explosion(c, 360, 220, 60, seed=2, sparks=40)
    for k, (x, y) in enumerate(((620, 120), (700, 180), (660, 290), (780, 240))):
        c.ellipse((x - 10, y - 8, x + 10, y + 8), "#e8e0d0")
        c.plume(x - 10, y, 30, 6, "#ffb07a", .6)
        c.glow_dot(x + 6, y, 2, "#5cff9a", 1.0)


def s_hardened_bunkers(c):
    surface(c, 300, ground="#141210", top="#4a4440")
    for k, x in enumerate((300, 520, 740)):
        c.poly([(x, 300), (x + 40, 260), (x + 140, 260), (x + 180, 300)], "#5a5650")
        c.hull([(x + 40, 262), (x + 140, 262), (x + 180, 300), (x, 300)], 262, 300, base="#7a766e", panel=30)
        c.poly([(x + 70, 278), (x + 110, 278), (x + 110, 286), (x + 70, 286)], "#0a0806")
        c.glow_dot(x + 90, 282, 3, "#ffb060", .5)
    explosion(c, 640, 230, 40, seed=6, sparks=20)


def s_predictive_fire_control(c):
    space(c, ("#1a2a4a", "#2a1a4a"))
    a = ship(c, "cruiser", 120, 240, .9)
    pts = [(1000 - t * 300, 80 + 60 * math.sin(t * 9)) for t in np.linspace(0, 1, 40)]
    for i in range(39):
        c.line(pts[i], pts[i + 1], 1, "#9aa8bc", glow=True, s=.3)
    missile(c, *pts[-1], PI, 1.0, 0)
    for k in range(6):
        t = 1.05 + k * .05
        px, py = 1000 - t * 300, 80 + 60 * math.sin(t * 9)
        c.ellipse((px - 3, py - 3, px + 3, py + 3), None, glow="#5cff9a", s=.6)
    tracers(c, a["turrets"][0][0], a["turrets"][0][1], 1000 - 1.15 * 300, 80 + 60 * math.sin(1.15 * 9), 8, "#5cff9a")


def s_spoofing(c):
    space(c, ("#1a2a4a", "#3a1a3a"))
    ship(c, "cruiser", 140, 200, 1.0)
    for k, (x, y) in enumerate(((700, 140), (760, 260))):
        missile(c, x, y, PI - .5 + k, 1.0, 140, curve=30)
    for k in range(3):
        c.ellipse((520 - k * 40, 300 - k * 30, 560 - k * 40, 340 - k * 30), None, glow="#c08aff", s=.25)
    waves(c, 560, 200, 30, 6, 26, "#c08aff", 0, .8, .3)




# ---- Sensors and signals ---------------------------------------------------------------------------

def s_command_uplink(c):
    space(c, ("#1a3a5a", "#2a2a4a"))
    c.planet(150, 300, 170, "earth", light=(.6, -.6, .5), atmo="#7fc0ff", atmo_w=.03)
    d = dish(c, 260, 160, 30)
    for k, (x, y) in enumerate(((700, 90), (860, 200), (760, 310))):
        ship(c, "escort", x, y, .55, seed=k)
        dashed(c, (300, 150), (x, y), "#7cc4ff")


def s_long_sensors(c):
    space(c, ("#1a3a5a", "#2a2a4a"))
    a = ship(c, "picket", 120, 220, 1.0)
    waves(c, a["dish"][0], a["dish"][1], 40, 9, 70, "#5cff9a", -.15, .5, .3)
    rival(c, "escort", 980, 150, .5)


def s_long_sensors_2(c):
    space(c, ("#1a4a5a", "#2a2a4a"), .5)
    a = ship(c, "picket", 60, 260, .9)
    waves(c, a["dish"][0], a["dish"][1], 40, 14, 70, "#5cff9a", -.2, .55, .3)
    for x, y in ((820, 90), (960, 140), (1060, 60)):
        rival(c, "escort", x, y, .3, seed=int(x))
        c.ellipse((x - 6, y - 16, x + 70, y + 16), None, glow="#5cff9a", s=.08)


def s_sun_watch(c):
    space(c, ("#3a2a1a", "#2a1a1a"), .3)
    X, Y = c.x, c.y
    c.sun(200, 200, 40, "#fff2c4", .22)
    n = noise(H, W, 30, 4, 5)
    d = np.sqrt((X - 220) ** 2 + (Y - 200) ** 2)
    flare = np.exp(-((d - 80) / 40) ** 2) * ss(n, .55, .9) * ss(X - 220, 0, 60)
    c.add(col("#ff9a3c"), flare * .8)
    satellite(c, 820, 180, 1.4)
    dashed(c, (800, 180), (420, 190), "#ffd08a")


def s_rock_mapping(c):
    space(c, ("#2a2a3a", "#1a2a3a"))
    rr = rng(3)
    for k in range(70):
        x, y = rr.random() * W, 140 + rr.normal(0, 50)
        r = rr.uniform(2, 14)
        c.planet(x, y, r, "moon", light=(-.6, -.4, .6), atmo=None, seed=k)
        if r > 10:
            for sx, sy in ((-1, -1), (1, -1), (-1, 1), (1, 1)):
                c.line((x + sx * (r + 6), y + sy * (r + 6)), (x + sx * (r + 1), y + sy * (r + 6)), 1, "#5cff9a", glow=True, s=.6)
    ship(c, "picket", 380, 320, .7)


def s_concealed_mounts(c):
    space(c, ("#2a2a3a", "#1a2a4a"))
    ship(c, "freighter", 160, 200, 1.4)
    x0 = 160 + 420 * 1.4 * .16 + 2 * 420 * 1.4 * .7 / 6
    c.poly([(x0, 158), (x0 + 60, 140), (x0 + 60, 156), (x0 + 2, 172)], "#8a7a5a")
    c.ellipse((x0 + 14, 166, x0 + 46, 186), "#3a3e46")
    c.line((x0 + 30, 176), (x0 + 90, 170), 3, "#2a2e36")


def s_signals_analysis(c):
    space(c, ("#1a2a4a", "#2a1a3a"))
    X, Y = c.x, c.y
    for k in range(3):
        y0 = 110 + k * 80
        wave = y0 + 18 * np.sin(X / (14 + k * 5)) * np.sin(X / 140 + k)
        c.add(col("#5cff9a" if k == 1 else "#7cc4ff"), np.exp(-((Y - wave) / 1.4) ** 2) * .7)
    dish(c, 1000, 330, 40)


def s_tight_beam(c):
    space(c, ("#1a3a6a", "#2a2a4a"))
    satellite(c, 200, 140, 1.2)
    satellite(c, 560, 220, 1.2)
    satellite(c, 920, 120, 1.2)
    beam(c, (200, 140), (560, 220), "#7cc4ff", 1.0, .8)
    beam(c, (560, 220), (920, 120), "#7cc4ff", 1.0, .8)


def s_ai_cores(c):
    c.img[:] = col("#05070b")
    for row in range(4):
        for k in range(12):
            x, y = 80 + k * 84, 50 + row * 80
            c.hull([(x, y), (x + 70, y), (x + 70, y + 64), (x, y + 64)], y, y + 64, base="#2a2e36", panel=72)
            rr = rng(row * 12 + k)
            for j in range(6):
                c.glow_dot(x + 10 + j * 10, y + 54, 1.2, "#5cff9a" if rr.random() < .7 else "#7cc4ff", .8)
    c.glow_dot(560, 190, 240, "#4a7aff", .08)


def s_deep_space_telescopes(c):
    c.sky(neb=("#5a2a6a", "#1a5a7a"), neb_amt=.8, stars=2600)
    X, Y = c.x, c.y
    gx, gy = 880, 110
    gd = ((X - gx) * .9 + (Y - gy) * .5) ** 2 / 3600 + ((Y - gy) * .9 - (X - gx) * .3) ** 2 / 300
    c.add(col("#ffe8c8"), np.exp(-gd) * .5)
    ang = -.22
    ca, sa = math.cos(ang), math.sin(ang)
    def R(px, py, ox=330, oy=250):
        return (ox + px * ca - py * sa, oy + px * sa + py * ca)
    c.hull([R(-150, -34), R(170, -34), R(170, 34), R(-150, 34)], 216, 284, base="#c8ccd2", panel=40)
    c.poly([R(170, -40), R(230, -52), R(230, 40), R(170, 40)], "#d8c8a0", .95)
    c.poly([R(176, -36), R(226, -46), R(226, 34), R(176, 36)], "#8a6a3a", .6)
    for k in (-1, 1):
        c.poly([R(-120, k * 40), R(40, k * 40), R(40, k * 110), R(-120, k * 110)], "#1d3d58")
    c.lights([R(-140, 20), R(-140, -20)], "#ffd28a", 1.2, .8)


def s_satellites(c):
    space(c, ("#1a3a5a", "#2a2a4a"))
    c.planet(560, 560, 380, "earth", light=(.3, -.9, .3), atmo="#7fc0ff", atmo_w=.015, night_lights=True)
    for k, (x, y) in enumerate(((240, 120), (560, 60), (880, 110))):
        satellite(c, x, y, 1.0)
    c.ellipse((140, 50, 980, 230), None, glow="#7cc4ff", s=.0)
    for k in range(60):
        t = k / 60 * 2 * PI
        c.glow_dot(560 + math.cos(t) * 420, 140 - math.sin(t) * 80 * .9 + 40, .8, "#7cc4ff", .25)


def s_fleet_data_links(c):
    space(c, ("#1a3a6a", "#2a2a4a"))
    pts = [(220, 120), (360, 260), (560, 160), (300, 340)]
    for k, (x, y) in enumerate(pts):
        ship(c, "escort" if k else "cruiser", x - 120, y, .7, seed=k)
    for i in range(len(pts)):
        for j in range(i + 1, len(pts)):
            dashed(c, pts[i], pts[j], "#5cff9a", 6, 6, 1.0, .4)
    missile(c, 900, 200, PI, 1.0, 100)
    tracers(c, 560, 160, 880, 200, 8)


def s_masked_emissions(c):
    space(c, ("#2a2a4a", "#1a2a3a"), .5)
    for k, (x, y) in enumerate(((200, 130), (420, 250), (640, 140), (300, 310))):
        a = ship(c, "cruiser" if k == 2 else "frigate", x, y, .65, seed=k, base="#7a8494")
        x1 = a["bow"][0]
        colr = "#c08aff" if k == 2 else "#5cff9a"
        for sx, ex in ((x - 8, x + 10), (x1 - 10, x1 + 8)):
            c.line((sx, y - 30), (ex, y - 30), 1.2, colr, glow=True, s=.7)
            c.line((sx, y + 30), (ex, y + 30), 1.2, colr, glow=True, s=.7)


def s_false_traffic(c):
    space(c, ("#1a2a4a", "#3a1a3a"))
    dish(c, 180, 340, 50)
    rr = rng(4)
    for k in range(40):
        x, y = 320 + rr.random() * 760, 40 + rr.random() * 300
        c.line((x, y), (x + rr.uniform(10, 40), y), 1.6, "#c08aff" if rr.random() < .5 else "#7cc4ff", glow=True, s=1.0)


def s_stealth_drones(c):
    space(c, ("#1a2a4a", "#1a1a2a"), .6)
    c.planet(120, 300, 140, "earth", light=(.6, -.6, .5), atmo="#7fc0ff", atmo_w=.03)
    rival(c, "picket", 880, 300, .6)
    waves(c, 900, 270, 40, 5, 40, "#ff9a7a", PI, .6, .2)
    for k in range(3):
        x, y = 360 + k * 220, 120 + k * 60
        c.poly([(x, y), (x + 50, y + 8), (x, y + 16)], "#14171d")
        c.add(col("#6a9ad8"), np.clip(c.mask(lambda d, s, x=x, y=y: d.line([x * s, y * s, (x + 50) * s, (y + 8) * s], fill=255, width=s)), 0, 1) * .5)


def s_decode_signals(c):
    space(c, ("#3a1a5a", "#1a3a5a"), .55)
    X, Y = c.x, c.y
    wave = 160 + 30 * np.sin(X / 18) * np.sin(X / 90)
    c.add(col("#c08aff"), np.exp(-((Y - wave) / 1.5) ** 2) * ss(X, 100, 560) * (X < 600) * .7)
    for k in range(10):
        x = 640 + k * 44
        h = 10 + (k * 37 % 30)
        c.poly([(x, 160 - h), (x + 20, 160 - h), (x + 20, 160 + h), (x, 160 + h)], "#5cff9a", .5)
    dish(c, 300, 350, 46)


def s_iff_codes(c):
    space(c, ("#1a3a4a", "#2a2a4a"))
    ship(c, "frigate", 140, 200, .9)
    rr = rng(2)
    for k in range(14):
        x, y = 560 + rr.random() * 500, 60 + rr.random() * 280
        c.ellipse((x - 6, y - 6, x + 6, y + 6), "#4a5262")
        c.glow_dot(x, y, 2, "#5cff9a", .9)


def s_missile_hijack(c):
    space(c, ("#3a1a3a", "#1a2a4a"))
    ship(c, "cruiser", 100, 220, .9)
    r = rival(c, "frigate", 880, 120, .6)
    pts = [(900 - 500 * t + 200 * t * t, 260 - 100 * t * (1 - t) * 4) for t in np.linspace(0, 1, 30)]
    trail(c, pts, "#9aa8bc", .3)
    missile(c, 860, 170, -.6, 1.1, 0)
    dashed(c, (500, 200), (840, 180), "#c08aff")


def s_jamming(c):
    space(c, ("#2a1a4a", "#1a2a4a"))
    ship(c, "cruiser", 120, 220, 1.0)
    X, Y = c.x, c.y
    n = noise(H, W, 3, 7, 2)
    c.add(col("#c08aff"), ss(n, .7, .95) * ss(X, 560, 700) * .25, bloom=False)
    waves(c, 520, 200, 40, 8, 36, "#c08aff", 0, .8, .25)


def s_stealth_coatings(c):
    c.sky(neb=("#1a4a6a", "#5a2a6a"), neb_amt=.75, stars=2200)
    X, Y = c.x, c.y
    nb = noise(H, W, 140, 12, 6)
    blob = np.exp(-(((X - 560) / 420) ** 2 + ((Y - 200) / 160) ** 2))
    c.add(col("#3a7ab0"), blob * (.25 + .5 * nb) * .55, bloom=False)
    c.add(col("#8a4ab0"), blob * ss(nb, .5, .9) * .35, bloom=False)
    ship(c, "cruiser", 220, 200, 1.4, dark=True, lights=False)
    for rr in (260, 330, 400):
        dd = np.sqrt((X - 1180) ** 2 + (Y - 120) ** 2)
        c.add(col("#5cff9a"), np.exp(-((dd - rr * 2) / 2.5) ** 2) * .22, bloom=False)


def s_cyber_attack(c):
    space(c, ("#1a3a2a", "#1a2a4a"))
    r = rival(c, "cruiser", 600, 200, 1.0)
    rr = rng(9)
    for k in range(80):
        x, y = rr.random() * 560, rr.random() * H
        c.line((x, y), (x + rr.uniform(6, 30), y), 1.4, "#5cff9a", glow=True, s=.9 * rr.random())
    for k in range(12):
        x = 650 + rr.random() * 400
        c.line((x, 186), (x + 20, 186), 2, "#5cff9a", glow=True, s=.8)


# ---- Industry and power -----------------------------------------------------------------------------

def factory(c, x, g, w=180, h=70, seed=0, stacks=2):
    building(c, x, g, w, h, seed=seed, lit=.4)
    for k in range(stacks):
        sx = x + 20 + k * 40
        c.hull([(sx, g - h - 50), (sx + 14, g - h - 50), (sx + 14, g - h), (sx, g - h)], g - h - 50, g - h, base="#7a7a7a", panel=60)
        c.glow_dot(sx + 7, g - h - 52, 3, "#ff4a3a", .5)


def s_automation(c):
    surface(c, 310, body=lambda: c.planet(950, 90, 60, "gas", light=(-.7, .2, .6), atmo="#ffd8a0", atmo_w=.03))
    factory(c, 300, 310, 260, 80, seed=1)
    for k in range(5):
        x = 620 + k * 60
        c.hull([(x, 290), (x + 40, 290), (x + 40, 306), (x, 306)], 290, 306, base="#d8b850", panel=50)
        c.glow_dot(x + 20, 288, 2, "#ffb860", .8)


def s_automation_2(c):
    surface(c, 300)
    for k in range(3):
        factory(c, 120 + k * 300, 300, 240, 60 + k * 15, seed=k)
    c.line((120, 280), (1000, 280), 2, "#4a4e56")


def s_automation_3(c):
    surface(c, 320, body=lambda: c.planet(560, 70, 40, "moon", light=(-.2, .4, .6), atmo=None))
    for k in range(6):
        building(c, 80 + k * 170, 320, 140, 70 + (k * 41 % 70), seed=k, lit=.7)
    for k in range(20):
        c.glow_dot(80 + k * 50, 330, 2, "#ffb860", .5)


def s_precision_workshops(c):
    c.img[:] = col("#0a0c10")
    c.ground(300, kind="#14161a", top="#2a2e34", haze="#4a5a6a")
    c.glow_dot(560, 60, 200, "#ffe8c0", .12)
    for k in range(4):
        x = 160 + k * 230
        c.hull([(x, 220), (x + 140, 220), (x + 140, 300), (x, 300)], 220, 300, base="#5a6068", panel=40)
        c.line((x + 70, 220), (x + 70, 150), 4, "#8a8e96")
        c.line((x + 70, 150), (x + 120, 180), 3, "#8a8e96")
        c.glow_dot(x + 120, 196, 6, "#9fd8ff", 1.0)


def s_geothermal_power(c):
    surface(c, 300, ground="#1a0e08", top="#5a3020", haze="#ff8a5a", neb=("#3a1a1a", "#2a1a3a"))
    X, Y = c.x, c.y
    c.poly([(760, 300), (880, 170), (920, 170), (1060, 300)], "#2a1810")
    c.glow_dot(900, 170, 20, "#ff7a3c", 1.2)
    lava = noise(H, W, 10, 3, 3)
    chan = np.exp(-((X - 900 + (Y - 170) * .9) / 40) ** 2) * (Y > 170)
    c.add(col("#ff5a1a"), ss(lava, .6, .9) * chan * .9)
    building(c, 300, 300, 160, 50, seed=2)
    tower(c, 500, 300, 40, 70)
    for k in range(3):
        c.line((300 + k * 60, 250), (300 + k * 60, 300), 3, "#4a3a30")


def s_rare_element_assays(c):
    surface(c, 280, ground="#1a1612", top="#5a5048")
    rr = rng(6)
    for k in range(30):
        x, y = rr.random() * W, 290 + rr.random() * 90
        c.glow_dot(x, y, 2.5, rr.choice(["#5cff9a", "#7cc4ff", "#ffd08a"]), .9)
    person(c, 520, 300, 60, "#d8dee8", .95)
    c.glow_dot(545, 262, 4, "#5cff9a", 1.2)


def s_mineral_surveys(c):
    surface(c, 290, ground="#1a1410", top="#6a5a48")
    rr = rng(1)
    for k in range(5):
        x = 200 + k * 180
        c.line((x, 290), (x, 220), 2, "#8a8e96")
        c.glow_dot(x, 218, 3, "#ffd08a", .8)
    for k in range(40):
        x, y = rr.random() * W, 300 + rr.random() * 80
        c.ellipse((x - 3, y - 2, x + 3, y + 2), "#c8a060", .7)


def s_deep_boring(c):
    surface(c, 240, ground="#1a1410", top="#5a4a3a")
    c.poly([(480, 240), (640, 240), (600, 380), (520, 380)], "#0a0806")
    c.hull([(500, 100), (620, 100), (620, 240), (500, 240)], 100, 240, base="#8a8e96", panel=20)
    for k in range(6):
        c.line((500 + k * 24, 100), (560, 40), 2, "#6a6e76")
    c.glow_dot(560, 360, 10, "#ffb060", .8)
    c.lights([(510, 140), (610, 140), (560, 44)], "#ff4a3a", 1.5, .7)


def s_efficient_upkeep(c):
    space(c, ("#1a3a4a", "#2a2a4a"))
    station(c, 560, 190, 120)
    for k, x in enumerate((200, 920)):
        ship(c, "escort", x - 80, 300 - k * 200, .6, seed=k)


def shipyard(c, x, y, w=600, h=160):
    for k in range(7):
        gx = x + k * w / 6
        c.line((gx, y - h / 2), (gx, y + h / 2), 3, "#5a6270")
    c.line((x, y - h / 2), (x + w, y - h / 2), 4, "#6a7280")
    c.line((x, y + h / 2), (x + w, y + h / 2), 4, "#6a7280")
    c.lights([(x + k * w / 12, y - h / 2) for k in range(13)], "#ffd28a", 1.4, .7)


def s_fast_shipyard(c):
    space(c, ("#1a3a5a", "#2a2a4a"))
    c.planet(1050, 420, 200, "earth", light=(-.5, -.7, .5), atmo="#7fc0ff", atmo_w=.03)
    shipyard(c, 200, 190, 560, 180)
    ship(c, "cruiser", 260, 190, 1.0, lights=False)
    for k in range(5):
        c.glow_dot(320 + k * 90, 150 + (k % 2) * 80, 4, "#9fd8ff", 1.2)


def s_yard_tooling(c):
    space(c, ("#1a3a5a", "#3a2a3a"))
    shipyard(c, 160, 190, 760, 200)
    ship(c, "frigate", 200, 190, .9, lights=False)
    ship(c, "frigate", 560, 190, .9, lights=False, seed=2)
    for k in range(8):
        c.glow_dot(240 + k * 80, 110 + (k % 2) * 160, 3, "#ffd08a", 1.0)


def s_breeder_reactors(c):
    surface(c, 310)
    dome(c, 420, 310, 90, "#c8ccd2", s=0)
    m = c.ellipse((330, 220, 510, 400), "#9aa0a8")
    c.over(col("#7a8088"), m * (c.y < 310) * ss(c.y, 220, 310))
    c.poly([(330, 310), (510, 310), (510, 400), (330, 400)], "#1a1612")
    tower(c, 640, 310, 60, 110)
    tower(c, 760, 310, 60, 110)
    c.glow_dot(420, 290, 30, "#7cffd0", .2)


def s_grid_balancing(c):
    surface(c, 300)
    for k in range(9):
        x = 60 + k * 130
        c.line((x, 300), (x, 200), 3, "#2a2e34")
        c.line((x - 20, 206), (x + 20, 206), 2, "#2a2e34")
        if k < 8:
            for j in (-16, 16):
                pts = [(x + j + t * 130, 206 + 14 * math.sin(t * PI)) for t in np.linspace(0, 1, 10)]
                for i in range(9):
                    c.line(pts[i], pts[i + 1], 1, "#3a3e44")
    for k in range(9):
        c.glow_dot(60 + k * 130, 206, 3, "#7cc4ff", .6)


def s_deuterium_extraction(c):
    surface(c, 300, ground="#14181c", top="#4a5a68", haze="#7fb0d8")
    c.ellipse((100, 300, 1020, 384), "#123048")
    building(c, 520, 300, 160, 60, seed=3)
    for k in range(3):
        x = 720 + k * 70
        m = c.ellipse((x, 200, x + 56, 300), "#9fd8f5")
        v = np.clip((c.y - 200) / 100, 0, 1)
        c.over(col("#e0f4ff") * (1.05 - v[..., None]) + col("#1f4b63") * v[..., None], m * (c.y < 300))


def s_electronics_fabs(c):
    c.img[:] = col("#06080c")
    for row in range(3):
        for k in range(8):
            x, y = 100 + k * 120, 70 + row * 100
            c.poly([(x, y), (x + 90, y), (x + 90, y + 70), (x, y + 70)], "#0a1a14")
            for j in range(6):
                c.line((x + 6, y + 10 + j * 10), (x + 84, y + 10 + j * 10), .8, "#c8a040", alpha=.7)
            c.glow_dot(x + 45, y + 35, 6, "#5cff9a", .4)
    c.glow_dot(560, 190, 300, "#7cc4ff", .05)


def s_orbital_docks(c):
    space(c, ("#1a3a5a", "#2a2a4a"))
    c.planet(160, 340, 200, "rust", light=(.5, -.7, .5), atmo="#ffb08a", atmo_w=.02)
    shipyard(c, 480, 180, 480, 140)
    ship(c, "frigate", 520, 180, .9, lights=False)


def s_recycling(c):
    surface(c, 300)
    building(c, 600, 300, 220, 80, seed=4)
    rr = rng(2)
    for k in range(40):
        x, y = 200 + rr.random() * 340, 300 - rr.random() * 40 * (1 - abs(rr.random() - .5))
        c.poly([(x, y), (x + 8, y - 6), (x + 14, y)], "#6a6e76")
    c.line((520, 240), (600, 260), 3, "#5a6068")


def s_colony_shipyards(c):
    space(c, ("#1a3a5a", "#2a2a4a"))
    c.planet(900, 470, 300, "earth", light=(-.3, -.9, .3), atmo="#7fc0ff", atmo_w=.012, night_lights=True)
    shipyard(c, 140, 160, 520, 150)
    ship(c, "cruiser", 170, 160, .95, lights=False)
    ship(c, "escort", 760, 90, .6, plume="#9fd8ff", plume_len=.5)


def s_fusion_power(c):
    c.sky(tint=("#141a2e", "#070a12"), neb=("#2a3a6a", "#4a2a5a"), neb_amt=.3, stars=900)
    c.planet(880, 120, 90, "gas", light=(-.7, .1, .6), rot=1.0, atmo="#ffd8a0", atmo_w=.03, rings=(.2, 1.35, 2.1, "#b8a888"))
    c.ground(300, kind="#1a1612", curve=-10, top="#6a6258", haze="#8aa0c0")
    cx, cy = 470, 262
    X, Y = c.x, c.y
    c.hull([(300, 262), (640, 262), (660, 310), (280, 310)], 262, 310, base="#5a6068", panel=24, grime=.5)
    R, r = 130, 46
    dx, dy = (X - cx) / R, (Y - cy) / (R * .38)
    d = np.sqrt(dx * dx + dy * dy)
    tube = np.clip(1 - ((d - 1) * R / r) ** 2, 0, 1)
    m = ss(tube, 0, .05)
    nz = np.sqrt(tube)
    lam = np.clip(nz * .7 - ((Y - cy) / 80) * .4 + .1, 0, 1)
    ang = np.arctan2(dy, dx)
    coils = ss(np.sin(ang * 18), .7, .85)
    c.over(col("#9aa4b2") * (.12 + .85 * lam[..., None]) * (1 - .45 * coils[..., None]), m * (Y < cy + 50))
    win = m * ss(Y, cy - 10, cy - 4) * (Y < cy + 30) * (np.abs(X - cx) < 170) * ss(tube, .5, .6)
    c.over(col("#0a0a14"), win)
    c.add(col("#d08aff"), np.exp(-((d - 1) * R / 6) ** 2) * win * 2.0)
    c.add(col("#ffffff"), np.exp(-((d - 1) * R / 2) ** 2) * win * 1.8)
    c.add(col("#b07aff"), np.exp(-(((X - cx) / 260) ** 2 + ((Y - 300) / 30) ** 2)) * (Y > 296) * .25, bloom=False)
    for tx in (720, 790):
        tower(c, tx, 300, 40, 70)
    for px in range(90, 300, 70):
        c.line((px, 300), (px, 250), 2, "#2a2e34")
    c.lights([(320 + i * 30, 300) for i in range(11)], "#ffd28a", 1.4, .7)


def s_helium_3_skimming(c):
    space(c, ("#3a2a1a", "#2a2a4a"), .3)
    c.planet(560, 760, 620, "gas", light=(.2, -.9, .3), atmo="#ffd8a0", atmo_w=.012, rot=.6)
    station(c, 560, 90, 60)
    c.line((560, 120), (560, 150), 2, "#8a94a4")
    ship(c, "tanker", 120, 200, .6, plume="#9fd8ff", plume_len=.5)
    c.glow_dot(560, 150, 8, "#ffd8a0", .7)


def s_mass_driver(c):
    surface(c, 300, body=lambda: c.planet(950, 80, 50, "moon", light=(-.6, .3, .6), atmo=None))
    c.hull([(100, 290), (700, 140), (712, 150), (112, 302)], 140, 302, base="#7a808a", panel=40)
    for k in range(10):
        x = 120 + k * 60
        c.ellipse((x - 6, 282 - k * 15, x + 6, 296 - k * 15), "#c08a4a")
    streak(c, (712, 145), (900, 98), "#ffd08a", 2)


def s_helium_3_reactors(c):
    s_fusion_power(c)
    c.glow_dot(470, 262, 50, "#ffe08a", .3)


def s_power_beaming(c):
    space(c, ("#1a3a5a", "#2a2a4a"))
    c.planet(140, 300, 150, "earth", light=(.6, -.6, .5), atmo="#7fc0ff", atmo_w=.03)
    c.planet(980, 120, 80, "rust", light=(-.6, .3, .6), atmo="#ffb08a", atmo_w=.03)
    station(c, 260, 120, 30, ring=False)
    station(c, 900, 220, 30, ring=False)
    beam(c, (280, 124), (880, 216), "#ffd08a", 2.5)


def s_skyhook(c):
    space(c, ("#1a3a5a", "#2a2a4a"))
    c.planet(560, 900, 760, "rust", light=(.4, -.9, .3), atmo="#ffb08a", atmo_w=.012)
    cx, cy = 560, 80
    c.line((cx - 260, cy + 70), (cx + 260, cy - 70), 2, "#c8ccd2")
    station(c, cx, cy, 30, ring=False)
    c.glow_dot(cx - 260, cy + 70, 4, "#ffd28a", .9)
    missile(c, 260, 170, -.6, 1.2, 80)


def s_space_elevator(c):
    space(c, ("#24345a", "#4a2a5a"), .4)
    c.sun(1060, 40, 9, strength=1.1)
    c.planet(560, 1180, 900, "earth", light=(.6, -.7, .4), rot=1.2, atmo="#7fc0ff", atmo_w=.012, night_lights=True)
    c.line((560, 282), (560, 70), 1.4, "#c9d4e0", alpha=.9)
    c.line((560, 282), (560, 70), 4, "#9fd8ff", glow=True, s=.08)
    c.hull([(552, 170), (568, 170), (570, 186), (550, 186)], 170, 186, base="#c8ccd2", panel=8)
    c.lights([(556, 184), (564, 184)], "#ffd28a", 1.4, .8)
    c.hull([(530, 50), (590, 50), (596, 72), (524, 72)], 50, 72, base="#b8c0cc", panel=10)
    for sx in (430, 610):
        solar_panel(c, sx, 56, sx + 80, 66)
    c.lights([(540, 70), (580, 70), (560, 52)], "#ffd28a", 1.3, .8)
    c.glow_dot(560, 282, 4, "#ffe2a8", .6)


def s_self_replicating_miners(c):
    space(c, ("#2a2a3a", "#1a2a3a"))
    c.planet(560, 300, 160, "moon", light=(.5, -.6, .5), atmo=None, seed=3)
    rr = rng(8)
    for k in range(24):
        a = rr.uniform(0, 2 * PI)
        r = rr.uniform(170, 330)
        x, y = 560 + math.cos(a) * r, 300 + math.sin(a) * r * .5
        c.poly([(x - 6, y), (x, y - 5), (x + 6, y), (x, y + 5)], "#c8ccd2")
        c.glow_dot(x, y, 1.5, "#5cff9a", .7)



# ---- Life and colonies ------------------------------------------------------------------------------

def greenhouse(c, x, g, w, h, seed=0):
    m = c.poly([(x, g), (x, g - h * .6), (x + w / 2, g - h), (x + w, g - h * .6), (x + w, g)], "#0e2418", .9)
    leaves = noise(H, W, 4, c.seed + seed, 3)
    c.add(col("#5cd07a"), m * ss(leaves, .5, .8) * .35 * ss(c.y, g - h * .5, g), bloom=False)
    c.add(col("#ffe8c0"), m * .06, bloom=False)
    c.add(col("#bfe6ff"), np.clip(m - c.blur(m, 1.2), 0, 1) * .5)
    for k in range(1, 6):
        xx = x + k * w / 6
        c.line((xx, g), (xx, g - h * (.6 + .4 * (1 - abs(xx - x - w / 2) / (w / 2)))), .6, "#8ab0c8", alpha=.5)


def s_food_recyclers(c):
    P = interior(c, "#0e1218", "#bfe6ff")
    for k, x in enumerate((360, 680)):
        c.hull([(x, 200), (x + 80, 200), (x + 80, 330), (x, 330)], 200, 330, base="#8a949e", panel=20)
        m = c.ellipse((x + 20, 220, x + 60, 260), "#1a3a2a")
        c.add(col("#5cff9a"), m * .4)
        c.line((x + 80, 300), (x + 120 if k == 0 else x - 40, 300), 4, "#5a6068")


def s_compressed_rations(c):
    c.img[:] = col("#07090d")
    c.glow_dot(560, 60, 260, "#ffe8c0", .1)
    for row in range(4):
        for k in range(14):
            x, y = 100 + k * 66 + (row % 2) * 33, 60 + row * 72
            c.hull([(x, y), (x + 56, y), (x + 56, y + 58), (x, y + 58)], y, y + 58, base="#7a8a5a" if (k + row) % 3 else "#8a7a5a", panel=60, spec=.3)
            c.line((x + 8, y + 18), (x + 48, y + 18), 3, "#c8c0a0")


def s_hydroponics(c):
    P = interior(c, "#0c140e", "#ffe8c0")
    for side in (-1, 1):
        for i in range(8):
            z = 1.2 + i * .8
            x0, y0 = P(z, 60, side)
            x1, y1 = P(z + .5, 60, side)
            m = c.poly([(x0, y0), (x1, y1), (x1, y1 - 70 / z), (x0, y0 - 70 / z)], "#0e2a14", .9)
            leaves = noise(H, W, 3, 40 + i, 3)
            c.add(col("#6ae08a"), m * ss(leaves, .45, .8) * .4 / (1 + i * .3), bloom=False)
            c.glow_dot((x0 + x1) / 2, y0 - 90 / z, 4 / z * 3, "#ff8ad8", .3 / (1 + i * .3))


def s_nutrition_science(c):
    c.img[:] = col("#07090d")
    c.glow_dot(560, 190, 300, "#bfe6ff", .08)
    for k in range(5):
        x = 220 + k * 160
        c.ellipse((x, 220, x + 100, 250), "#c8ccd2")
        c.ellipse((x + 10, 210, x + 90, 240), "#e8ecf0")
        c.ellipse((x + 30, 200, x + 70, 226), ["#7aa04a", "#c08a4a", "#c0603a", "#d8c060", "#6a9a8a"][k])
    for k in range(6):
        c.glow_dot(180 + k * 150, 120, 3, "#5cff9a", .5)


def colony(c, g=300, n=6, seed=0, lit=.7):
    for k in range(n):
        x = 180 + k * 130
        h = 50 + (k * 37 % 60)
        building(c, x, g, 90, h, seed=seed + k, roof="dome" if k % 3 == 0 else None, lit=lit)


def s_public_services(c):
    surface(c, 300, body=lambda: c.planet(950, 80, 50, "gas", light=(-.6, .3, .6), atmo="#ffd8a0", atmo_w=.03))
    colony(c, 300, 5)
    building(c, 500, 300, 120, 60, seed=9, roof="dome")
    c.poly([(552, 254), (568, 254), (568, 262), (552, 262)], "#ffffff")
    c.poly([(556, 250), (564, 250), (564, 266), (556, 266)], "#ffffff")
    c.glow_dot(560, 258, 10, "#5cff9a", .4)


def s_frontier_medicine(c):
    P = interior(c, "#10161c", "#e8f4ff")
    for k, x in enumerate((300, 640)):
        c.hull([(x, 280), (x + 180, 280), (x + 180, 300), (x, 300)], 280, 300, base="#c8ccd2", panel=40)
        c.line((x + 30, 300), (x + 30, 340), 3, "#5a6068")
        c.line((x + 150, 300), (x + 150, 340), 3, "#5a6068")
        c.poly([(x + 160, 200), (x + 200, 200), (x + 200, 250), (x + 160, 250)], "#0a1a14")
        X = c.x
        wave = 225 + 10 * np.sin((X - x) / 3) * (np.abs((X - x - 180) % 20 - 10) < 2)
        c.add(col("#5cff9a"), np.exp(-((c.y - wave) / 1) ** 2) * (X > x + 162) * (X < x + 198) * .8)


def s_civic_planning(c):
    surface(c, 290, body=lambda: c.planet(220, 80, 40, "moon", light=(.6, .3, .6), atmo=None))
    for k in range(8):
        x = 80 + k * 125
        building(c, x, 290, 100, 40 + (k % 3) * 25, seed=k, roof="dome" if k % 2 else None, lit=.8)
    for k in range(30):
        c.glow_dot(60 + k * 35, 300, 1.5, "#ffd28a", .6)
    greenhouse(c, 480, 290, 160, 70)


def s_crew_drills(c):
    P = interior(c, "#121216", "#ffb07a")
    for k in range(4):
        person(c, 380 + k * 110, 330, 110 - k * 6, "#0c0e12", .9)
    c.glow_dot(560, 60, 30, "#ff4a3a", .5)


def s_shore_leave(c):
    surface(c, 300, ground="#0e1410", top="#3a4a3a", haze="#9fd8ff", body=lambda: c.planet(560, 120, 90, "earth", light=(.4, -.5, .7), atmo="#7fc0ff", atmo_w=.04))
    dome(c, 560, 300, 300, "#9fd8ff", s=1.0)
    greenhouse(c, 300, 300, 140, 60, seed=1)
    for k in range(5):
        person(c, 480 + k * 40, 300, 34, "#1a2028", .9)
    c.lights([(320 + k * 60, 300) for k in range(9)], "#ffd28a", 1.4, .7)


def s_compact_housing(c):
    surface(c, 310)
    for k in range(10):
        x = 120 + k * 90
        building(c, x, 310, 76, 110 + (k % 4) * 20, seed=k, lit=.8)


def s_scientist_training(c):
    P = interior(c, "#10141a", "#e8f4ff")
    c.poly([(380, 70), (740, 70), (740, 220), (380, 220)], "#0a1622")
    for k in range(6):
        c.line((400, 90 + k * 20), (400 + (k * 53 % 300), 90 + k * 20), 1.5, "#7cc4ff", glow=True, s=.5)
    for k in range(5):
        person(c, 380 + k * 90, 360, 80, "#0c0e12", .9)


def s_sealed_habitats(c):
    surface(c, 300, ground="#1a1612", top="#7a7068", body=lambda: c.planet(950, 90, 60, "rust", light=(-.6, .3, .6), atmo="#ffb08a", atmo_w=.03))
    for k, (x, r) in enumerate(((320, 80), (520, 110), (720, 70))):
        greenhouse(c, x - r, 300, r * 2, r * .9, seed=k)
        dome(c, x, 300, r, "#9fd8ff", s=.8)


def s_radiation_shielding(c):
    space(c, ("#3a3a1a", "#1a2a4a"), .4)
    c.planet(-60, 200, 220, "gas", light=(.8, .1, .5), atmo="#ffd8a0", atmo_w=.03)
    X, Y = c.x, c.y
    d = np.sqrt(((X + 60) / 1.0) ** 2 + ((Y - 200) / .6) ** 2)
    c.add(col("#c8a0ff"), np.exp(-((d - 420) / 60) ** 2) * noise(H, W, 30, 3, 4) * .35, bloom=False)
    ship(c, "frigate", 560, 190, 1.0)
    shield(c, 690, 190, 190, 60, "#ffd08a", .5)


def s_acceleration_couches(c):
    P = interior(c, "#10141a", "#bfe6ff")
    for k in range(3):
        x = 300 + k * 200
        c.poly([(x, 330), (x + 140, 300), (x + 150, 316), (x + 10, 346)], "#3a4250")
        c.poly([(x + 120, 300), (x + 150, 240), (x + 166, 250), (x + 140, 312)], "#3a4250")
        person(c, x + 90, 316, 60, "#0a0c10", .7)


def s_closed_loop(c):
    space(c, ("#1a3a3a", "#2a2a4a"))
    station(c, 560, 190, 200)
    X, Y = c.x, c.y
    dx, dy = (X - 560) / 200, (Y - 190) / 64
    d = np.sqrt(dx * dx + dy * dy)
    c.add(col("#5cd07a"), np.exp(-((d - 1) / .05) ** 2) * (dy > 0) * .3)


def s_food_vats(c):
    c.img[:] = col("#06080c")
    for k in range(5):
        x = 140 + k * 180
        m = c.poly([(x, 80), (x + 120, 80), (x + 120, 320), (x, 320)], "#0a1a14")
        v = np.clip((c.y - 80) / 240, 0, 1)
        c.add(col("#5ce08a"), m * (.15 + .35 * v) * (.6 + .4 * noise(H, W, 6, k, 3)))
        c.hull([(x - 6, 70), (x + 126, 70), (x + 126, 84), (x - 6, 84)], 70, 84, base="#8a949e", panel=30)
        c.hull([(x - 6, 316), (x + 126, 316), (x + 126, 330), (x - 6, 330)], 316, 330, base="#8a949e", panel=30)
        for j in range(8):
            rr = rng(k * 8 + j)
            c.glow_dot(x + 20 + rr.random() * 80, 120 + rr.random() * 190, 2, "#bfffd0", .4)


def s_low_g_medicine(c):
    space(c, ("#2a2a3a", "#1a2a4a"))
    c.planet(560, 300, 150, "moon", light=(.5, -.6, .5), atmo=None, seed=4)
    for k, (x, y) in enumerate(((470, 190), (560, 170), (640, 196))):
        dome(c, x, y + 14, 22, "#9fd8ff", s=1.2)
        c.lights([(x, y + 6)], "#ffd28a", 2, .9)


def s_marines(c):
    surface(c, 310, ground="#1a1410", top="#6a5a48", body=lambda: c.planet(900, 80, 50, "gas", light=(-.6, .3, .6), atmo="#ffd8a0", atmo_w=.03))
    ship(c, "escort", 140, 120, .7, plume="#ffb07a", plume_len=.4)
    for k in range(6):
        person(c, 380 + k * 70, 310, 80, "#0c1014", .95)
        c.glow_dot(380 + k * 70, 240, 2, "#5cff9a", .6)


def s_hospital_ships(c):
    space(c, ("#1a3a4a", "#2a2a4a"))
    a = ship(c, "cruiser", 160, 200, 1.3, base="#d8dce2")
    for x in (540, 700):
        c.poly([(x - 18, 192), (x + 18, 192), (x + 18, 208), (x - 18, 208)], "#c03a3a")
        c.poly([(x - 8, 180), (x + 8, 180), (x + 8, 220), (x - 8, 220)], "#c03a3a")
    ship(c, "escort", 860, 90, .6, base="#8a8478", lights=False)


def s_underground_habitats(c):
    c.sky(tint=("#121828", "#06080e"), neb=("#2a3a6a", "#4a2a5a"), neb_amt=.3, stars=900)
    c.poly([(0, 384), (0, 200), (200, 120), (380, 180), (560, 90), (760, 160), (940, 110), (1120, 190), (1120, 384)], "#2a241e")
    n = noise(H, W, 30, 4, 5)
    m = c.mask(lambda d, s: d.polygon([(px * s, py * s) for px, py in [(0, 384), (0, 200), (200, 120), (380, 180), (560, 90), (760, 160), (940, 110), (1120, 190), (1120, 384)]], fill=255))
    c.over(col("#4a4036") * (.6 + .6 * n[..., None]), m)
    rr = rng(3)
    for k in range(70):
        x, y = rr.random() * W, 200 + rr.random() * 170
        c.add(col("#ffcf8a"), c.mask(lambda d, s, x=x, y=y: d.rectangle([x * s, y * s, (x + 3) * s, (y + 2) * s], fill=255)) * .8)
    c.poly([(520, 384), (520, 330), (600, 330), (600, 384)], "#0a0806")
    c.glow_dot(560, 360, 20, "#ffcf8a", .4)


def s_arcologies(c):
    surface(c, 320, ground="#10140e", top="#3a4a38", body=lambda: c.planet(220, 70, 40, "moon", light=(.6, .3, .6), atmo=None))
    c.hull([(460, 320), (520, 60), (600, 60), (660, 320)], 60, 320, base="#8a949e", panel=14, grime=.3)
    rr = rng(2)
    for k in range(260):
        y = rr.uniform(70, 315)
        half = 30 + (y - 60) / 260 * 100
        x = 560 + rr.uniform(-half * .9, half * .9)
        c.add(col("#ffcf8a"), c.mask(lambda d, s, x=x, y=y: d.rectangle([x * s, y * s, (x + 3) * s, (y + 1.5) * s], fill=255)) * .9)
    dome(c, 560, 64, 40, "#9fd8ff")
    for k in range(3):
        building(c, 200 + k * 90, 320, 60, 40, seed=k, lit=.5)
        building(c, 760 + k * 90, 320, 60, 50, seed=k + 5, lit=.5)


def s_g_softening(c):
    space(c, ("#3a1a5a", "#1a3a5a"), .5)
    ship(c, "cruiser", 380, 200, 1.0, plume="#d8ecff", plume_len=1.6)
    X, Y = c.x, c.y
    dx, dy = (X - 540) / 140, (Y - 200) / 40
    d = np.sqrt(dx * dx + dy * dy)
    for k in range(4):
        c.add(col("#c08aff"), np.exp(-((d - 1 - k * .25) / .02) ** 2) * .4)


def s_terraforming(c):
    space(c, ("#1a3a5a", "#3a2a4a"), .35)
    c.sun(40, 60, 16, strength=1.2)
    c.planet(640, 200, 150, "terra", light=(-.85, -.3, .45), rot=-.6, atmo="#9fd0ff", atmo_w=.04, terra=.40)
    c.poly([(300, 70), (360, 40), (372, 52), (312, 84)], "#e6eef8", .95)
    c.line((336, 62), (540, 160), 6, "#fff2d0", glow=True, s=.15)
    c.line((336, 62), (540, 160), 1.5, "#fff8e8", glow=True, s=.5)
    c.glow_dot(540, 160, 8, "#fff2d0", .6)
    c.planet(940, 300, 18, "ice", light=(-.9, -.2, .3), atmo="#bfe6ff", atmo_w=.15, seed=9)
    c.plume(925, 300, 160, 10, "#bfe6ff", .35)
    c.hull([(960, 294), (990, 294), (994, 306), (960, 306)], 294, 306, base="#a0aab8", panel=8)
    c.glow_dot(1000, 300, 5, "#9fd8ff", .8)


# ---- Main ------------------------------------------------------------------------------------------

def scenes():
    return {k[2:].replace("_", "-"): v for k, v in globals().items() if k.startswith("s_") and callable(v)}


def main():
    args = sys.argv[1:]
    only = set(args[args.index("--only") + 1].split(",")) if "--only" in args else None
    sheet = args[args.index("--sheet") + 1] if "--sheet" in args else None
    data = json.load(open(os.path.join("src", "Lightlag.Sim", "Data", "economy.json")))
    ids = [r["id"] for r in data["research"]]
    sc = scenes()
    made = []
    for n, i in enumerate(ids):
        if i not in sc or (only and i not in only):
            continue
        c = Canvas(sum(ord(ch) * (k + 1) for k, ch in enumerate(i)) % 100000)
        sc[i](c)
        im = c.finish()
        im.save(os.path.join(OUT, i + ".webp"), quality=84, method=6)
        made.append((i, im))
    missing = [i for i in ids if i not in sc]
    print(f"painted {len(made)}; {len(missing)} ideas without a scene: {', '.join(missing)}")
    if sheet and made:
        cols = 3
        tw, th = 560, 192
        rows = (len(made) + cols - 1) // cols
        S = Image.new("RGB", (cols * (tw + 6), rows * (th + 22)), (20, 20, 24))
        from PIL import ImageDraw as D
        d = D.Draw(S)
        for k, (i, im) in enumerate(made):
            x, y = (k % cols) * (tw + 6), (k // cols) * (th + 22)
            S.paste(im.resize((tw, th), Image.LANCZOS), (x, y + 20))
            d.text((x + 4, y + 4), i, fill=(220, 220, 220))
        S.save(sheet)


if __name__ == "__main__":
    main()
