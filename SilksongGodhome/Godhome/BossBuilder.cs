using System;
using System.Collections.Generic;
using UnityEngine;

namespace SilksongGodhome.Godhome
{
    /// <summary>
    /// Rebuilds a Hollow Knight boss as live tk2d assets.
    ///
    /// The whole approach rests on one fact: tk2d's serialised structures are identical
    /// between Hollow Knight's copy and Silksong's TeamCherry.TK2D. So the collection and
    /// animation library are reconstructed as *real* tk2dSpriteCollectionData and
    /// tk2dSpriteAnimation objects - which matters beyond looks, because every
    /// Tk2dPlayAnimation action in the boss's FSMs drives a real tk2dSpriteAnimator.
    /// </summary>
    internal static class BossBuilder
    {
        private static readonly Dictionary<string, tk2dSpriteCollectionData> CollectionCache =
            new Dictionary<string, tk2dSpriteCollectionData>(StringComparer.Ordinal);
        private static readonly Dictionary<string, tk2dSpriteAnimation> LibraryCache =
            new Dictionary<string, tk2dSpriteAnimation>(StringComparer.Ordinal);

        /// <summary>Spawns a boss at a position. Returns the GameObject, or null.</summary>
        public static GameObject Spawn(string bossName, Vector3 position, string clip = null)
        {
            BossData.Boss boss = BossData.Load(bossName);
            if (boss == null) return null;

            try
            {
                tk2dSpriteCollectionData collection = BuildCollection(boss);
                if (collection == null) return null;

                tk2dSpriteAnimation library = BuildLibrary(boss, collection);

                BossData.Node root = boss.Root;

                var go = new GameObject("Godhome_" + boss.Name);
                go.transform.position = position;
                if (root != null)
                {
                    go.layer = root.Layer;
                    go.transform.localScale = root.Scale;
                    go.transform.localRotation = root.Rotation;
                }

                // AddComponent(go, collection, spriteId) is tk2d's own entry point; it
                // wires the mesh, material and bounds the way the engine expects.
                tk2dSprite sprite = tk2dSprite.AddComponent(go, collection, 0);
                if (sprite == null)
                {
                    Plugin.Log.LogError("Godhome: tk2dSprite.AddComponent returned null.");
                    UnityEngine.Object.Destroy(go);
                    return null;
                }

                if (library != null)
                {
                    var animator = go.AddComponent<tk2dSpriteAnimator>();
                    animator.Library = library;

                    string want = clip;
                    if (string.IsNullOrEmpty(want) && library.clips.Length > 0)
                    {
                        // Prefer an idle-ish clip so a spawned boss isn't mid-attack.
                        want = PickIdleClip(library);
                    }
                    if (!string.IsNullOrEmpty(want))
                    {
                        animator.Play(want);
                        Plugin.Log.LogInfo($"Godhome: '{boss.Name}' playing '{want}'.");
                    }
                }

                // Physics, hitbox and children before behaviour: the FSM's very first
                // state pushes velocity and activates the damager, so those have to exist.
                if (root != null)
                {
                    ApplyPhysics(go, root);
                    foreach (BossData.Node child in root.Children) BuildChild(go.transform, child);
                }

                // Behaviour last, so the animator and sprite already exist when the FSM's
                // first state runs.
                if (boss.Fsms.Count > 0)
                {
                    int built = FsmBuilder.Attach(go, boss.Fsms);
                    Plugin.Log.LogInfo($"Godhome: attached {built}/{boss.Fsms.Count} FSM(s) to '{boss.Name}'.");

                    // The arena normally supplies the events that start a fight; a
                    // spawned boss has no arena, so BossWaker sends them instead.
                    go.AddComponent<BossWaker>();
                }

                Plugin.Log.LogInfo($"Godhome: spawned '{boss.Name}' at {position}.");
                return go;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Godhome: couldn't spawn '{bossName}': {e}");
                return null;
            }
        }

        /// <summary>
        /// Rigidbody, colliders, health and damage - the difference between a picture of
        /// a boss and an enemy. Values are Hollow Knight's own: Gruz Mother is a dynamic
        /// body with zero gravity and frozen rotation, a 3.52 x 1.52 body hitbox, and a
        /// separate trigger on a child that deals the contact damage.
        /// </summary>
        private static void ApplyPhysics(GameObject go, BossData.Node n)
        {
            if (n.HasBody)
            {
                var rb = go.AddComponent<Rigidbody2D>();
                rb.mass = n.Mass;
                rb.gravityScale = n.GravityScale;
                rb.linearDamping = n.LinearDrag;
                rb.angularDamping = n.AngularDrag;
                rb.bodyType = (RigidbodyType2D)n.BodyType;
                rb.constraints = (RigidbodyConstraints2D)n.Constraints;
                rb.collisionDetectionMode = (CollisionDetectionMode2D)n.CollisionDetection;
                rb.interpolation = (RigidbodyInterpolation2D)n.Interpolate;
            }

            foreach (BossData.BoxDef b in n.Boxes)
            {
                var c = go.AddComponent<BoxCollider2D>();
                c.offset = b.Offset;
                c.size = b.Size;
                c.isTrigger = b.Trigger;
                c.enabled = b.Enabled;
            }

            foreach (BossData.CircleDef cd in n.Circles)
            {
                var c = go.AddComponent<CircleCollider2D>();
                c.offset = cd.Offset;
                c.radius = cd.Radius;
                c.isTrigger = cd.Trigger;
                c.enabled = cd.Enabled;
            }

            if (n.HasHealth)
            {
                var hm = go.AddComponent<HealthManager>();
                ComponentInitSafe(hm);
                hm.hp = n.Hp;
            }

            if (n.HasDamage)
            {
                var dh = go.AddComponent<DamageHero>();
                ComponentInitSafe(dh);
                dh.damageDealt = n.DamageDealt;
                dh.hazardType = (GlobalEnums.HazardType)n.HazardType;
            }
        }

        private static void ComponentInitSafe(Component c)
        {
            // Same trap as everywhere else: AddComponent leaves [Serializable] fields and
            // arrays null, and this game's code rarely checks.
            Rebuild.ComponentInit.FillNulls(c);
        }

        private static void BuildChild(Transform parent, BossData.Node n)
        {
            var go = new GameObject(n.Name);
            go.transform.SetParent(parent, false);
            go.layer = n.Layer;
            go.transform.localPosition = n.Position;
            go.transform.localRotation = n.Rotation;
            go.transform.localScale = n.Scale;

            ApplyPhysics(go, n);

            foreach (BossData.Node c in n.Children) BuildChild(go.transform, c);

            // Applied last so children are parented onto a live object first. Hollow
            // Knight ships the Hero Damager inactive; its FSM switches it on mid-attack.
            if (!n.Active) go.SetActive(false);
        }

        private static string PickIdleClip(tk2dSpriteAnimation library)
        {
            string[] preferred = { "Idle", "Fly", "Sleep", "Walk" };
            foreach (string p in preferred)
            {
                foreach (tk2dSpriteAnimationClip c in library.clips)
                {
                    if (c != null && string.Equals(c.name, p, StringComparison.OrdinalIgnoreCase))
                        return c.name;
                }
            }
            return library.clips.Length > 0 && library.clips[0] != null ? library.clips[0].name : null;
        }

        // ------------------------------------------------------------------

        private static tk2dSpriteCollectionData BuildCollection(BossData.Boss boss)
        {
            if (CollectionCache.TryGetValue(boss.Name, out tk2dSpriteCollectionData cached) && cached != null)
                return cached;

            Texture2D tex = boss.Textures.Length > 0
                ? Rebuild.GodhomeData.LoadPage(boss.Textures[0])
                : null;
            if (tex == null)
            {
                Plugin.Log.LogError($"Godhome: boss '{boss.Name}' has no usable atlas texture.");
                return null;
            }

            // tk2d's own shader, which Silksong ships - it multiplies the atlas by the
            // per-vertex colour tk2dSprite writes.
            Shader shader = Shader.Find("tk2d/BlendVertexColor") ?? Shader.Find("Sprites/Default");
            var material = new Material(shader) { name = boss.CollectionName + "_mat", mainTexture = tex };

            // tk2d keeps these on GameObjects, not as ScriptableObjects - in Hollow
            // Knight they live on inactive prefabs. A hidden, DontDestroyOnLoad host
            // gives them the same lifetime without appearing in the scene.
            var host = new GameObject("Godhome_Collection_" + boss.CollectionName);
            host.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(host);

            var coll = host.AddComponent<tk2dSpriteCollectionData>();
            coll.name = boss.CollectionName;
            coll.spriteCollectionName = boss.CollectionName;
            coll.materials = new[] { material };
            coll.textures = new Texture[] { tex };
            coll.premultipliedAlpha = false;

            // Keep tk2d out of its platform-variant and material-instancing paths: `inst`
            // then returns this object, and Init() uses our material directly.
            coll.hasPlatformData = false;
            coll.needMaterialInstance = false;
            coll.materialIdsValid = true;
            coll.spriteCollectionPlatforms = new string[0];
            coll.spriteCollectionPlatformGUIDs = new string[0];
            coll.pngTextures = new TextAsset[0];
            coll.materialPngTextureId = new int[0];

            var defs = new tk2dSpriteDefinition[boss.Defs.Length];
            for (int i = 0; i < defs.Length; i++)
            {
                BossData.SpriteDef s = boss.Defs[i];
                var d = new tk2dSpriteDefinition
                {
                    name = s.Name ?? ("sprite" + i),
                    material = material,
                    materialId = 0,
                    texelSize = s.TexelSize,
                    positions = s.Positions,
                    uvs = s.Uvs,
                    indices = s.Indices,
                    boundsData = s.BoundsData,
                    untrimmedBoundsData = s.UntrimmedBoundsData,
                    normals = new Vector3[0],
                    tangents = new Vector4[0],
                    normalizedUvs = new Vector2[0],
                    customColliders = new tk2dSpriteColliderDefinition[0],
                    polygonCollider2D = new tk2dCollider2DData[0],
                    edgeCollider2D = new tk2dCollider2DData[0],
                    attachPoints = new tk2dSpriteDefinition.AttachPoint[0],
                    colliderType = tk2dSpriteDefinition.ColliderType.None,
                };
                defs[i] = d;
            }
            coll.spriteDefinitions = defs;

            CollectionCache[boss.Name] = coll;
            Plugin.Log.LogInfo($"Godhome: built collection '{coll.spriteCollectionName}' ({defs.Length} sprites).");
            return coll;
        }

        private static tk2dSpriteAnimation BuildLibrary(BossData.Boss boss, tk2dSpriteCollectionData coll)
        {
            if (LibraryCache.TryGetValue(boss.Name, out tk2dSpriteAnimation cached) && cached != null)
                return cached;
            if (boss.Clips == null || boss.Clips.Length == 0) return null;

            var host = new GameObject("Godhome_Anim_" + boss.Name);
            host.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(host);

            var lib = host.AddComponent<tk2dSpriteAnimation>();
            lib.name = boss.Name + "_anim";

            var clips = new tk2dSpriteAnimationClip[boss.Clips.Length];
            for (int i = 0; i < clips.Length; i++)
            {
                BossData.Clip c = boss.Clips[i];
                var frames = new tk2dSpriteAnimationFrame[c.Frames.Length];
                for (int k = 0; k < frames.Length; k++)
                {
                    BossData.Frame f = c.Frames[k];
                    frames[k] = new tk2dSpriteAnimationFrame
                    {
                        spriteCollection = coll,
                        spriteId = f.SpriteId,
                        triggerEvent = f.TriggerEvent,
                        eventInfo = f.EventInfo ?? "",
                        eventInt = f.EventInt,
                        eventFloat = f.EventFloat,
                    };
                }
                clips[i] = new tk2dSpriteAnimationClip
                {
                    name = c.Name,
                    fps = c.Fps,
                    loopStart = c.LoopStart,
                    wrapMode = (tk2dSpriteAnimationClip.WrapMode)c.WrapMode,
                    frames = frames,
                };
            }
            lib.clips = clips;

            LibraryCache[boss.Name] = lib;
            Plugin.Log.LogInfo($"Godhome: built animation library ({clips.Length} clips).");
            return lib;
        }
    }
}
