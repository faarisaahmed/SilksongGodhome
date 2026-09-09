#!/usr/bin/env python3
"""
Bake a Hollow Knight boss into data the mod can rebuild.

Stage one: make the boss exist and animate. That means its tk2d sprite collection (the
sprite definitions and the atlas behind them) and its animation library. Both structures
are byte-identical between Hollow Knight's tk2d and Silksong's TeamCherry.TK2D, so they
can be rebuilt as real tk2dSpriteCollectionData / tk2dSpriteAnimation assets at runtime -
which also means Tk2dPlayAnimation actions in the boss's FSMs will have something real to
drive once those are wired up.

    python3 bossbake.py GG_Gruz_Mother "Giant Fly"
"""

import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ggformat import Writer
from hkassets import HKBuild
from monoread import script_ptr, HEADER, read_fields
from tk2dparse import Tk2dReader
from fsmvalidate import parse_fsm_component
import fsmbake
from fsmbake import write_fsm, FSM_MAGIC, FSM_VERSION
from audiobake import decode_clip

MAGIC = b"GGBS"
VERSION = 4

HK = os.path.expanduser(
    "~/Downloads/Hollow Knight.app/Contents/SharedSupport/prefix/drive_c/"
    "GOG Games/Hollow Knight/Hollow Knight_Data")
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "SilksongGodhome", "Baked")


# HealthManager's serialised head, down to hp. AudioEvent is clip + 3 floats.
HEALTH_HEAD = [
    ("audioPlayerPrefab", "pptr"),
    ("inv_clip", "pptr"), ("inv_pmin", "f32"), ("inv_pmax", "f32"), ("inv_vol", "f32"),
    ("blockHitPrefab", "pptr"), ("strikeNailPrefab", "pptr"), ("slashImpactPrefab", "pptr"),
    ("fireballHitPrefab", "pptr"), ("sharpShadowImpactPrefab", "pptr"),
    ("corpseSplatPrefab", "pptr"),
    ("dsw_clip", "pptr"), ("dsw_pmin", "f32"), ("dsw_pmax", "f32"), ("dsw_vol", "f32"),
    ("dmg_clip", "pptr"), ("dmg_pmin", "f32"), ("dmg_pmax", "f32"), ("dmg_vol", "f32"),
    ("smallGeoPrefab", "pptr"), ("mediumGeoPrefab", "pptr"), ("largeGeoPrefab", "pptr"),
    ("hp", "i32"),
]

DAMAGE_HERO = [("damageDealt", "i32"), ("hazardType", "i32")]


def class_of(scene, o, lvl):
    try:
        ms = scene.resolve(script_ptr(o.get_raw_data()), lvl)
        return ms.read_typetree().get("m_ClassName") if ms else None
    except Exception:
        return None


def bake(scene_name, boss_name, log=print):
    build = HKBuild(HK)
    idx = build.find_scene(scene_name)
    if idx is None:
        raise SystemExit(f"{scene_name} not in build")
    scene = build.load_scene(idx)
    lvl = scene.level_file

    names = {}
    for g in scene.scene_objects("GameObject"):
        try:
            names[g.path_id] = g.read_typetree().get("m_Name")
        except Exception:
            pass

    # Audio referenced by the boss's FSMs. These are the boss's own sounds - the buzz,
    # the charge, the slam - and they're the one referenced asset type that transfers
    # whole, so they're baked and re-linked by name on the other side.
    audio_seen = {}

    def resolve_audio(ptr):
        if not ptr or not ptr.get("m_PathID"):
            return ""
        key = (ptr.get("m_FileID"), ptr.get("m_PathID"))
        if key in audio_seen:
            return audio_seen[key]
        obj = scene.resolve(ptr, lvl)
        if obj is None or obj.type.name != "AudioClip":
            audio_seen[key] = ""
            return ""
        try:
            cname = obj.read_typetree().get("m_Name") or "clip"
        except Exception:
            cname = "clip"
        rname = "audio_" + "".join(c if c.isalnum() or c in "._-" else "_" for c in cname)
        decoded = decode_clip(obj)
        if decoded is None:
            log(f"    ! boss clip '{cname}' could not be decoded")
            audio_seen[key] = ""
            return ""
        pcm, count, rate = decoded
        apath = os.path.join(OUT, rname + ".pcm")
        if not os.path.exists(apath):
            os.makedirs(OUT, exist_ok=True)
            with open(apath, "wb") as af:
                af.write(pcm)
        audio_seen[key] = rname
        boss_clips[rname] = (count, rate)
        return rname

    boss_clips = {}
    fsmbake.ASSET_RESOLVER = resolve_audio

    # The boss's own FSMs - its behaviour.
    fsms = []
    for o in scene.scene_objects("MonoBehaviour"):
        raw = o.get_raw_data()
        if class_of(scene, o, lvl) != "PlayMakerFSM":
            continue
        gid = struct.unpack_from("<q", raw, 4)[0]
        if names.get(gid) != boss_name:
            continue
        try:
            fsm, used = parse_fsm_component(raw)
            if used != len(raw):
                log(f"  ! FSM on '{boss_name}' consumed {used} of {len(raw)}; skipping")
                continue
            fsms.append(fsm)
        except Exception as e:
            log(f"  ! FSM parse failed: {e!r}")

    coll_obj = anim_obj = None
    sprite_id = 0
    for o in scene.scene_objects("MonoBehaviour"):
        raw = o.get_raw_data()
        cn = class_of(scene, o, lvl)
        if cn not in ("tk2dSprite", "tk2dSpriteAnimator"):
            continue
        gid = struct.unpack_from("<q", raw, 4)[0]
        if names.get(gid) != boss_name:
            continue
        r = Tk2dReader(raw, HEADER)
        r.string()
        if cn == "tk2dSprite":
            coll_obj = scene.resolve(r.pptr(), lvl)
        else:
            anim_obj = scene.resolve(r.pptr(), lvl)

    if coll_obj is None or anim_obj is None:
        raise SystemExit(f"'{boss_name}' has no tk2d sprite collection / animator")

    def parse(obj, fn):
        raw = obj.get_raw_data()
        r = Tk2dReader(raw, HEADER)
        r.string()
        d = getattr(r, fn)()
        if r.i != len(raw):
            raise SystemExit(f"{fn} parse consumed {r.i} of {len(raw)} - layout is wrong")
        return d

    coll = parse(coll_obj, "sprite_collection")
    anim = parse(anim_obj, "sprite_animation")
    log(f"  collection '{coll['spriteCollectionName']}': {len(coll['spriteDefinitions'])} defs, "
        f"{len(coll['textures'])} texture(s)")
    log(f"  animation: {len(anim['clips'])} clips")
    if fsms:
        nst = sum(len(f["states"]) for f in fsms)
        nac = sum(len(s["actionData"]["actionNames"]) for f in fsms for s in f["states"])
        log(f"  FSMs: {len(fsms)} ({', '.join(f['name'] for f in fsms)}) "
            f"- {nst} states, {nac} actions")

    # -- textures ------------------------------------------------------
    #
    # Named after the sprite *collection*, not the boss: bosses that share a collection
    # share its atlas, and those atlases are the biggest thing in the bake. False Knight
    # and its Head are one 9.7 MB sheet; so are Mato and Oro.
    os.makedirs(OUT, exist_ok=True)
    safe = "".join(c if c.isalnum() or c in "._-" else "_" for c in boss_name)
    coll_safe = "".join(c if c.isalnum() or c in "._-" else "_"
                        for c in (coll["spriteCollectionName"] or boss_name))
    tex_names = []
    for i, tptr in enumerate(coll["textures"]):
        t = scene.resolve(tptr, scene.file_of(coll_obj))
        if t is None:
            continue
        rname = f"boss_atlas_{coll_safe}_{i}"
        path = os.path.join(OUT, rname + ".png")
        if os.path.exists(path):
            tex_names.append(rname)
            log(f"    texture {rname}.png (shared, already baked)")
            continue
        try:
            t.read().image.save(path, optimize=True)
            tex_names.append(rname)
            log(f"    texture {rname}.png ({os.path.getsize(path)//1024} KB)")
        except Exception as e:
            log(f"    ! texture {i} failed: {e!r}")

    # -- write ---------------------------------------------------------
    w = Writer()
    w.buf += MAGIC
    w.i32(VERSION)
    w.string(boss_name)
    w.string(scene_name)
    w.string(coll["spriteCollectionName"] or boss_name)

    w.i32(len(tex_names))
    for n in tex_names:
        w.string(n)

    defs = coll["spriteDefinitions"]
    w.i32(len(defs))
    for d in defs:
        w.string(d["name"])
        w.i32(d["materialId"])
        w.vec2(*d["texelSize"])
        for arr, wr in ((d["positions"], w.vec3), (d["uvs"], w.vec2),
                        (d["boundsData"], w.vec3), (d["untrimmedBoundsData"], w.vec3)):
            w.i32(len(arr))
            for v in arr:
                wr(*v)
        w.i32(len(d["indices"]))
        for i in d["indices"]:
            w.i32(i)

    clips = anim["clips"]
    w.i32(len(clips))
    for c in clips:
        w.string(c["name"])
        w.f32(c["fps"])
        w.i32(c["loopStart"])
        w.i32(c["wrapMode"])
        w.i32(len(c["frames"]))
        for fr in c["frames"]:
            w.i32(fr["spriteId"])
            w.boolean(fr["triggerEvent"])
            w.string(fr["eventInfo"] or "")
            w.i32(fr["eventInt"])
            w.f32(fr["eventFloat"])

    # -- hierarchy and components -------------------------------------
    #
    # A boss needs more than art: a Rigidbody2D for its FSM's SetVelocity2d actions to
    # push, its own collider as a hitbox, and the child "Hero Damager" that actually
    # hurts you (inactive until the FSM turns it on).
    tr = {}
    go = {}
    for o in scene.scene_objects("GameObject"):
        try:
            go[o.path_id] = o.read_typetree()
        except Exception:
            pass
    for o in scene.scene_objects("Transform"):
        try:
            tr[o.path_id] = o.read_typetree()
        except Exception:
            pass
    g2t = {}
    for pid, d in tr.items():
        g2t[(d.get("m_GameObject") or {}).get("m_PathID")] = pid

    boss_gid = None
    for pid, d in go.items():
        if d.get("m_Name") == boss_name:
            boss_gid = pid
            break

    def components(gpid):
        out = []
        for c in (go.get(gpid, {}).get("m_Component") or []):
            ptr = c.get("component") if isinstance(c, dict) else None
            o = scene.resolve(ptr, lvl) if ptr else None
            if o is not None:
                out.append(o)
        return out

    def write_node(gpid):
        d = go[gpid]
        t = tr.get(g2t.get(gpid)) or {}
        pos = t.get("m_LocalPosition") or {}
        rot = t.get("m_LocalRotation") or {}
        scl = t.get("m_LocalScale") or {}

        w.string(d.get("m_Name") or "")
        w.i32(int(d.get("m_Layer") or 0))
        w.boolean(bool(d.get("m_IsActive", True)))
        w.vec3(pos.get("x", 0.0), pos.get("y", 0.0), pos.get("z", 0.0))
        w.vec4(rot.get("x", 0.0), rot.get("y", 0.0), rot.get("z", 0.0), rot.get("w", 1.0))
        w.vec3(scl.get("x", 1.0), scl.get("y", 1.0), scl.get("z", 1.0))

        rb = None
        boxes = []
        circles = []
        health = None
        damage = None
        for o in components(gpid):
            n = o.type.name
            if n == "Rigidbody2D":
                rb = o.read_typetree()
            elif n == "BoxCollider2D":
                boxes.append(o.read_typetree())
            elif n == "CircleCollider2D":
                circles.append(o.read_typetree())
            elif n == "MonoBehaviour":
                raw2 = o.get_raw_data()
                cn = class_of(scene, o, lvl)
                if cn == "HealthManager":
                    try:
                        health = read_fields(raw2, HEALTH_HEAD)["hp"]
                    except Exception:
                        health = None
                elif cn == "DamageHero":
                    try:
                        damage = read_fields(raw2, DAMAGE_HERO)
                    except Exception:
                        damage = None

        w.boolean(rb is not None)
        if rb is not None:
            w.f32(rb.get("m_Mass", 1.0))
            w.f32(rb.get("m_GravityScale", 1.0))
            w.f32(rb.get("m_LinearDrag", 0.0))
            w.f32(rb.get("m_AngularDrag", 0.0))
            w.i32(int(rb.get("m_BodyType", 0)))
            w.i32(int(rb.get("m_Constraints", 0)))
            w.i32(int(rb.get("m_CollisionDetection", 0)))
            w.i32(int(rb.get("m_Interpolate", 0)))

        w.i32(len(boxes))
        for bx in boxes:
            off = bx.get("m_Offset") or {}
            size = bx.get("m_Size") or {}
            w.vec2(off.get("x", 0.0), off.get("y", 0.0))
            w.vec2(size.get("x", 1.0), size.get("y", 1.0))
            w.boolean(bool(bx.get("m_IsTrigger", False)))
            w.boolean(bool(bx.get("m_Enabled", True)))

        w.i32(len(circles))
        for cc in circles:
            off = cc.get("m_Offset") or {}
            w.vec2(off.get("x", 0.0), off.get("y", 0.0))
            w.f32(cc.get("m_Radius", 0.5))
            w.boolean(bool(cc.get("m_IsTrigger", False)))
            w.boolean(bool(cc.get("m_Enabled", True)))

        w.boolean(health is not None)
        if health is not None:
            w.i32(int(health))

        w.boolean(damage is not None)
        if damage is not None:
            w.i32(int(damage["damageDealt"]))
            w.i32(int(damage["hazardType"]))

        kids = []
        for c in ((tr.get(g2t.get(gpid)) or {}).get("m_Children") or []):
            cp = (tr.get(c["m_PathID"]) or {}).get("m_GameObject", {}).get("m_PathID")
            if cp and cp in go:
                kids.append(cp)
        w.i32(len(kids))
        for k in kids:
            write_node(k)

    write_node(boss_gid)

    # write_fsm resolves audio as it goes, so the clip table is written after it and the
    # reader seeks back - instead, FSMs are serialised into a scratch writer first.
    scratch = Writer()
    scratch.i32(len(fsms))
    for f in fsms:
        write_fsm(scratch, f)

    w.i32(len(boss_clips))
    for rname, (count, rate) in sorted(boss_clips.items()):
        w.string(rname)
        w.i32(count)
        w.i32(rate)
    if boss_clips:
        log(f"  audio: {len(boss_clips)} clips referenced by the FSMs")

    w.buf += scratch.bytes()

    path = os.path.join(OUT, f"boss_{safe}.boss")
    with open(path, "wb") as f:
        f.write(w.bytes())
    log(f"  -> {os.path.basename(path)} ({os.path.getsize(path)//1024} KB)")
    return path


def find_bosses(scene_name, log=print):
    """
    Objects in a scene that look like a boss: a HealthManager (so it can be killed) and a
    tk2dSpriteAnimator (so it has animation to bake).
    """
    build = HKBuild(HK)
    idx = build.find_scene(scene_name)
    if idx is None:
        return []
    scene = build.load_scene(idx)
    lvl = scene.level_file

    names = {}
    for g in scene.scene_objects("GameObject"):
        try:
            names[g.path_id] = g.read_typetree().get("m_Name")
        except Exception:
            pass

    have = {}
    for o in scene.scene_objects("MonoBehaviour"):
        cn = class_of(scene, o, lvl)
        if cn not in ("HealthManager", "tk2dSpriteAnimator"):
            continue
        gid = struct.unpack_from("<q", o.get_raw_data(), 4)[0]
        have.setdefault(gid, set()).add(cn)

    out = []
    for gid, kinds in have.items():
        if {"HealthManager", "tk2dSpriteAnimator"} <= kinds:
            n = names.get(gid)
            if n:
                out.append(n)
    return sorted(set(out))


def main():
    if len(sys.argv) >= 2 and sys.argv[1] == "--find":
        for scene in sys.argv[2:]:
            print(f"{scene}: {find_bosses(scene)}")
        return 0

    if len(sys.argv) >= 2 and sys.argv[1] == "--auto":
        total = 0
        for scene in sys.argv[2:]:
            for b in find_bosses(scene):
                print(f"[{b} in {scene}]")
                try:
                    bake(scene, b)
                    total += 1
                except SystemExit as e:
                    print(f"  ! {e}")
        print(f"\nbaked {total} boss(es)")
        return 0

    if len(sys.argv) < 3:
        print(__doc__)
        return 1
    print(f"[{sys.argv[2]} in {sys.argv[1]}]")
    bake(sys.argv[1], sys.argv[2])
    return 0


if __name__ == "__main__":
    sys.exit(main())
