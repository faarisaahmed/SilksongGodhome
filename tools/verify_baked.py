#!/usr/bin/env python3
"""
Read a baked .scene back the way GodhomeData.cs does and sanity-check it.

The C# side uses a plain BinaryReader, so this mirrors it field for field: any
mismatch between writer and reader shows up here as a short read, a bad magic, or a
count that runs off the end - rather than as a crash inside the game.

    python3 verify_baked.py ../SilksongGodhome/Baked/GG_Atrium.scene
"""

import os
import struct
import sys

from ggformat import (MAGIC, FORMAT_VERSION, HAS_SPRITE, HAS_BOX, HAS_EDGE, HAS_POLY,
                      HAS_CAMLOCK, HAS_RESPAWN, HAS_HAZARD,
                      HAS_TRANSITION, HAS_SIMPLE,
                      HAS_MESH, HAS_SEQDOOR, HAS_STATUE,
                      HAS_AUDIO)


class Reader:
    def __init__(self, data):
        self.d = data
        self.i = 0

    def take(self, n):
        if self.i + n > len(self.d):
            raise EOFError(f"short read at offset {self.i}: wanted {n}, "
                           f"{len(self.d) - self.i} left")
        b = self.d[self.i:self.i + n]
        self.i += n
        return b

    def i32(self):  return struct.unpack("<i", self.take(4))[0]
    def f32(self):  return struct.unpack("<f", self.take(4))[0]
    def boolean(self): return struct.unpack("<?", self.take(1))[0]

    def string(self):
        n = 0
        shift = 0
        while True:
            b = self.take(1)[0]
            n |= (b & 0x7F) << shift
            if not (b & 0x80):
                break
            shift += 7
        return self.take(n).decode("utf-8")


def verify(path):
    data = open(path, "rb").read()
    r = Reader(data)

    magic = r.take(4)
    assert magic == MAGIC, f"bad magic {magic!r}"
    version = r.i32()
    assert version == FORMAT_VERSION, f"version {version} != {FORMAT_VERSION}"
    name = r.string()
    bounds = (r.f32(), r.f32())
    if r.boolean():
        r.i32(); r.f32()
        [r.f32() for _ in range(4)]; r.f32(); [r.f32() for _ in range(4)]
        for _ in range(3):
            for _ in range(r.i32()):
                [r.f32() for _ in range(4)]
    shaders = [r.string() for _ in range(r.i32())]
    clips = [(r.string(), r.i32(), r.i32()) for _ in range(r.i32())]

    pages = [r.string() for _ in range(r.i32())]

    nsprites = r.i32()
    sprites = []
    for _ in range(nsprites):
        s = {
            "name": r.string(),
            "page": r.i32(),
            "rect": (r.f32(), r.f32(), r.f32(), r.f32()),
            "pivot": (r.f32(), r.f32()),
            "ppu": r.f32(),
            "border": (r.f32(), r.f32(), r.f32(), r.f32()),
        }
        sprites.append(s)

    nobj = r.i32()
    stats = {"sprite": 0, "box": 0, "edge": 0, "poly": 0, "inactive": 0,
             "camlock": 0, "respawn": 0, "hazard": 0, "transition": 0, "seqdoor": 0, "statue": 0, "audio": 0, "mesh": 0, "meshverts": 0}
    respawn_names = []
    exits = set()
    parents_ok = True
    for i in range(nobj):
        oname = r.string()
        parent = r.i32()
        r.i32()                    # layer
        if not r.boolean():
            stats["inactive"] += 1
        [r.f32() for _ in range(3)]   # pos
        [r.f32() for _ in range(4)]   # rot
        [r.f32() for _ in range(3)]   # scale
        mask = r.i32()

        if parent >= i:
            parents_ok = False      # must be strictly parent-before-child

        if mask & HAS_SPRITE:
            si = r.i32()
            sh = r.i32()
            assert -1 <= sh < len(shaders), f"object {i} '{oname}' shader index {sh} out of range"
            [r.f32() for _ in range(4)]
            r.i32(); r.i32()
            r.boolean(); r.boolean(); r.boolean()
            assert 0 <= si < nsprites, f"object {i} '{oname}' sprite index {si} out of range"
            stats["sprite"] += 1
        if mask & HAS_BOX:
            n = r.i32()
            for _ in range(n):
                [r.f32() for _ in range(4)]
                r.boolean(); r.boolean()
            stats["box"] += n
        if mask & HAS_EDGE:
            n = r.i32()
            for _ in range(n):
                r.f32(); r.f32()
                for _ in range(r.i32()):
                    r.f32(); r.f32()
                r.boolean(); r.boolean()
            stats["edge"] += n
        if mask & HAS_POLY:
            n = r.i32()
            for _ in range(n):
                r.f32(); r.f32()
                for _ in range(r.i32()):
                    for _ in range(r.i32()):
                        r.f32(); r.f32()
                r.boolean(); r.boolean()
            stats["poly"] += n
        if mask & HAS_CAMLOCK:
            [r.f32() for _ in range(4)]
            r.boolean(); r.boolean(); r.boolean()
            stats["camlock"] += 1
        if mask & HAS_RESPAWN:
            r.boolean()
            stats["respawn"] += 1
            respawn_names.append(oname)
        if mask & HAS_HAZARD:
            r.boolean()
            stats["hazard"] += 1
        if mask & HAS_SEQDOOR:
            r.string(); r.string()
            r.i32(); r.i32(); r.i32()
            stats["seqdoor"] += 1
        if mask & HAS_AUDIO:
            r.i32(); r.f32(); r.f32(); r.f32()
            r.boolean(); r.boolean(); r.boolean()
            stats["audio"] += 1
        if mask & HAS_STATUE:
            r.string(); r.string()
            stats["statue"] += 1
        if mask & HAS_MESH:
            vn = r.i32()
            for _ in range(vn):
                [r.f32() for _ in range(5)]
            tn = r.i32()
            for _ in range(tn * 3):
                r.i32()
            r.i32(); r.i32(); r.i32(); r.i32(); r.boolean()
            stats["mesh"] += 1
            stats["meshverts"] += vn
        if mask & HAS_SIMPLE:
            for _ in range(r.i32()):
                r.string()
        if mask & HAS_TRANSITION:
            tgt = r.string(); r.string()
            r.f32(); r.f32(); r.f32()
            for _ in range(6): r.boolean()
            stats["transition"] += 1
            if tgt: exits.add(tgt)

    leftover = len(data) - r.i
    print(f"{os.path.basename(path)}")
    print(f"  scene name      {name}")
    print(f"  format version  {version}")
    print(f"  scene bounds    {bounds[0]:.0f} x {bounds[1]:.0f} world units")
    print(f"  shaders         {len(shaders)}  {shaders}")
    print(f"  atlas pages     {len(pages)}  {pages}")
    print(f"  sprites         {nsprites}")
    print(f"  objects         {nobj}")
    print(f"  with sprite     {stats['sprite']}")
    print(f"  colliders       box {stats['box']} / edge {stats['edge']} / poly {stats['poly']}")
    print(f"  camera locks    {stats['camlock']}")
    print(f"  respawn markers {stats['respawn']}  {respawn_names}")
    print(f"  hazard markers  {stats['hazard']}")
    print(f"  transitions     {stats['transition']}  -> {sorted(exits)}")
    print(f"  pantheon doors  {stats['seqdoor']}")
    print(f"  boss statues    {stats['statue']}")
    print(f"  audio sources   {stats['audio']} on {len(clips)} clips")
    print(f"  meshes          {stats['mesh']} ({stats['meshverts']} verts)")
    print(f"  inactive        {stats['inactive']}")
    print(f"  parent ordering {'OK (parent always precedes child)' if parents_ok else 'BROKEN'}")
    print(f"  trailing bytes  {leftover}")

    # Page files must exist next to the .scene, since the csproj globs them in.
    d = os.path.dirname(path)
    missing = [p for p in pages if not os.path.exists(os.path.join(d, p + ".png"))]
    print(f"  page files      {'all present' if not missing else 'MISSING ' + str(missing)}")

    # Rects must sit inside their page.
    try:
        from PIL import Image
        bad = 0
        dims = {p: Image.open(os.path.join(d, p + ".png")).size for p in pages}
        for s in sprites:
            w, h = dims[pages[s["page"]]]
            x, y, rw, rh = s["rect"]
            if x < 0 or y < 0 or x + rw > w + 0.5 or y + rh > h + 0.5:
                bad += 1
        print(f"  rects in bounds {'all OK' if not bad else f'{bad} OUT OF BOUNDS'}")
    except ImportError:
        pass

    ok = (leftover == 0 and parents_ok and not missing)
    print(f"  => {'PASS' if ok else 'FAIL'}")
    return 0 if ok else 1


if __name__ == "__main__":
    args = sys.argv[1:] or [os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                         "..", "SilksongGodhome", "Baked", "GG_Atrium.scene")]
    sys.exit(max(verify(a) for a in args))
