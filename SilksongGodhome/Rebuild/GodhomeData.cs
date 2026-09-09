using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace SilksongGodhome.Rebuild
{
    /// <summary>
    /// Reads the baked Godhome scene data embedded in this DLL.
    ///
    /// The format is written by tools/extract_godhome.py; see tools/ggformat.py for the
    /// authoritative layout. Strings are .NET BinaryWriter-compatible, so a plain
    /// BinaryReader is all that's needed here.
    ///
    /// <see cref="FormatVersion"/> must match ggformat.FORMAT_VERSION.
    /// </summary>
    internal static class GodhomeData
    {
        public const int FormatVersion = 11;
        private const string Magic = "GGHM";
        private const string ResourcePrefix = "Godhome.";

        // Component bits, mirroring ggformat.py.
        public const int HasSprite = 1 << 0;
        public const int HasBox    = 1 << 1;
        public const int HasEdge   = 1 << 2;
        public const int HasPoly   = 1 << 3;
        public const int HasCamLock = 1 << 4;
        public const int HasRespawn = 1 << 5;
        public const int HasHazard  = 1 << 6;
        public const int HasTransition = 1 << 7;
        public const int HasSimple = 1 << 8;
        public const int HasMesh = 1 << 9;
        public const int HasSeqDoor = 1 << 10;
        public const int HasStatue = 1 << 11;
        public const int HasAudio = 1 << 12;

        private static readonly Dictionary<string, BakedScene> Cache = new Dictionary<string, BakedScene>();
        private static HashSet<string> _available;

        // ------------------------------------------------------------------

        public sealed class SpriteDef
        {
            public string Name;
            public int Page;
            public Rect Rect;
            public Vector2 Pivot;
            public float Ppu;
            public Vector4 Border;
        }

        public sealed class BoxDef
        {
            public Vector2 Offset, Size;
            public bool Trigger, Enabled;
        }

        public sealed class EdgeDef
        {
            public Vector2 Offset;
            public Vector2[] Points;
            public bool Trigger, Enabled;
        }

        public sealed class PolyDef
        {
            public Vector2 Offset;
            public Vector2[][] Paths;
            public bool Trigger, Enabled;
        }

        public sealed class ObjectDef
        {
            public string Name;
            public int Parent;
            public int Layer;
            public bool Active;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
            public int Mask;

            // SpriteRenderer
            public int SpriteIndex;
            public int ShaderIndex;
            public Color Color;
            public int SortingOrder;
            public int SortingLayerId;
            public bool FlipX, FlipY, RendererEnabled;

            // Colliders. Plural on purpose: Hollow Knight's tilemap chunks carry
            // several EdgeCollider2Ds on a single GameObject, and they are the floor.
            public BoxDef[] Boxes;
            public EdgeDef[] Edges;
            public PolyDef[] Polys;

            // CameraLockArea
            public float CamXMin, CamYMin, CamXMax, CamYMax;
            public bool PreventLookUp, PreventLookDown, MaxPriority;

            // RespawnMarker / HazardRespawnMarker
            public bool RespawnFacingRight;
            public bool HazardFacingRight;

            /// <summary>Components attachable by name alone.</summary>
            public string[] SimpleComponents;

            // BossSequenceDoor - a Pantheon entrance. The three indices point at the
            // objects BossSequenceDoor.Start() would normally toggle.
            public string DoorPlayerData, DoorSequence;
            public int DoorLockSet, DoorUnlockedSet, DoorPrompt;

            // AudioSource
            public int ClipIndex;
            public float Volume, Pitch, SpatialBlend;
            public bool Loop, PlayOnAwake, AudioEnabled;

            // BossStatue - a Hall of Gods plinth.
            public string StatueBoss, StatueDream;

            // MeshFilter + MeshRenderer. The tilemap chunks are Godhome's floors and
            // walls, so these are the level's actual structure.
            public Vector3[] MeshVerts;
            public Vector2[] MeshUVs;
            public int[] MeshTris;
            public int MeshPage, MeshShaderIndex, MeshSortingOrder, MeshSortingLayerId;
            public bool MeshEnabled;

            // TransitionPoint
            public string TargetScene, EntryPoint;
            public Vector2 EntryOffset;
            public float EntryDelay;
            public bool IsADoor, DontWalkOutOfDoor, AlwaysEnterRight, AlwaysEnterLeft;
            public bool HardLandOnExit, NonHazardGate;
        }

        /// <summary>
        /// Hollow Knight's per-room colour grading, lifted from the scene's SceneManager.
        /// Silksong's CustomSceneManager has all of these under the same names.
        /// </summary>
        public sealed class Lighting
        {
            public int DarknessLevel;
            public float Saturation;
            public Color DefaultColor;
            public float DefaultIntensity;
            public Color HeroLightColor;
            public AnimationCurve Red, Green, Blue;
        }

        /// <summary>One of Godhome's sounds: mono 16-bit PCM at 22050 Hz.</summary>
        public sealed class ClipDef
        {
            public string Name;
            public int SampleCount;
            public int Rate;
        }

        public sealed class BakedScene
        {
            public string Name;
            public ClipDef[] Clips;
            public Lighting Light;

            /// <summary>
            /// Scene size in world units, read from Hollow Knight's tk2dTileMap.
            /// CameraController derives sceneWidth/sceneHeight and xLimit/yLimit from
            /// exactly these numbers.
            /// </summary>
            public float Width, Height;

            public string[] ShaderNames;
            public string[] PageNames;
            public SpriteDef[] Sprites;
            public ObjectDef[] Objects;
        }

        // ------------------------------------------------------------------

        /// <summary>Names of every scene baked into this build.</summary>
        public static HashSet<string> Available
        {
            get
            {
                if (_available != null) return _available;

                _available = new HashSet<string>(StringComparer.Ordinal);
                foreach (string res in Assembly.GetExecutingAssembly().GetManifestResourceNames())
                {
                    if (res.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                        res.EndsWith(".scene", StringComparison.Ordinal))
                    {
                        _available.Add(res.Substring(ResourcePrefix.Length,
                            res.Length - ResourcePrefix.Length - ".scene".Length));
                    }
                }

                if (_available.Count == 0)
                {
                    Plugin.Log.LogWarning(
                        "Godhome: no baked scenes are embedded in this DLL. Run " +
                        "tools/extract_godhome.py and rebuild.");
                }
                else
                {
                    Plugin.Log.LogInfo($"Godhome: {_available.Count} baked scene(s): {string.Join(", ", ToArray(_available))}");
                }
                return _available;
            }
        }

        private static string[] ToArray(HashSet<string> set)
        {
            var a = new string[set.Count];
            set.CopyTo(a);
            Array.Sort(a, StringComparer.Ordinal);
            return a;
        }

        public static bool HasScene(string name) => Available.Contains(name);

        public static BakedScene Load(string name)
        {
            if (Cache.TryGetValue(name, out BakedScene cached)) return cached;

            Stream s = Assembly.GetExecutingAssembly()
                               .GetManifestResourceStream(ResourcePrefix + name + ".scene");
            if (s == null)
            {
                Plugin.Log.LogError($"Godhome: no baked data for '{name}'.");
                return null;
            }

            try
            {
                using (s)
                using (var r = new BinaryReader(s))
                {
                    BakedScene scene = Read(r, name);
                    Cache[name] = scene;
                    return scene;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Godhome: baked data for '{name}' is unreadable: {e}");
                return null;
            }
        }

        /// <summary>
        /// Loads a baked clip into an AudioClip.
        ///
        /// The extractor stores mono 16-bit PCM, which is exactly what AudioClip.SetData
        /// wants once it's scaled to floats - so there's no decoder involved at runtime.
        /// </summary>
        public static AudioClip LoadClip(ClipDef def)
        {
            if (def == null || def.SampleCount <= 0) return null;

            Stream s = Assembly.GetExecutingAssembly()
                               .GetManifestResourceStream(ResourcePrefix + def.Name + ".pcm");
            if (s == null)
            {
                Plugin.Log.LogWarning($"Godhome: audio clip '{def.Name}' is missing from the DLL.");
                return null;
            }

            byte[] bytes;
            using (s)
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                bytes = ms.ToArray();
            }

            int count = Mathf.Min(def.SampleCount, bytes.Length / 2);
            if (count <= 0) return null;

            var data = new float[count];
            for (int i = 0; i < count; i++)
            {
                short v = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
                data[i] = v / 32768f;
            }

            AudioClip clip = AudioClip.Create(def.Name, count, 1, def.Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>Loads an atlas page PNG into a Texture2D.</summary>
        public static Texture2D LoadPage(string pageName)
        {
            Stream s = Assembly.GetExecutingAssembly()
                               .GetManifestResourceStream(ResourcePrefix + pageName + ".png");
            if (s == null)
            {
                Plugin.Log.LogError($"Godhome: atlas page '{pageName}' is missing from the DLL.");
                return null;
            }

            byte[] bytes;
            using (s)
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                bytes = ms.ToArray();
            }

            // Size is a placeholder - LoadImage resizes to whatever the PNG holds.
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false)
            {
                name = pageName,
                // Godhome's art is authored at a fixed pixel scale and never filtered
                // in-game; Bilinear keeps it from crawling when the camera moves.
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            if (!tex.LoadImage(bytes, markNonReadable: true))
            {
                Plugin.Log.LogError($"Godhome: atlas page '{pageName}' failed to decode.");
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            return tex;
        }

        // ------------------------------------------------------------------

        private static AnimationCurve ReadCurve(BinaryReader r)
        {
            int n = r.ReadInt32();
            var keys = new Keyframe[n];
            for (int i = 0; i < n; i++)
            {
                keys[i] = new Keyframe(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            }
            return new AnimationCurve(keys);
        }

        private static BakedScene Read(BinaryReader r, string expectedName)
        {
            var magic = new string(r.ReadChars(4));
            if (magic != Magic)
                throw new InvalidDataException($"bad magic '{magic}', expected '{Magic}'");

            int version = r.ReadInt32();
            if (version != FormatVersion)
                throw new InvalidDataException(
                    $"format version {version}, expected {FormatVersion} - re-run tools/extract_godhome.py");

            var scene = new BakedScene { Name = r.ReadString() };
            if (scene.Name != expectedName)
                Plugin.Log.LogWarning($"Godhome: '{expectedName}.scene' declares itself as '{scene.Name}'.");

            scene.Width = r.ReadSingle();
            scene.Height = r.ReadSingle();

            if (r.ReadBoolean())
            {
                var l = new Lighting
                {
                    DarknessLevel = r.ReadInt32(),
                    Saturation = r.ReadSingle(),
                    DefaultColor = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    DefaultIntensity = r.ReadSingle(),
                    HeroLightColor = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                };
                l.Red = ReadCurve(r);
                l.Green = ReadCurve(r);
                l.Blue = ReadCurve(r);
                scene.Light = l;
            }

            int shaderCount = r.ReadInt32();
            scene.ShaderNames = new string[shaderCount];
            for (int i = 0; i < shaderCount; i++) scene.ShaderNames[i] = r.ReadString();

            int clipCount = r.ReadInt32();
            scene.Clips = new ClipDef[clipCount];
            for (int i = 0; i < clipCount; i++)
            {
                scene.Clips[i] = new ClipDef
                {
                    Name = r.ReadString(),
                    SampleCount = r.ReadInt32(),
                    Rate = r.ReadInt32(),
                };
            }

            int pageCount = r.ReadInt32();
            scene.PageNames = new string[pageCount];
            for (int i = 0; i < pageCount; i++) scene.PageNames[i] = r.ReadString();

            int spriteCount = r.ReadInt32();
            scene.Sprites = new SpriteDef[spriteCount];
            for (int i = 0; i < spriteCount; i++)
            {
                scene.Sprites[i] = new SpriteDef
                {
                    Name = r.ReadString(),
                    Page = r.ReadInt32(),
                    Rect = new Rect(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    Pivot = new Vector2(r.ReadSingle(), r.ReadSingle()),
                    Ppu = r.ReadSingle(),
                    Border = new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                };
            }

            int objCount = r.ReadInt32();
            scene.Objects = new ObjectDef[objCount];
            for (int i = 0; i < objCount; i++)
            {
                var o = new ObjectDef
                {
                    Name = r.ReadString(),
                    Parent = r.ReadInt32(),
                    Layer = r.ReadInt32(),
                    Active = r.ReadBoolean(),
                    Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    Rotation = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    Scale = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                };
                o.Mask = r.ReadInt32();

                if ((o.Mask & HasSprite) != 0)
                {
                    o.SpriteIndex = r.ReadInt32();
                    o.ShaderIndex = r.ReadInt32();
                    o.Color = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    o.SortingOrder = r.ReadInt32();
                    o.SortingLayerId = r.ReadInt32();
                    o.FlipX = r.ReadBoolean();
                    o.FlipY = r.ReadBoolean();
                    o.RendererEnabled = r.ReadBoolean();
                }

                if ((o.Mask & HasBox) != 0)
                {
                    o.Boxes = new BoxDef[r.ReadInt32()];
                    for (int b = 0; b < o.Boxes.Length; b++)
                    {
                        o.Boxes[b] = new BoxDef
                        {
                            Offset = new Vector2(r.ReadSingle(), r.ReadSingle()),
                            Size = new Vector2(r.ReadSingle(), r.ReadSingle()),
                            Trigger = r.ReadBoolean(),
                            Enabled = r.ReadBoolean(),
                        };
                    }
                }

                if ((o.Mask & HasEdge) != 0)
                {
                    o.Edges = new EdgeDef[r.ReadInt32()];
                    for (int e = 0; e < o.Edges.Length; e++)
                    {
                        var def = new EdgeDef
                        {
                            Offset = new Vector2(r.ReadSingle(), r.ReadSingle()),
                        };
                        int n = r.ReadInt32();
                        def.Points = new Vector2[n];
                        for (int p = 0; p < n; p++)
                            def.Points[p] = new Vector2(r.ReadSingle(), r.ReadSingle());
                        def.Trigger = r.ReadBoolean();
                        def.Enabled = r.ReadBoolean();
                        o.Edges[e] = def;
                    }
                }

                if ((o.Mask & HasPoly) != 0)
                {
                    o.Polys = new PolyDef[r.ReadInt32()];
                    for (int y = 0; y < o.Polys.Length; y++)
                    {
                        var def = new PolyDef
                        {
                            Offset = new Vector2(r.ReadSingle(), r.ReadSingle()),
                        };
                        int paths = r.ReadInt32();
                        def.Paths = new Vector2[paths][];
                        for (int p = 0; p < paths; p++)
                        {
                            int n = r.ReadInt32();
                            var pts = new Vector2[n];
                            for (int q = 0; q < n; q++)
                                pts[q] = new Vector2(r.ReadSingle(), r.ReadSingle());
                            def.Paths[p] = pts;
                        }
                        def.Trigger = r.ReadBoolean();
                        def.Enabled = r.ReadBoolean();
                        o.Polys[y] = def;
                    }
                }

                if ((o.Mask & HasCamLock) != 0)
                {
                    o.CamXMin = r.ReadSingle();
                    o.CamYMin = r.ReadSingle();
                    o.CamXMax = r.ReadSingle();
                    o.CamYMax = r.ReadSingle();
                    o.PreventLookUp = r.ReadBoolean();
                    o.PreventLookDown = r.ReadBoolean();
                    o.MaxPriority = r.ReadBoolean();
                }

                if ((o.Mask & HasRespawn) != 0) o.RespawnFacingRight = r.ReadBoolean();
                if ((o.Mask & HasHazard) != 0) o.HazardFacingRight = r.ReadBoolean();

                if ((o.Mask & HasSeqDoor) != 0)
                {
                    o.DoorPlayerData = r.ReadString();
                    o.DoorSequence = r.ReadString();
                    o.DoorLockSet = r.ReadInt32();
                    o.DoorUnlockedSet = r.ReadInt32();
                    o.DoorPrompt = r.ReadInt32();
                }

                if ((o.Mask & HasAudio) != 0)
                {
                    o.ClipIndex = r.ReadInt32();
                    o.Volume = r.ReadSingle();
                    o.Pitch = r.ReadSingle();
                    o.SpatialBlend = r.ReadSingle();
                    o.Loop = r.ReadBoolean();
                    o.PlayOnAwake = r.ReadBoolean();
                    o.AudioEnabled = r.ReadBoolean();
                }

                if ((o.Mask & HasStatue) != 0)
                {
                    o.StatueBoss = r.ReadString();
                    o.StatueDream = r.ReadString();
                }

                if ((o.Mask & HasMesh) != 0)
                {
                    int vn = r.ReadInt32();
                    o.MeshVerts = new Vector3[vn];
                    o.MeshUVs = new Vector2[vn];
                    for (int v = 0; v < vn; v++)
                    {
                        o.MeshVerts[v] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        o.MeshUVs[v] = new Vector2(r.ReadSingle(), r.ReadSingle());
                    }
                    int tn = r.ReadInt32();
                    o.MeshTris = new int[tn * 3];
                    for (int t = 0; t < tn * 3; t++) o.MeshTris[t] = r.ReadInt32();
                    o.MeshPage = r.ReadInt32();
                    o.MeshShaderIndex = r.ReadInt32();
                    o.MeshSortingOrder = r.ReadInt32();
                    o.MeshSortingLayerId = r.ReadInt32();
                    o.MeshEnabled = r.ReadBoolean();
                }

                if ((o.Mask & HasSimple) != 0)
                {
                    o.SimpleComponents = new string[r.ReadInt32()];
                    for (int c = 0; c < o.SimpleComponents.Length; c++)
                        o.SimpleComponents[c] = r.ReadString();
                }

                if ((o.Mask & HasTransition) != 0)
                {
                    o.TargetScene = r.ReadString();
                    o.EntryPoint = r.ReadString();
                    o.EntryOffset = new Vector2(r.ReadSingle(), r.ReadSingle());
                    o.EntryDelay = r.ReadSingle();
                    o.IsADoor = r.ReadBoolean();
                    o.DontWalkOutOfDoor = r.ReadBoolean();
                    o.AlwaysEnterRight = r.ReadBoolean();
                    o.AlwaysEnterLeft = r.ReadBoolean();
                    o.HardLandOnExit = r.ReadBoolean();
                    o.NonHazardGate = r.ReadBoolean();
                }

                scene.Objects[i] = o;
            }

            return scene;
        }
    }
}
