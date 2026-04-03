using System;
using System.Collections.Generic;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Config;
using System.Linq;

[assembly: ModInfo(
    name: "LightningHighlight",
    modID: "lightninghighlight",
    Version = "1.1.3",
    Description = "Highlight lightning protection",
    Website = "",
    Authors = new[] { "MichValwin" }
    )
]

namespace LightningHighlight {
    public struct LightningAttractor {
        public BlockPos pos;
        public float artificialElevation;
        public float elevationAttractivenessMultiplier;
        public int rainHeight;
    }

    public class LightningHighlightModSystem : ModSystem {
        private ICoreClientAPI api;
        private ModConfig config;
        private Thread thread;
        private bool enable = false;

        private Dictionary<int, (float artificialElevation, float elevationAttractivenessMultiplier)> attractorBlocks = [];


        public override void StartClientSide(ICoreClientAPI api) {
            this.api = api;
            config = new ModConfig(api, Mod);
            attractorBlocks = getLightningAttractors();
            RegisterHotkey();
        }

        private void RegisterHotkey() {
            api.Input.RegisterHotKey(config.HotkeyCode, config.HotkeyDescriptionString, GlKeys.O, type: HotkeyType.HelpAndOverlays, ctrlPressed: true);
            api.Input.SetHotKeyHandler(config.HotkeyCode, toggleHotkey);
        }

        private bool toggleHotkey(KeyCombination _) {
            toggleVisualization();
            return true;
        }

        private void toggleVisualization() {
            // Prevents starting a new thread before old one has ended
            if (!enable && thread?.IsAlive == true) return;
            enable = !enable;

            thread = new Thread(RunThread) {
                IsBackground = true,
                Name = config.ThreadName
            };
            thread.Start();
        }

        private void RunThread() {
            while (enable) {
                try {
                    drawHighlights();
                } catch (Exception ex) {
                    api.Logger.Error(ex);
                }
                Thread.Sleep(500);
            }
            clearHighlights();
        }

        private Dictionary<int, (float artificialElevation, float elevationAttractivenessMultiplier)> getLightningAttractors() {
            Dictionary<int, (float artificialElevation, float elevationAttractivenessMultiplier)> attractors = new();
            foreach (var block in api.World.Blocks) {
                if (block?.BlockEntityBehaviors == null) continue;

                var bht = block.BlockEntityBehaviors.FirstOrDefault(b => b.Name == "AttractsLightning");
                if (bht == null) continue;

                float artificialElevation = bht.properties?["ArtificialElevation"].AsFloat(1.0f) ?? 1.0f;
                float elevationAttractivenessMultiplier = bht.properties?["ElevationAttractivenessMultiplier"].AsFloat(1.0f) ?? 1.0f;

                attractors[block.Id] = (artificialElevation, elevationAttractivenessMultiplier);
            }
            return attractors;
        }

        private List<LightningAttractor> getAllBlockAttractLightning(BlockPos center, int r) {
            var chunkSize = GlobalConstants.ChunkSize;
            FastVec2i chunk2D = new(center.X / chunkSize, center.Z / chunkSize);
            FastVec2i start = new(chunk2D.X - r, chunk2D.Y - r);
            FastVec2i end = new(chunk2D.X + r, chunk2D.Y + r);
            Vec3i mapSize = api.World.BlockAccessor.MapSize;

            List<LightningAttractor> attractors = new List<LightningAttractor>();

            for (var cx = start.X; cx <= end.X; cx++) {
                for (var cz = start.Y; cz <= end.Y; cz++) {
                    for (var cy = mapSize.Y / chunkSize - 1; cy >= 0; cy--) {
                        IWorldChunk chunk = api.World.ChunkProvider.GetChunk(cx, cy, cz);
                        if (chunk.Empty) {
                            continue;
                        }

                        chunk.Unpack();
                        if (!attractorBlocks.Keys.Any(id => chunk.Data.ContainsBlock(id))) continue;

                        foreach (var (pos, entity) in chunk.BlockEntities) {
                            if (!attractorBlocks.TryGetValue(entity.Block.Id, out var attractorConfig)) continue;

                            if (attractorBlocks.ContainsKey(entity.Block.Id)) {
                                attractors.Add(new LightningAttractor {
                                    pos = pos,
                                    artificialElevation = attractorConfig.artificialElevation,
                                    elevationAttractivenessMultiplier = attractorConfig.elevationAttractivenessMultiplier,
                                    rainHeight = api.World.BlockAccessor.GetRainMapHeightAt(pos.X, pos.Z)
                                });
                            }
                        }
                    }
                }
            }

            return attractors;
        }

        private void drawHighlights() {
            BlockPos pp = api.World.Player.Entity.Pos.AsBlockPos;
            int r = config.ChunkRadius;
            List<LightningAttractor> attractors = getAllBlockAttractLightning(pp, r);

            List<BlockPos> positions = [];
            List<int> colors = [];

            var chunkSize = GlobalConstants.ChunkSize;
            FastVec2i chunk2D = new(pp.X / chunkSize, pp.Z / chunkSize);
            FastVec2i start = new(chunk2D.X - r, chunk2D.Y - r);
            FastVec2i end = new(chunk2D.X + r, chunk2D.Y + r);
            Vec3i mapSize = api.World.BlockAccessor.MapSize;

            // Loop through chunk columns
            for (var gx = start.X; gx <= end.X; gx++) {
                for (var gz = start.Y; gz <= end.Y; gz++) {
                    for (int cx = 0; cx < chunkSize; cx++) {
                        for (int cz = 0; cz < chunkSize; cz++) {
                            int worldX = gx * chunkSize + cx;
                            int worldZ = gz * chunkSize + cz;

                            int rainHeight = api.World.BlockAccessor.GetRainMapHeightAt(worldX, worldZ);
                            if (rainHeight < 0 || rainHeight >= mapSize.Y) continue; // Invalid pos

                            var pos = new BlockPos(worldX, rainHeight, worldZ);

                            bool canHitBlock = true;
                            foreach (var attractor in attractors) {
                                canHitBlock = !isLightningAttracted(pos, attractor, attractor.rainHeight, rainHeight);
                                if (!canHitBlock) break;
                            }

                            positions.Add(pos);
                            colors.Add(canHitBlock ? config.parsedLightningHitColor : config.parsedSafeColor);
                        }
                    }
                }
            }

            showHighlights(positions, colors);
        }

        private void showHighlights(List<BlockPos> positions, List<int> colors) {
            api.Event.EnqueueMainThreadTask(() => api.World.HighlightBlocks(api.World.Player, config.HighlighSlot, positions, colors), config.TaskCode);
        }
        private void clearHighlights() {
            api.Event.EnqueueMainThreadTask(() => api.World.HighlightBlocks(api.World.Player, config.HighlighSlot, new List<BlockPos>()), config.TaskCode);
        }

        // Code from https://github.com/anegostudios/vssurvivalmod/blob/ac9a0059d84ca3449f066f26b5ee6b47bc9ce76a/BlockEntityBehavior/BEBehaviorAttractsLightning.cs#L62
        private bool isLightningAttracted(BlockPos impactPos, LightningAttractor attractor, int ourRainHeight, int impactRainHeight) {
            var world = api.World;

            // Code from vssurvivalmod
            // Get BEBehaviorAttractsLightning config attributes
            //int ourRainHeight = world.BlockAccessor.GetRainMapHeightAt(attractor.pos.X, attractor.pos.Z);

            // Something may be above us blocking line of sight to the sky
            if (ourRainHeight != attractor.pos.Y) return false;

            //int impactRainHeight = world.BlockAccessor.GetRainMapHeightAt((int)impactPos.X, (int)impactPos.Z);

            float yDiff = attractor.artificialElevation + ourRainHeight - impactRainHeight;

            // We want the modifier to always be beneficial (if greater than 1)
            if (yDiff < 0) {
                yDiff /= attractor.elevationAttractivenessMultiplier;
            } else {
                yDiff *= attractor.elevationAttractivenessMultiplier;
            }

            yDiff = GameMath.Min(40, yDiff); // Cap to 40

            // Offset the distance by 1 only when the diff between the attractor and the impact pos is positive
            double impactX = impactPos.X + (attractor.pos.X < impactPos.X ? 1.0 : 0.0);
            double impactZ = impactPos.Z + (attractor.pos.Z < impactPos.Z ? 1.0 : 0.0);

            var posAttractor = new Vec2d(attractor.pos.X, attractor.pos.Z);
            double distance = posAttractor.DistanceTo(impactX, impactZ);
            if (distance - yDiff > 0.0f) return false;

            // // FROM the wiki:
            // int ourRainHeight = world.BlockAccessor.GetRainMapHeightAt(attractor.X, attractor.Z);
            // if (ourRainHeight != attractor.Y) return false;
            // // sqrt((rod.x - target.x)^2 + (rod.z - target.z)^2) <= min(40, (5 + rod.y - target.y) * 2) 
            // var posAttractor = new Vec2d(attractor.X, attractor.Z);
            // var distance = Math.Ceiling(posAttractor.DistanceTo(impactPos.X, impactPos.Z)); // Ceil the number so we can display complete block coverage
            // var yDiff = GameMath.Min(40, (5 + attractor.Y - impactPos.Y) * 2);
            // if (distance > yDiff) return false;

            return true;
        }
    }


}