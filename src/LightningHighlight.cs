using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

[assembly: ModInfo(
    name: "LightningHighlight",
    modID: "lightninghighlight",
    Version = "1.1.4",
    Description = "Highlight lightning protection",
    Website = "",
    Authors = new[] { "MichValwin", "Psyloh" }
    )
]

namespace LightningHighlight {
    struct LightningAttractor {
        public int Id;
        public BlockPos Pos;
        public BehaviorProperties Properties;
    }

    //Property class for each type of attractor, won't be much instanciated as there's only one type of attractor as of yet
    class BehaviorProperties {
        public float ArtificialElevation { get; set; }
        public float ElevationAttractivenessMultiplier { get; set; }
    }


    public class LightningHighlightModSystem : ModSystem {
        private ICoreClientAPI api;
        private ModConfig config;

        Dictionary<int, BehaviorProperties>? _attractorBlocks;

        private long _listenerId = 0;
        private Action<float> _hightlightAction;

        HightlightRenderer _renderer;

        public override void StartClientSide(ICoreClientAPI api) {
            this.api = api;
            config = new ModConfig(api, Mod);

            _hightlightAction = DrawHighlightsNewRenderer;
            _renderer = new(api);

            api.Event.RegisterRenderer(_renderer, EnumRenderStage.OIT);

            api.Input.RegisterHotKey(config.HotkeyCode, config.HotkeyDescriptionString, GlKeys.O, type: HotkeyType.HelpAndOverlays, ctrlPressed: true);
            api.Input.SetHotKeyHandler(config.HotkeyCode, _ => ToggleVisualization());
        }


        //Simple toggle system which adds/remove the highlight calculating function which won't be the drawer if an IRender is involved...
        private bool ToggleVisualization() {
            if (_listenerId == 0) {
                _listenerId = api.Event.RegisterGameTickListener(_hightlightAction, 500);
            } else {
                api.Event.UnregisterGameTickListener(_listenerId);
                _renderer.Dispose(); // For the renderer
                _listenerId = 0;
            }
            return true;
        }

        private Dictionary<int, BehaviorProperties> AttractorBlocks {
            //Cause that function is supposed to happen only once, let's just make sure it does!
            get {
                if (_attractorBlocks == null) {
                    _attractorBlocks = [];
                    foreach (var block in api.World.Blocks) {
                        //I might be wrong but I'm pretty sure all blocks have that array instanciated
                        if (block.BlockEntityBehaviors.Length < 1) continue;

                        var bht = block.BlockEntityBehaviors.FirstOrDefault(b => b.Name == "AttractsLightning");
                        if (bht == null) continue;

                        //If  that line throws a NullException there's a biger issue underlying
                        //thus we are ok to crash the game!
                        _attractorBlocks[block.Id] = bht.properties?.AsObject<BehaviorProperties>()!;
                    }
                }
                return _attractorBlocks;
            }
        }

        private List<LightningAttractor> GetAttractorsByRadius(BlockPos center, int r) {
            r += 2; // Get 2x chunks more to get all the attractors that could possible hit?
            var chunkSize = GlobalConstants.ChunkSize;
            FastVec2i chunk2D = new(center.X / chunkSize, center.Z / chunkSize);
            FastVec2i start = new(chunk2D.X - r, chunk2D.Y - r);
            FastVec2i end = new(chunk2D.X + r, chunk2D.Y + r);
            Vec3i mapSize = api.World.BlockAccessor.MapSize;

            List<LightningAttractor> attractors = [];

            for (var cx = start.X; cx <= end.X; cx++) {
                for (var cz = start.Y; cz <= end.Y; cz++) {
                    for (var cy = mapSize.Y / chunkSize - 1; cy >= 0; cy--) {
                        IWorldChunk chunk = api.World.ChunkProvider.GetChunk(cx, cy, cz);
                        if (chunk.Empty) {
                            continue;
                        }
                        chunk.Unpack();
                        if (!AttractorBlocks.Keys.Any(chunk.Data.ContainsBlock)) continue;


                        foreach (var (pos, entity) in chunk.BlockEntities) {
                            if (!AttractorBlocks.TryGetValue(entity.Block.Id, out var properties)) {
                                continue;
                            }

                            int rainHeight = api.World.BlockAccessor.GetRainMapHeightAt(pos.X, pos.Z);
                            if (pos.Y != rainHeight) continue; // Attractor is blocked by something

                            attractors.Add(new() {
                                Id = entity.Block.Id,
                                Pos = pos,
                                Properties = properties
                            });
                        }
                    }
                }
            }
            return attractors;
        }

        private void DrawHighlightsNewRenderer(float _) {
            Stopwatch sw = new Stopwatch();
            sw.Start();
            BlockPos pp = api.World.Player.Entity.Pos.AsBlockPos;
            int r = config.ChunkRadius;
            List<LightningAttractor> attractors = GetAttractorsByRadius(pp, r);

            var chunkSize = GlobalConstants.ChunkSize;
            FastVec2i playerChunk = new(pp.X / chunkSize, pp.Z / chunkSize);
            //The first position in the first chunk (north-west)
            FastVec2i start = chunkSize * (playerChunk - r);
            //The position after the last chunk (south-east)
            FastVec2i end = chunkSize * (playerChunk + r + 1);
            Vec3i mapSize = api.World.BlockAccessor.MapSize;

            int width = end.X - start.X;
            int height = end.Y - start.Y;
            int capacity = width * height;


            var origin = pp.Copy();
            //MeshData mesh = new(capacity * 4 * 6, capacity * 6 * 6, false, false, true, false); ALL faces
            MeshData mesh = new(capacity * 4, capacity * 1, false, false, true, false); // Mesh for only UP face


            // Build discriminator array  so we dont cehck on positions that cannot be covered
            bool[] collideArr = new bool[capacity];
            int sizeAttractorMax = 43; // Magic number (bigger than max raidus protects)
            int attrR = sizeAttractorMax;
            foreach (var attr in attractors) {
                for (int z = attr.Pos.Z - attrR; z < attr.Pos.Z + attrR; z++) {
                    for (int x = attr.Pos.X - attrR; x < attr.Pos.X + attrR; x++) {
                        int xx = end.X - x;
                        int zz = end.Y - z;
                        if (xx < 0 || zz < 0 || xx >= width || zz >= height) {
                            continue;
                        }
                        collideArr[xx + zz * width] = true;
                    }
                }
            }

            api.Event.EnqueueMainThreadTask(() => { _renderer.Dispose(); }, "lmr"); // Dispose of old mesh

            //rather than iterating through chunks we will iterate through the whole area so it's straightforward to parallelize efficiently
            for (var z = start.Y; z < end.Y; z++) {
                for (var x = start.X; x < end.X; x++) {
                    var pos = new BlockPos(x, 0, z);
                    pos.Y = api.World.BlockAccessor.GetRainMapHeightAt(pos);
                    if (pos.Y < 0 || pos.Y >= mapSize.Y) {
                        continue; // Invalid pos TODO: check
                    }

                    int xx = end.X - x - 1;
                    int zz = end.Y - z - 1;
                    if (!collideArr[xx + zz * width]) {
                        // Cant be protected
                        int color = config.parsedLightningHitColor;
                        HightlightRenderer.addUPFaceToMesh(mesh, pos, origin, color);
                    } else {
                        bool isProtected = attractors.Any(a => IsLightningAttracted(pos, a));
                        int color = isProtected ? config.parsedSafeColor : config.parsedLightningHitColor;
                        HightlightRenderer.addUPFaceToMesh(mesh, pos, origin, color);
                    }
                }
            }

            // Upload new mesh and store ref
            api.Event.EnqueueMainThreadTask(() => _renderer.Context = new(origin, api.Render.UploadMesh(mesh)), "lmr");

            sw.Stop();
            api.Logger.Debug($"To calculate lights and populate list, taken ${sw.ElapsedMilliseconds}");
        }

        private static bool IsLightningAttracted(BlockPos testPos, LightningAttractor attractor) {
            var rodPos = attractor.Pos;
            var properties = attractor.Properties;
            float yDiff = properties.ArtificialElevation + rodPos.Y - testPos.Y;

            if (yDiff <= 0) {
                return false;
            }

            var radius = yDiff * properties.ElevationAttractivenessMultiplier;
            radius = GameMath.Min(40, radius);

            //TODO: fix this :-/
            double testX = testPos.X + (rodPos.X < testPos.X ? 1 : 0);
            double testZ = testPos.Z + (rodPos.Z < testPos.Z ? 1 : 0);

            var posAttractor = new Vec2d(rodPos.X, rodPos.Z);
            if (posAttractor.DistanceTo(testX, testZ) > radius) {
                return false;
            }
            return true;
        }
    }
}