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
from monoread import script_ptr, HEADER
from tk2dparse import Tk2dReader
from fsmvalidate import parse_fsm_component
from fsmbake import write_fsm, FSM_MAGIC, FSM_VERSION

MAGIC = b"GGBS"
VERSION = 2

HK = os.path.expanduser(
    "~/Downloads/Hollow Knight.app/Contents/SharedSupport/prefix/drive_c/"
    "GOG Games/Hollow Knight/Hollow Knight_Data")
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "SilksongGodhome", "Baked")


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

    w.i32(len(fsms))
    for f in fsms:
        write_fsm(w, f)

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
