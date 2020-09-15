using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

/*
    This is an adaption of the work of Maurício Gomes (OrSpeeder), who created
    the original Sprint and Dash Mod, and released it under the  GNU General 
    Public License. Sprint and Dash Mod Redux will also use it.

    Sprint and Dash Mod Redux is free software: you can redistribute it 
    and/or modify it under the terms of the GNU General Public License as 
    published by the Free Software Foundation, either version 3 of the 
    License, or (at your option) any later version.

    For information on this license, see: <http://www.gnu.org/licenses/>

    Sprint and Dash Mod Redux mod is distributed in the hope that it will be 
    useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.
 */

namespace SprintAndDashRedux
{
    public class SprintDashMain : Mod
    {
        /*********
        ** Properties
        *********/
        private SprintDashConfig myConfig;

        private Farmer myPlayer;

        /// <summary>The stamina cost per tick for sprinting.</summary>
        private float StamCost;

        /// <summary>Length of time combat dash lasts in milliseconds.</summary>
        private int DashDuration;

        /// <summary>2.5x duration.</summary>
        private int DashCooldown;

        /// <summary>Number of seconds of sprinting before player is winded, or 0 to disable windedness.</summary>
        private int WindedStep;

        /// <summary>Whether the player can get winded.</summary>
        private bool EnableWindedness;

        /// <summary>Whether we're operating the button as a toggle.</summary>
        private bool EnableToggle;

        /// <summary>At what tick count should we actually do update logic</summary>
        private uint IntervalTicks;

        /// <summary>Whether to log.</summary>
        private bool Verbose;

        /// <summary>How long player has been sprinting.</summary>
        private int SprintTime;

        /// <summary>Running calculation of stamina to use up.</summary>
        private float StamToConsume;

        /// <summary>How many "stages" of winding (+1 per windedStep seconds) the player has accumulated.</summary>
        private int StepsProgressed;

        /// <summary>Multiplier to <see cref="StamCost"/> based on windedness.</summary>
        private int WindedAccumulated;

        /// <summary>When winded but no longer sprinting, this governs how quickly windedness goes away.</summary>
        private int WindedCooldownStep;

        /// <summary>Whether the sprint function is toggled on.</summary>
        private bool SprintToggledOn;

        //Not really sure I understand why it was necessary to use these identifier ints rather than just comparing references to Buff objects, but these probably should be deprecated.
        private const int SprintBuffID = 58012395;
        private const int DashBuffID = 623165;
        private const int CooldownBuffID = 6890125;

        private Buff SprintBuff;
        private Buff DashBuff;
        private Buff CooldownBuff;

        /// <summary>Does nothing, exists to time cooldown of windedness.</summary>
        private Buff WindedBuff;

        //private KeyboardState CurrentKeyboardState;

        private Keys[] RunKey;

        /// <summary>Don't need to cooldown for dash by default</summary>
        private bool NeedCooldown;

        /// <summary>The sprint time. This is 1.2 seconds to accomodate a range of update intervals.</summary>
        private readonly int SprintBuffDuration = 1200;

        /// <summary>When to check to refresh buffs.</summary>
        private int TimeoutCheck;

        /// <summary>The current milliseconds left for a buff.</summary>
        private int CurrentTimeLeft;

        /// <summary>How long to refresh a status if relevant.</summary>
        private int RefreshTime;

        /// <summary>How little stamina player must have for sprint to refresh.</summary>
        private float MinStaminaToRefresh;


        /*********
        ** Public methods
        *********/
        /// <summary>The mod entry point, called after the mod is first loaded.</summary>
        /// <param name="helper">Provides simplified APIs for writing mods.</param>
        public override void Entry(IModHelper helper)
        {
            SprintBuff = new Buff(0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 1, "Sprinting", "Sprinting");
            CooldownBuff = new Buff(0, 0, 0, 0, 0, 0, 0, 0, 0, -1, -2, -2, 1, "Combat Dash Cooldown", "Combat Dash Cooldown");
            WindedBuff = new Buff(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, "Winded", "Winded");

            // read config
            myConfig = helper.ReadConfig<SprintDashConfig>();
            StamCost = Math.Max(1, myConfig.StamCost);
            DashDuration = Math.Min(10, myConfig.DashDuration) * 1000; // 1-10 seconds, < 0  turns off at later step.
            DashCooldown = (int)(DashDuration * 2.5);
            WindedStep = myConfig.WindedStep;
            if (WindedStep > 0)
            {
                EnableWindedness = true;
                WindedCooldownStep = WindedStep * 200;  //Recovering from winded-ness take 1/5 the time spent being winded.
                WindedStep *= 1000; // convert config-based times to ms
            }
            MinStaminaToRefresh = Math.Max(0, myConfig.QuitSprintingAt);
            EnableToggle = myConfig.ToggleMode;
            RunKey = null;

            //60 tick/sec, interval in 0-1 seconds acts as multiplier.
            IntervalTicks = Math.Max(1, Math.Min(60, (uint)(myConfig.TimeInterval * 60.0)));
            //Want a timeout that is slightly more than the interval. (1 itck is ~16.666.. ms)
            TimeoutCheck = 5 + Math.Max(17, Math.Min(1000, (int)(myConfig.TimeInterval * 1000)));

            Verbose = myConfig.VerboseMode;

            // hook events
            helper.Events.GameLoop.SaveLoaded += StartupTasks;
            helper.Events.GameLoop.UpdateTicked += GameEvents_UpdateTick;
            helper.Events.Input.ButtonPressed += Input_ButtonPressed;

            // log info
            Monitor.Log("Sprint & Dash Redux => Initialized", LogLevel.Info);
            LogIt($"Stamina cost: {StamCost}, dash duration: {DashDuration}, dash cooldown: {DashCooldown}, winded step: {WindedStep}, toggle mode: {EnableToggle}", LogLevel.Trace);
        }

        private void StartupTasks(object sender, SaveLoadedEventArgs e)
        {
            myPlayer = Game1.player;
        }

        /*********
        ** Private methods
        *********/
        //Common task.
        void StartSprintBuff()
        {
            LogIt("Starting to sprint...");

            SprintBuff.millisecondsDuration = SprintBuffDuration;
            SprintBuff.which = SprintDashMain.SprintBuffID;
            Game1.buffsDisplay.addOtherBuff(SprintBuff);

            StamToConsume = StamCost + WindedAccumulated;
            if (myPlayer.stamina > StamToConsume) myPlayer.stamina -= StamToConsume;
            else myPlayer.stamina = 0;
        }

        //Controlled logging.
        void LogIt(string msg, LogLevel lvl = LogLevel.Trace)
        {
            if (Verbose) Monitor.Log(msg, lvl);
        }

        //Special log output for winded system.
        private void WindedTest()
        {
            LogIt($"(Winded Status) sprint time: {SprintTime}, steps progressed: {StepsProgressed}, winded accumulated: {WindedAccumulated}");
        }

        /// <summary>Detect key press.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        void Input_ButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            // do nothing if the conditions aren't favorable
            if (!Game1.shouldTimePass() || myPlayer.isRidingHorse())
                return;

            SButton pressedButton = e.Button;
            
            //Used to detect running
            Keys pressedKey;
            if (pressedButton.TryGetKeyboard(out Keys key)) pressedKey = key;
            else pressedKey = Keys.None;

            // dashing is a time-limited thing, just do it on a press
            if (DashDuration > 0 && pressedButton == myConfig.DashKey && !NeedCooldown)
            {
                foreach (Buff buff in Game1.buffsDisplay.otherBuffs)
                {
                    if (buff == CooldownBuff || buff == DashBuff)
                        return;
                }

                int speed = (myPlayer.FarmingLevel / 2) + 1;
                int defense = (myPlayer.ForagingLevel / 2 + myPlayer.FishingLevel / 3) +1;
                int attack = myPlayer.CombatLevel + 1;

                DashBuff = new Buff(0, 0, 0, 0, 0, 0, 0, 0, 0, speed, defense, attack, 1, "Combat Dash", "Combat Dash")
                {
                    millisecondsDuration = DashDuration,
                    which = SprintDashMain.DashBuffID,
                    glow = Color.AntiqueWhite
                };
                Game1.buffsDisplay.addOtherBuff(DashBuff);
                float staminaToConsume = speed + defense + attack + 10;
                float healthToConsume = 0f;
                if (staminaToConsume > myPlayer.Stamina)
                {
                    healthToConsume = staminaToConsume - myPlayer.Stamina;
                    staminaToConsume = Math.Max(myPlayer.Stamina - 1, 0f);
                }
                myPlayer.Stamina -= staminaToConsume;
                myPlayer.health = Math.Max(1, myPlayer.health - (int)healthToConsume);
                NeedCooldown = true;

                if (healthToConsume == 0)
                {
                    Game1.playSound("hoeHit");
                    Game1.playSound("hoeHit");
                    Game1.playSound("hoeHit");
                }
                else
                {
                    Game1.playSound("hoeHit");
                    Game1.playSound("ow");
                }

                Vector2 tileLocation = myPlayer.getTileLocation();
                Vector2 initialTile = myPlayer.getTileLocation();
                Game1.currentLocation.temporarySprites.Add(new TemporaryAnimatedSprite(6, new Vector2(tileLocation.X * Game1.tileSize, tileLocation.Y * Game1.tileSize), Color.White, 8, Game1.random.NextDouble() < 0.5, Vector2.Distance(initialTile, tileLocation) * 30f));
                tileLocation.X -= 1;
                Game1.currentLocation.temporarySprites.Add(new TemporaryAnimatedSprite(6, new Vector2(tileLocation.X * Game1.tileSize, tileLocation.Y * Game1.tileSize), Color.White, 8, Game1.random.NextDouble() < 0.5, Vector2.Distance(initialTile, tileLocation) * 30f));
                tileLocation.X += 2;
                Game1.currentLocation.temporarySprites.Add(new TemporaryAnimatedSprite(6, new Vector2(tileLocation.X * Game1.tileSize, tileLocation.Y * Game1.tileSize), Color.White, 8, Game1.random.NextDouble() < 0.5, Vector2.Distance(initialTile, tileLocation) * 30f));
                tileLocation.X -= 1;
                tileLocation.Y -= 1;
                Game1.currentLocation.temporarySprites.Add(new TemporaryAnimatedSprite(6, new Vector2(tileLocation.X * Game1.tileSize, tileLocation.Y * Game1.tileSize), Color.White, 8, Game1.random.NextDouble() < 0.5, Vector2.Distance(initialTile, tileLocation) * 30f));
                tileLocation.Y += 2;
                Game1.currentLocation.temporarySprites.Add(new TemporaryAnimatedSprite(6, new Vector2(tileLocation.X * Game1.tileSize, tileLocation.Y * Game1.tileSize), Color.White, 8, Game1.random.NextDouble() < 0.5, Vector2.Distance(initialTile, tileLocation) * 30f));

                LogIt($"Activating dash for {DashBuff.millisecondsDuration}ms with buff of +{speed} speed, +{defense} defense, +{attack} attack");
            }
            else if (pressedButton == myConfig.SprintKey)
            {
                //If we aren't in toggle mode or aren't currently toggled sprinting, start the buff.
                if (!SprintToggledOn) StartSprintBuff();

                if (EnableToggle)
                {
                    SprintToggledOn = !SprintToggledOn;

                    //Re-enable autorun if sprinting because sprint-walking is, uh, dumb.
                    if (!Game1.options.autoRun && SprintToggledOn) Game1.options.autoRun = true;
                }
            }
            //At this point, the run button is basically a toggle for autorun. Not sure if this is the best feature honestly but eh.
            else if (RunKey.Contains(pressedKey) && EnableToggle)
            {
                Game1.options.autoRun = !Game1.options.autoRun;

                //Disable sprinting if we're no longer running because sprint-walking is, uh, dumb.
                if (SprintToggledOn && !Game1.options.autoRun) SprintToggledOn = false;
            }
        }

        //Commonly used check.
        private bool IsInSprintMode()
        {
            if (Helper.Input.IsDown(myConfig.SprintKey) || SprintToggledOn) return true;
            else return false;
        }

        //Do this every tick. Checks for persistent effects, does first-run stuff.
        private void GameEvents_UpdateTick(object sender, UpdateTickedEventArgs e)
        {
            /*
             * Only updates if:
             * 1. We're on the appropriate interval.
             * 2. Not in menu, cutscene, etc.
             * 3. Player exists. (This eliminates some errors.)
             */
            if (!e.IsMultipleOf(IntervalTicks) || !Game1.shouldTimePass() || myPlayer == null) return;

            Buff curBuff = null;

            //This is complicated and necessary because SDV stores the run button as an array of buttons. Theoretically we may have more than one.
            if (RunKey == null)
            {
                RunKey = new Keys[Game1.options.runButton.Length];

                int i = 0;

                foreach (InputButton button in Game1.options.runButton)
                {
                    RunKey[i] = button.key;
                    i++;
                }
            }

            //Cancel toggled sprinting if on horseback
            if (myPlayer.isRidingHorse() && EnableToggle && SprintToggledOn) SprintToggledOn = false;

            //Apply the cooldown bluff if needed.
            if (NeedCooldown)
            {
                bool canAddCooldown = true;
                foreach (Buff buff in Game1.buffsDisplay.otherBuffs)
                {
                    if (buff == CooldownBuff || buff == DashBuff)
                        canAddCooldown = false;
                }

                if (canAddCooldown)
                {
                    NeedCooldown = false;
                    CooldownBuff.millisecondsDuration = DashCooldown;
                    CooldownBuff.which = SprintDashMain.CooldownBuffID;
                    CooldownBuff.glow = Color.DarkRed;
                    Game1.buffsDisplay.addOtherBuff(CooldownBuff);

                    LogIt($"Dash cooldown activated for {CooldownBuff.millisecondsDuration}ms.");
                }
            }

            //Apply or refresh sprint buff if needed.
            if (myPlayer.isMoving() && IsInSprintMode() && !myPlayer.isRidingHorse())
            {
                if (SprintTime < 0) SprintTime = 0;

                //Only buff we're interested in is sprint
                foreach (Buff buff in Game1.buffsDisplay.otherBuffs) if (buff == SprintBuff) curBuff = buff;

                //If we found the sprint buff... deal with it
                if (curBuff != null)
                {
                    CurrentTimeLeft = curBuff.millisecondsDuration;

                    //Sprinting should implicitly entail running.
                    if (!myPlayer.running) myPlayer.setRunning(true);

                    if (CurrentTimeLeft <= TimeoutCheck)
                    {
                        RefreshTime = SprintBuffDuration - CurrentTimeLeft;
                        SprintTime += RefreshTime;

                        if (EnableWindedness)
                        {
                            //Have to be over the step threshold.
                            if (SprintTime > WindedStep)
                            {
                                StepsProgressed = (int)Math.Floor((double)(SprintTime / WindedStep));
                                WindedAccumulated = (int)(StamCost * StepsProgressed);
                            }

                            WindedTest();
                        }
                        //else LogIt("Refreshing sprint...");

                        StamToConsume = StamCost + WindedAccumulated;
                        if (myPlayer.stamina > StamToConsume) myPlayer.stamina -= StamToConsume;
                        else myPlayer.stamina = 0;

                        //Only refresh duration if more than min stam remains.
                        if (myPlayer.stamina > MinStaminaToRefresh) curBuff.millisecondsDuration += RefreshTime;
                    }

                    curBuff = null;
                }
                //Only grant the buff if player has more than min stam
                else if (myPlayer.stamina > MinStaminaToRefresh) StartSprintBuff();
            }

            //We can cancel sprint mode immediately like this.
            if (!IsInSprintMode()) SprintBuff.millisecondsDuration = 0;

            if (EnableWindedness && SprintTime > 0 && (!IsInSprintMode() || !myPlayer.isMoving() || myPlayer.isRidingHorse()))
            {
                //Only buff we're interested in is winded
                foreach (Buff buff in Game1.buffsDisplay.otherBuffs) if (buff == WindedBuff) curBuff = buff;

                //If we found the winded buff... deal with it
                if (curBuff != null)
                {
                    CurrentTimeLeft = curBuff.millisecondsDuration;

                    if (CurrentTimeLeft <= TimeoutCheck)
                    {
                        RefreshTime = WindedCooldownStep - CurrentTimeLeft;

                        if (WindedAccumulated > 0)
                        {
                            StepsProgressed -= 1;
                            WindedAccumulated -= (int)StamCost;
                            curBuff.millisecondsDuration += RefreshTime;
                            SprintTime -= WindedStep;
                        }
                        else SprintTime -= RefreshTime;

                        if (WindedAccumulated < 0) WindedAccumulated = 0; // just in case

                        WindedTest();
                    }

                    curBuff = null;
                }
                //If not we need to add it
                else
                {
                    WindedBuff.millisecondsDuration = WindedCooldownStep;
                    //WindedBuff.glow = SprintTime > WindedStep ? Color.Khaki : Color.Transparent;
                    Game1.buffsDisplay.addOtherBuff(WindedBuff);

                    WindedTest();
                }
            }
        }
    }
}
