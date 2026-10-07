using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.Testing;
using Terraria.Utilities;

namespace GameHook
{
    /// <summary>
    /// Episodes against the Eye of Cthulhu. F5 resets/starts, F6 resets and replays last fight,
    /// F7 runs the evaluation: every baseline agent for EvalEpisodes episodes, seeds 0 to EvalEpisodes - 1.
    /// Reset restores the game's own gameplay snapshot (Terraria.Testing.StateSnapshot, the one behind /checkpoint),
    /// taken once per session after building the arena and loadout, so every episode starts from the same state and RNG seeding
    /// </summary>
    internal static class Env
    {
        private const Keys PlayKey = Keys.F5;
        private const Keys ReplayKey = Keys.F6;
        private const Keys EvalKey = Keys.F7;

        private const int EvalEpisodes = 10;

        private const int MaxEpisodeTicks = 60 * 60 * 3;

        // arena in tiles: layers of wooden platforms above world spawn, with the space above the lowest cleared
        private const int ArenaHalfWidth = 100;
        private const int ArenaHeight = 50;
        private const int ArenaFloorAboveSpawn = 20;
        private const int PlatformLayers = 3;
        private const int PlatformSpacing = 8;

        // the boss appears this far above the player, in pixels
        private const int BossSpawnHeight = 400;

        private const int PlayerLife = 200;

        private enum Mode
        {
            None,
            Play,
            Replay,
            Agent
        }

        private static Mode _mode;
        private static StateSnapshot _checkpoint;
        private static int _bossIndex;
        private static int _step;
        private static int _seed;
        private static IAgent _agent;
        private static List<Controls> _actions;    // what this episode did, or is replaying
        private static List<Controls> _lastPlayed; // the last episode you played, for F6
        private static int _lastPlayedSeed;
        private static Controls _applied;          // the action the game used on the last world update
        private static IAgent[] _evalAgents;
        private static bool _evaluating;
        private static int _evalNext;              // index of the next evaluation episode
        private static bool _playWasDown;
        private static bool _replayWasDown;
        private static bool _evalWasDown;

        /// <summary>The arena is centered on world spawn.</summary>
        public static float ArenaMiddleX
        {
            get { return Main.spawnTileX * 16 + 8; }
        }

        public static bool InEpisode
        {
            get { return _mode != Mode.None; }
        }

        /// <summary>
        /// The aim the game actually used this tick, in an episode. Logged instead of the recomputed
        /// cursor because that is the exact value a replay needs.
        /// </summary>
        public static void GetAppliedAim(out int ax, out int ay)
        {
            ax = _applied.Ax;
            ay = _applied.Ay;
        }
        
        public static void AfterTick()
        {
            if (Main.gameMenu)
            {
                _mode = Mode.None;
                _checkpoint = null; // a snapshot belongs to one world
                return;
            }

            if (_mode != Mode.None)
                CheckEnd();

            bool playDown = KeyDown(PlayKey);
            bool replayDown = KeyDown(ReplayKey);
            bool evalDown = KeyDown(EvalKey);
            if (playDown && !_playWasDown)
            {
                _evaluating = false;
                Start(Mode.Play, null, Environment.TickCount & int.MaxValue);
            }
            else if (replayDown && !_replayWasDown && _lastPlayed != null)
            {
                _evaluating = false;
                Start(Mode.Replay, null, _lastPlayedSeed);
            }
            else if (evalDown && !_evalWasDown)
            {
                _evalAgents = new IAgent[] { new IdleAgent(), new RandomAgent(), new KiteAgent() };
                _evaluating = true;
                _evalNext = 0;
            }
            _playWasDown = playDown;
            _replayWasDown = replayDown;
            _evalWasDown = evalDown;

            // the next evaluation episode starts as soon as the last one ends
            if (_mode == Mode.None && _evaluating)
            {
                if (_evalNext < _evalAgents.Length * EvalEpisodes)
                {
                    Start(Mode.Agent, _evalAgents[_evalNext / EvalEpisodes], _evalNext % EvalEpisodes);
                    _evalNext++;
                }
                else
                {
                    _evaluating = false;
                    Main.NewText("Evaluation done");
                }
            }
        }

        /// <summary>
        /// Called at the start of Main.DoUpdateInWorld, after the game read this tick's input and before
        /// players update. For rewriting input, at same spot the game's /replay overwrites input.
        /// </summary>
        public static void BeforeWorldUpdate()
        {
            if (_mode == Mode.None)
                return;

            Controls action;
            if (_mode == Mode.Play)
            {
                action = ReadPlayer();
                _actions.Add(action);
                ApplyAim(action);
            }
            else
            {
                if (_mode == Mode.Replay)
                {
                    if (_step >= _actions.Count)
                        return; // CheckEnd stops the episode on the next tick
                    action = _actions[_step];
                }
                else
                {
                    action = _agent.Act(Main.LocalPlayer, Main.npc[_bossIndex]);
                }
                ApplyHeld(action.Held);
                ApplyAim(action);
            }
            _applied = action;
            _step++;
        }

        private static void Start(Mode mode, IAgent agent, int seed)
        {
            Player player = Main.LocalPlayer;
            if (_checkpoint == null)
            {
                if (player.dead)
                    return; // the snapshot would hold a dead player
                BuildArena(player);
                _checkpoint = StateSnapshot.Gameplay.Capture();
            }
            _checkpoint.Restore();
            player = Main.LocalPlayer; // in case the restore replaced the object

            // not part of the snapshot. The Eye leaves when it is day
            Main.dayTime = false;
            Main.time = 0;
            Reseed(seed);

            // its own named RNG, which the snapshot reset, so the boss spawns identically every time
            using (Main.SwapRandom("GameHookBossSpawn"))
            {
                Vector2 c = player.Center;
                _bossIndex = NPC.NewNPC(new EntitySource_BossSpawn(player), (int)c.X, (int)c.Y - BossSpawnHeight, NPCID.EyeofCthulhu);
            }

            // Old becomes this on the next input read, so the first JustPressed doesn't depend on
            // what was held when the key was pressed
            PlayerInput.Triggers.Current.Reset();

            if (mode == Mode.Play)
                _actions = new List<Controls>();
            else
                _actions = _lastPlayed;
            if (agent != null)
                agent.Begin(seed);
            _agent = agent;
            _mode = mode;
            _seed = seed;
            _step = 0;

            string name = mode == Mode.Play ? "play" : mode == Mode.Replay ? "replay" : agent.Name;
            Hook.WriteEvent("{\"ev\":\"reset\",\"mode\":\"" + name + "\",\"seed\":" + seed + "}");
        }

        /// <summary>
        /// Gives every world RNG the episode's seed. The snapshot restores them all to the same state,
        /// so without this the same actions would always play out the same way.
        /// </summary>
        private static void Reseed(int seed)
        {
            FieldInfo field = typeof(Main).GetField("_rngs", BindingFlags.NonPublic | BindingFlags.Static);
            Dictionary<string, UnifiedRandom> restored = (Dictionary<string, UnifiedRandom>)field.GetValue(null);
            Dictionary<string, UnifiedRandom> rngs = new Dictionary<string, UnifiedRandom>();
            foreach (string key in restored.Keys)
                rngs[key] = new UnifiedRandom(seed);
            field.SetValue(null, rngs); // a new dictionary, so the snapshot's copy is never touched
        }

        private static void CheckEnd()
        {
            NPC boss = Main.npc[_bossIndex];
            string result = null;
            if (Main.LocalPlayer.dead)
                result = "death";
            else if (!boss.active || boss.type != NPCID.EyeofCthulhu)
                result = boss.life <= 0 ? "win" : "despawn"; // a killed NPC keeps life <= 0
            else if (_step >= MaxEpisodeTicks)
                result = "timeout";
            else if (_mode == Mode.Replay && _step >= _actions.Count)
                result = "out-of-actions";

            if (result == null)
            {
                RemoveOtherNpcs();
                return;
            }

            if (_mode == Mode.Play)
            {
                _lastPlayed = _actions;
                _lastPlayedSeed = _seed;
            }
            _mode = Mode.None;
            Hook.WriteEvent("{\"ev\":\"end\",\"result\":\"" + result + "\",\"steps\":" + _step + "}");
        }

        /// <summary>Natural spawns would make episodes differ, so only the boss and its servants stay.</summary>
        private static void RemoveOtherNpcs()
        {
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC npc = Main.npc[i];
                if (npc.active && !npc.townNPC && npc.type != NPCID.EyeofCthulhu && npc.type != NPCID.ServantofCthulhu)
                    npc.active = false;
            }
        }

        private static Controls ReadPlayer()
        {
            Player player = Main.LocalPlayer;
            Vector2 aim = Main.MouseWorld - player.Center;
            Controls action;
            action.Held = Hook.ReadHeldTriggers();
            action.Ax = (int)Math.Round(aim.X);
            action.Ay = (int)Math.Round(aim.Y);
            return action;
        }

        private static void ApplyHeld(ulong held)
        {
            TriggersPack triggers = PlayerInput.Triggers;
            string[] names = Hook.TriggerNames;
            for (int i = 0; i < names.Length; i++)
                triggers.Current.KeyStatus[names[i]] = (held >> i & 1) != 0;
            triggers.Update(); // JustPressed and JustReleased from Old vs the new Current
        }

        /// <summary>
        /// Points the cursor at player center + aim. Used when playing too, so a replay sees the
        /// exact same aim
        /// </summary>
        private static void ApplyAim(Controls action)
        {
            Player player = Main.LocalPlayer;
            Vector2 c = player.Center;
            Main.mouseX = (int)Math.Round(c.X + action.Ax - Main.screenPosition.X);
            Main.mouseY = (int)Math.Round(c.Y + action.Ay - Main.screenPosition.Y);
            // neither is part of the action, so both are kept fixed: no scrolling through the hotbar,
            // and the cursor being over the UI never blocks using the item
            PlayerInput.ScrollWheelDelta = 0;
            player.mouseInterface = false;
        }
        
        private static void BuildArena(Player player)
        {
            int floorY = Main.spawnTileY - ArenaFloorAboveSpawn;
            int left = Main.spawnTileX - ArenaHalfWidth;
            int right = Main.spawnTileX + ArenaHalfWidth;
            for (int x = left; x <= right; x++)
            {
                for (int y = floorY - ArenaHeight; y <= floorY; y++)
                {
                    if (Main.tile[x, y] == null)
                        Main.tile[x, y] = new Tile();
                    Main.tile[x, y].ClearEverything();
                    // ResetToType also makes it active, and frame 0 is the wooden platform style
                    int above = floorY - y;
                    if (above % PlatformSpacing == 0 && above / PlatformSpacing < PlatformLayers)
                        Main.tile[x, y].ResetToType(TileID.Platforms);
                }
            }
            WorldGen.RangeFrame(left - 1, floorY - ArenaHeight - 1, right + 1, floorY + 1);
            Main.refreshMap = true;

            for (int i = 0; i < player.inventory.Length; i++)
                player.inventory[i].TurnToAir();
            for (int i = 0; i < player.armor.Length; i++)
                player.armor[i].TurnToAir();
            for (int i = 0; i < player.miscEquips.Length; i++)
                player.miscEquips[i].TurnToAir(); // includes the grappling hook slot
            player.inventory[0].SetDefaults(ItemID.Minishark);
            player.inventory[54].SetDefaults(ItemID.EndlessMusketPouch); // first ammo slot
            player.armor[3].SetDefaults(ItemID.CreativeWings); // Fledgling Wings, first accessory slot
            player.selectedItemState.Select(0);

            for (int i = 0; i < player.buffType.Length; i++)
            {
                player.buffType[i] = 0;
                player.buffTime[i] = 0;
            }
            player.statLifeMax = PlayerLife;
            player.statLife = PlayerLife;

            player.position = new Vector2(Main.spawnTileX * 16, floorY * 16 - player.height);
            player.velocity = Vector2.Zero;
            player.fallStart = (int)(player.position.Y / 16f); // fall damage counts from here

            for (int i = 0; i < Main.maxNPCs; i++)
            {
                if (!Main.npc[i].townNPC)
                    Main.npc[i].active = false;
            }
            for (int i = 0; i < Main.maxProjectiles; i++)
                Main.projectile[i].active = false;
            for (int i = 0; i < Main.maxItems; i++)
                Main.item[i].TurnToAir(); // items on the ground
        }

        private static bool KeyDown(Keys key)
        {
            // keyState is empty while the game window is not focused
            return Main.keyState.IsKeyDown(key) && !Main.drawingPlayerChat;
        }
    }
}
