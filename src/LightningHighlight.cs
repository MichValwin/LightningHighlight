using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

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

            api.ChatCommands.GetOrCreate("threat").HandleWith(OnThreaded);
            api.ChatCommands.GetOrCreate("slim").HandleWith(OnSlim);
            api.ChatCommands.GetOrCreate("simple").HandleWith(OnSimple);
            api.ChatCommands.GetOrCreate("render").HandleWith(OnRender);
        }

        TextCommandResult OnThreaded(TextCommandCallingArgs args) {
            _hightlightAction = DrawHighlightsThreaded;
            return TextCommandResult.Success("Changed to threaded");
        }

        TextCommandResult OnSlim(TextCommandCallingArgs args) {
            _hightlightAction = DrawHighlightsNew;
            return TextCommandResult.Success("Changed to slim same arrr");
        }

        TextCommandResult OnSimple(TextCommandCallingArgs args) {
            _hightlightAction = DrawHighlightsSimple;
            return TextCommandResult.Success("Changed to simple");
        }

        TextCommandResult OnRender(TextCommandCallingArgs args) {
            _hightlightAction = DrawHighlightsNewRenderer;
            return TextCommandResult.Success("Changed to render");
        }


        //Simple toggle system which adds/remove the highlight calculating function which won't be the drawer if an IRender is involved...
        private bool ToggleVisualization() {
            if (_listenerId == 0) {
                _listenerId = api.Event.RegisterGameTickListener(_hightlightAction, 500);
            } else {
                api.Event.UnregisterGameTickListener(_listenerId);
                api.World.HighlightBlocks(api.World.Player, config.HighlighSlot, []);

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

        private void DrawHighlightsNew(float _) {
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
            //api.World.HighlightBlocks(api.World.Player, config.HighlighSlot, _positions, _colors);
            //TODO: Add hightlghter to renderer

            sw.Stop();
            api.Logger.Debug($"Taken {sw.ElapsedMilliseconds}ms to do the highlight");

        }

        private void DrawHighlightsSimple(float _) {
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

            int capacity = (end.X - start.X) * (end.Y - start.Y);
            List<BlockPos> positions = new(capacity);
            List<int> colors = new(capacity);

            //rather than iterating through chunks we will iterate through the whole area so it's straightforward to parallelize efficiently
            for (var z = start.Y; z < end.Y; z++) {
                for (var x = start.X; x < end.X; x++) {
                    var pos = new BlockPos(x, 0, z);

                    pos.Y = api.World.BlockAccessor.GetRainMapHeightAt(pos);

                    if (pos.Y < 0 || pos.Y >= mapSize.Y) {
                        continue; // Invalid pos
                    }
                    bool isProtected = attractors.Any(a => IsLightningAttracted(pos, a));

                    positions.Add(pos);
                    int color = isProtected ? config.parsedSafeColor : config.parsedLightningHitColor;
                    colors.Add(color);
                }
            }

            sw.Stop();
            api.Logger.Debug($"To calculate lights and populate list, taken ${sw.ElapsedMilliseconds}");

            sw.Start();
            api.World.HighlightBlocks(api.World.Player, config.HighlighSlot, positions, colors);
            sw.Stop();
            api.Logger.Debug($"Taken {sw.ElapsedMilliseconds}ms to do the highlight");
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
            MeshData mesh = new(capacity * 4, capacity * 1, false, false, true, false);
            float[] shadings = CubeMeshUtil.DefaultBlockSideShadingsByFacing;

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


            api.Event.EnqueueMainThreadTask(() => { if (_renderer.Context?.MeshRef != null) _renderer.Context.MeshRef.Dispose(); }, "lmr");

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

                        var center = new Vec3f {
                            X = pos.X - origin.X + 0.5f,
                            Y = pos.InternalY - origin.Y + 0.5f,
                            Z = pos.Z - origin.Z + 0.5f
                        };
                        ModelCubeUtilExt.AddFaceSkipTex(mesh, BlockFacing.UP, center, Vec3f.One, color, shadings[BlockFacing.UP.Index]);
                    } else {
                        bool isProtected = attractors.Any(a => IsLightningAttracted(pos, a));
                        int color = isProtected ? config.parsedSafeColor : config.parsedLightningHitColor;
                        var center = new Vec3f {
                            X = pos.X - origin.X + 0.5f,
                            Y = pos.InternalY - origin.Y + 0.5f,
                            Z = pos.Z - origin.Z + 0.5f
                        };
                        ModelCubeUtilExt.AddFaceSkipTex(mesh, BlockFacing.UP, center, Vec3f.One, color, shadings[BlockFacing.UP.Index]);
                    }
                    // Create mesh renderer
                    // foreach (var face in BlockFacing.ALLFACES) {

                    //     var center = new Vec3f {
                    //         X = pos.X - origin.X + 0.5f,
                    //         Y = pos.InternalY - origin.Y + 0.5f,
                    //         Z = pos.Z - origin.Z + 0.5f
                    //     };
                    //     ModelCubeUtilExt.AddFaceSkipTex(mesh, face, center, Vec3f.One, color, shadings[face.Index]);
                    // }

                    // Only create a mesh for the UP face of the block

                }
            }

            // Upload mesh and store ref
            api.Event.EnqueueMainThreadTask(() => _renderer.Context = new(origin, api.Render.UploadMesh(mesh)), "lmr");

            sw.Stop();
            api.Logger.Debug($"To calculate lights and populate list, taken ${sw.ElapsedMilliseconds}");
        }

        private void DrawHighlightsThreaded(float _) {
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

            int capacity = (end.X - start.X) * (end.Y - start.Y);
            List<BlockPos> positions = new(capacity);
            List<int> colors = new(capacity);

            //rather than iterating through chunks we will iterate through the whole area so it's straightforward to parallelize efficiently
            //I prefer to iterate line by line, hence Z : personal preference :-3
            for (var z = start.Y; z < end.Y; z++) {
                Parallel.For(start.X, end.X,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount - 1 },
                    (x, loopState) => {
                        var pos = new BlockPos(x, 0, z);

                        pos.Y = api.World.BlockAccessor.GetRainMapHeightAt(pos);

                        if (pos.Y < 0 || pos.Y >= mapSize.Y) return; // Invalid pos
                        bool isProtected = attractors.Any(a => IsLightningAttracted(pos, a));

                        positions.Add(pos);
                        colors.Add(isProtected ? config.parsedSafeColor : config.parsedLightningHitColor);
                    });
            }




            sw.Stop();
            api.Logger.Debug($"To calculate lights and populate list, taken ${sw.ElapsedMilliseconds}");

            sw.Start();
            api.World.HighlightBlocks(api.World.Player, config.HighlighSlot, positions, colors);
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