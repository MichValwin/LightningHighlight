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

        public override void StartClientSide(ICoreClientAPI api) {
            this.api = api;
            config = new ModConfig(api, Mod);

            api.Input.RegisterHotKey(config.HotkeyCode, config.HotkeyDescriptionString, GlKeys.O, type: HotkeyType.HelpAndOverlays, ctrlPressed: true);
            api.Input.SetHotKeyHandler(config.HotkeyCode, _ => ToggleVisualization());
        }

        long _listenerId = 0;
        //Simple toggle system which adds/remove the highlight calculating function which won't be the drawer if an IRender is involved...
        private bool ToggleVisualization() {
            if (_listenerId == 0) {
                _listenerId = api.Event.RegisterGameTickListener(DrawHighlights, 500);
            } else {
                api.Event.UnregisterGameTickListener(_listenerId);
                api.World.HighlightBlocks(api.World.Player, config.HighlighSlot, []);

                _listenerId = 0;
            }
            return true;
        }

        private List<LightningAttractor> GetAttractorsByRadius(BlockPos center, int r) {
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

        private List<BlockPos> _positions;
        private List<int> _colors;
        private int _lastCapacity;

        private void EnsurePoolSize(int capacity) {
            if (_positions == null || capacity != _lastCapacity) {
                _positions = new List<BlockPos>(capacity);
                _colors = new List<int>(capacity);
                for (int i = 0; i < capacity; i++) {
                    _positions.Add(new BlockPos(0, 0, 0));
                    _colors.Add(config.parsedLightningHitColor);
                }

                _lastCapacity = capacity;
            }
        }

        private void DrawHighlights(float _) {
            Stopwatch sw = new Stopwatch();
            sw.Start();

            BlockPos pp = api.World.Player.Entity.Pos.AsBlockPos;
            int r = config.ChunkRadius;
            List<LightningAttractor> attractors = GetAttractorsByRadius(pp, r);

            var chunkSize = GlobalConstants.ChunkSize;
            FastVec2i chunk2D = new(pp.X / chunkSize, pp.Z / chunkSize);
            FastVec2i start = chunk2D - r;
            FastVec2i end = chunk2D + r;
            Vec3i mapSize = api.World.BlockAccessor.MapSize;

            int capacity = (end.X - start.X + 1) * chunkSize * (end.Y - start.Y + 1) * chunkSize;
            // List<BlockPos> positions = new(capacity);
            // List<int> colors = new(capacity);
            EnsurePoolSize(capacity);

            int idx = 0;

            // Loop through the rainMap of each chunk column in the radius
            for (var gx = start.X; gx <= end.X; gx++) {
                for (var gz = start.Y; gz <= end.Y; gz++) {
                    for (int cx = 0; cx < chunkSize; cx++) {
                        for (int cz = 0; cz < chunkSize; cz++) {
                            BlockPos pos = _positions[idx];
                            pos.X = gx * chunkSize + cx;
                            pos.Z = gz * chunkSize + cz;
                            pos.Y = api.World.BlockAccessor.GetRainMapHeightAt(pos);

                            if (pos.Y < 0 || pos.Y >= mapSize.Y) continue;

                            bool isProtected = attractors.Any(a => IsLightningAttracted(pos, a));
                            _colors[idx] = isProtected ? config.parsedSafeColor : config.parsedLightningHitColor;
                            idx++;

                            // BlockPos pos = new(gx * chunkSize + cx, 0, gz * chunkSize + cz);
                            // pos.Y = api.World.BlockAccessor.GetRainMapHeightAt(pos);

                            // if (pos.Y < 0 || pos.Y >= mapSize.Y) continue; // Invalid pos

                            // bool isProtected = attractors.Any(a => IsLightningAttracted(pos, a));

                            // positions.Add(pos);
                            // colors.Add(isProtected ? config.parsedSafeColor : config.parsedLightningHitColor);
                        }
                    }
                }
            }
            sw.Stop();
            api.Logger.Debug($"To calculate lights and populate list, taken ${sw.ElapsedMilliseconds}");

            sw.Start();
            api.World.HighlightBlocks(api.World.Player, config.HighlighSlot, _positions, _colors);
            sw.Stop();
            api.Logger.Debug($"Taken {sw.ElapsedMilliseconds}ms to do the highlight");

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