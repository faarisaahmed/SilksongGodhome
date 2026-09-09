using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace SilksongGodhome.Godhome
{
    /// <summary>
    /// Reads a baked Hollow Knight boss.
    ///
    /// Written by tools/bossbake.py. Stage one carries what makes a boss exist and
    /// animate: its tk2d sprite collection (definitions + atlas) and its animation
    /// library. tk2dSpriteDefinition, tk2dSpriteCollectionData, tk2dSpriteAnimation,
    /// tk2dSpriteAnimationClip and tk2dSpriteAnimationFrame are byte-identical between
    /// Hollow Knight's tk2d and Silksong's TeamCherry.TK2D, so these rebuild as real
    /// tk2d assets rather than as an approximation.
    /// </summary>
    internal static class BossData
    {
        private const string Prefix = "Godhome.";
        private const string Magic = "GGBS";
        private const int Version = 2;

        public sealed class SpriteDef
        {
            public string Name;
            public int MaterialId;
            public Vector2 TexelSize;
            public Vector3[] Positions;
            public Vector2[] Uvs;
            public Vector3[] BoundsData;
            public Vector3[] UntrimmedBoundsData;
            public int[] Indices;
        }

        public sealed class Frame
        {
            public int SpriteId;
            public bool TriggerEvent;
            public string EventInfo;
            public int EventInt;
            public float EventFloat;
        }

        public sealed class Clip
        {
            public string Name;
            public float Fps;
            public int LoopStart;
            public int WrapMode;
            public Frame[] Frames;
        }

        public sealed class Boss
        {
            public string Name;
            public string Scene;
            public string CollectionName;
            public string[] Textures;
            public SpriteDef[] Defs;
            public Clip[] Clips;
            public System.Collections.Generic.List<FsmData.Fsm> Fsms =
                new System.Collections.Generic.List<FsmData.Fsm>();
        }

        public static Boss Load(string bossName)
        {
            string safe = Sanitize(bossName);
            Stream s = Assembly.GetExecutingAssembly()
                               .GetManifestResourceStream(Prefix + "boss_" + safe + ".boss");
            if (s == null)
            {
                Plugin.Log.LogWarning($"Godhome: no baked boss '{bossName}'.");
                return null;
            }

            try
            {
                using (s)
                using (var r = new BinaryReader(s))
                {
                    var magic = new string(r.ReadChars(4));
                    if (magic != Magic) throw new InvalidDataException($"bad magic '{magic}'");
                    int v = r.ReadInt32();
                    if (v != Version) throw new InvalidDataException($"version {v}, expected {Version}");

                    var b = new Boss
                    {
                        Name = r.ReadString(),
                        Scene = r.ReadString(),
                        CollectionName = r.ReadString(),
                    };

                    b.Textures = new string[r.ReadInt32()];
                    for (int i = 0; i < b.Textures.Length; i++) b.Textures[i] = r.ReadString();

                    b.Defs = new SpriteDef[r.ReadInt32()];
                    for (int i = 0; i < b.Defs.Length; i++)
                    {
                        var d = new SpriteDef
                        {
                            Name = r.ReadString(),
                            MaterialId = r.ReadInt32(),
                            TexelSize = new Vector2(r.ReadSingle(), r.ReadSingle()),
                        };
                        d.Positions = ReadV3(r);
                        d.Uvs = ReadV2(r);
                        d.BoundsData = ReadV3(r);
                        d.UntrimmedBoundsData = ReadV3(r);
                        int n = r.ReadInt32();
                        d.Indices = new int[n];
                        for (int k = 0; k < n; k++) d.Indices[k] = r.ReadInt32();
                        b.Defs[i] = d;
                    }

                    b.Clips = new Clip[r.ReadInt32()];
                    for (int i = 0; i < b.Clips.Length; i++)
                    {
                        var c = new Clip
                        {
                            Name = r.ReadString(),
                            Fps = r.ReadSingle(),
                            LoopStart = r.ReadInt32(),
                            WrapMode = r.ReadInt32(),
                        };
                        c.Frames = new Frame[r.ReadInt32()];
                        for (int k = 0; k < c.Frames.Length; k++)
                        {
                            c.Frames[k] = new Frame
                            {
                                SpriteId = r.ReadInt32(),
                                TriggerEvent = r.ReadBoolean(),
                                EventInfo = r.ReadString(),
                                EventInt = r.ReadInt32(),
                                EventFloat = r.ReadSingle(),
                            };
                        }
                        b.Clips[i] = c;
                    }

                    int nf = r.ReadInt32();
                    for (int i = 0; i < nf; i++) b.Fsms.Add(FsmData.ReadFsm(r));

                    int st = 0, ac = 0;
                    foreach (FsmData.Fsm f in b.Fsms)
                    {
                        st += f.States.Length;
                        foreach (FsmData.State s2 in f.States) ac += s2.Actions.ActionNames.Length;
                    }
                    Plugin.Log.LogInfo(
                        $"Godhome: loaded boss '{b.Name}' - {b.Defs.Length} sprites, {b.Clips.Length} clips, " +
                        $"{b.Fsms.Count} FSMs ({st} states, {ac} actions).");
                    return b;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Godhome: baked boss '{bossName}' is unreadable: {e}");
                return null;
            }
        }

        public static string Sanitize(string n)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in n ?? "")
                sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' ? c : '_');
            return sb.ToString();
        }

        private static Vector3[] ReadV3(BinaryReader r)
        {
            var a = new Vector3[r.ReadInt32()];
            for (int i = 0; i < a.Length; i++)
                a[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            return a;
        }

        private static Vector2[] ReadV2(BinaryReader r)
        {
            var a = new Vector2[r.ReadInt32()];
            for (int i = 0; i < a.Length; i++)
                a[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
            return a;
        }
    }
}
