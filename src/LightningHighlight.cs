using System;
using System.Collections.Generic;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;


namespace LightningHighlight {
    public class LightningHighlightModSystem : ModSystem {
        private ICoreClientAPI api;
        private ModConfig config;
        private Thread thread;
        private IBlockAccessor blockAccessor;
        private bool enable = false;


        public override void StartClientSide(ICoreClientAPI api) {
            this.api = api;
            config = new ModConfig(api, Mod);
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

            blockAccessor = api.World.GetLockFreeBlockAccessor();

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
                Thread.Sleep(250);
            }
            clearHighlights();
        }

        private List<BlockPos> getAllBlockAttractLightning(BlockPos center, int r) {
            List<BlockPos> attractors = new List<BlockPos>();

            BlockPos minSearch = new(center.X - r, center.Y - r, center.Z - r);
            BlockPos maxSearch = new(center.X + r, center.Y + r, center.Z + r);
            blockAccessor.WalkBlocks(
                minSearch,
                maxSearch,
                (block, x, y, z) => {
                    if (block.Id == 0) return;

                    // Get all blocks with BEBehaviorAttractsLightning
                    BlockEntity be = blockAccessor.GetBlockEntity(new BlockPos(x, y, z));
                    if (be == null) return;
                    var behavior = be.GetBehavior<BEBehaviorAttractsLightning>();
                    if (behavior == null) return;

                    attractors.Add(new BlockPos(x, y, z));
                });

            return attractors;
        }

        private void drawHighlights() {
            BlockPos pp = api.World.Player.Entity.Pos.AsBlockPos;
            var r = config.Radius;
            List<BlockPos> attractors = getAllBlockAttractLightning(pp, r);

            List<BlockPos> positions = new();
            List<int> colors = new();

            BlockPos minSearch = new(pp.X - r, pp.Y - r, pp.Z - r);
            BlockPos maxSearch = new(pp.X + r, pp.Y + r, pp.Z + r);
            blockAccessor.WalkBlocks(
                minSearch,
                maxSearch,
                (block, x, y, z) => {
                    if (block.Id == 0) return;
                    //if (!block.SideSolid[BlockFacing.UP.Index]) return;

                    //  Check if it has line of sight to the sky
                    var rainHeight = api.World.BlockAccessor.GetRainMapHeightAt(x, z);
                    if (rainHeight != y) return;

                    var blockPos = new BlockPos(x, y, z);
                    // Check if lightning should affect to  block
                    bool canHit = true;
                    foreach (var attractor in attractors) {
                        canHit = canLightningImpact(blockPos, attractor);
                        if (!canHit) break;
                    }

                    positions.Add(blockPos);
                    colors.Add(canHit ? config.parsedLightningHitColor : config.parsedSafeColor);
                });

            showHighlights(positions, colors);
        }

        private void showHighlights(List<BlockPos> positions, List<int> colors) {
            api.Event.EnqueueMainThreadTask(() => api.World.HighlightBlocks(api.World.Player, config.HighlighSlot, positions, colors), config.TaskCode);
        }
        private void clearHighlights() {
            api.Event.EnqueueMainThreadTask(() => api.World.HighlightBlocks(api.World.Player, config.HighlighSlot, new List<BlockPos>()), config.TaskCode);
        }

        // Code from https://github.com/anegostudios/vssurvivalmod/blob/ac9a0059d84ca3449f066f26b5ee6b47bc9ce76a/BlockEntityBehavior/BEBehaviorAttractsLightning.cs#L62
        private bool canLightningImpact(BlockPos impactPos, BlockPos attractor) {
            var world = api.World;
            var ourPos = attractor;

            // Get BEBehaviorAttractsLightning attributes
            BlockEntity be = blockAccessor.GetBlockEntity(attractor);
            BEBehaviorAttractsLightning behavior = be.GetBehavior<BEBehaviorAttractsLightning>();
            float artificialElevation = 1f;
            float elevationMultiplier = 1f;
            if (behavior != null) {
                // Access the block's type properties
                artificialElevation = be.Block.Attributes["artificialElevation"].AsFloat(1f);
                elevationMultiplier = be.Block.Attributes["elevationAttractivenessMultiplier"].AsFloat(1f);
            }

            var ourRainHeight = world.BlockAccessor.GetRainMapHeightAt(ourPos.X, ourPos.Z);

            // Something may be above us blocking line of sight to the sky
            if (ourRainHeight != ourPos.Y) return true;

            var impactRainHeight = world.BlockAccessor.GetRainMapHeightAt((int)impactPos.X, (int)impactPos.Z);

            float yDiff = artificialElevation + ourRainHeight - impactRainHeight;

            // We want the modifier to always be beneficial (if greater than 1)
            if (yDiff < 0) {
                yDiff /= elevationMultiplier;
            }
            else {
                yDiff *= elevationMultiplier;
            }

            yDiff = GameMath.Min(40, yDiff);

            var posA = new Vec2d(ourPos.X, ourPos.Z);
            if (posA.DistanceTo(impactPos.X, impactPos.Z) > yDiff) return true;

            return false;
        }
    }


}