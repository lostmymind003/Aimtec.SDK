namespace Aimtec.SDK.Orbwalking
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Linq;

    using Aimtec.SDK.Damage;
    using Aimtec.SDK.Events;
    using Aimtec.SDK.Extensions;
    using Aimtec.SDK.Menu;
    using Aimtec.SDK.Menu.Components;
    using Aimtec.SDK.Menu.Config;
    using Aimtec.SDK.Prediction.Health;
    using Aimtec.SDK.TargetSelector;
    using Aimtec.SDK.Util;

    internal class OrbwalkingImpl : AOrbwalker
    {
        #region Fields

        /// <summary>
        ///     The time the last attack command was sent (determined locally)
        /// </summary>
        protected float LastAttackCommandSentTime;

        #endregion

        #region Constructors and Destructors

        internal OrbwalkingImpl()
        {
            this.Initialize();
        }

        #endregion

        #region Public Properties

        public float AnimationTime => Player.AttackCastDelay * 1000;

        public float AttackCoolDownTime
           =>
               (Player.ChampionName.Equals("Graves")
                    ? 1.07402968406677f * Player.AttackDelay - 0.716238141059875f
                    : Player.AttackDelay) * 1000 - this.AttackDelayReduction;

        public override bool IsWindingUp
        {
            get
            {
                var detectionTime = Math.Max(this.ServerAttackDetectionTick, this.LastAttackCommandSentTime);
                return Game.TickCount + Game.Ping / 2 - detectionTime <= this.WindUpTime;
            }
        }

        public override float WindUpTime => this.AnimationTime + this.ExtraWindUp;

        #endregion

        #region Properties

        protected bool AttackReady => Game.TickCount + Game.Ping / 2 - this.ServerAttackDetectionTick
            >= this.AttackCoolDownTime;

        private bool Attached { get; set; }



        private int AttackDelayReduction => this.Config["Advanced"]["AttackDelayReduction"].Value;

        private int ExtraWindUp => this.Config["Attacking"]["ExtraWindup"].Value;

        private int HoldPositionRadius => this.Config["Misc"]["HoldPositionRadius"].Value;

        private int FarmDelay => this.Config["Farming"]["FarmDelay"].Value;

        private bool DrawAttackRange => this.Config["Drawings"]["DrawAttackRange"].Enabled;

        private bool DrawHoldPosition => this.Config["Drawings"]["DrawHoldRadius"].Enabled;

        private bool DrawKillable => this.Config["Drawings"]["DrawKillableMinion"].Enabled;


        /// <summary>
        ///     Special auto attack names that do not trigger OnProcessAutoAttack
        /// </summary>
        private readonly string[] specialAttacks =
        {
            "caitlynheadshotmissile",
            "goldcardpreattack",
            "redcardpreattack",
            "bluecardpreattack",
            "viktorqbuff",
            "quinnwenhanced",
            "renektonexecute",
            "renektonsuperexecute",
            "trundleq",
            "xenzhaothrust",
            "xenzhaothrust2",
            "xenzhaothrust3",
            "frostarrow",
            "garenslash2",
            "kennenmegaproc",
            "masteryidoublestrike"
        };

        /// <summary>
        ///     Gets or sets the Forced Target
        /// </summary>
        private AttackableUnit ForcedTarget { get; set; }

        private AttackableUnit LastTarget { get; set; }

        //Members
        private float ServerAttackDetectionTick { get; set; }

        private Obj_AI_Hero GangPlank { get; set; }

        #endregion

        #region Public Methods and Operators

        public override void Attach(IMenu menu)
        {
            if (!this.Attached)
            {
                this.Attached = true;
                menu.Add(this.Config);
                Obj_AI_Base.OnProcessAutoAttack += this.ObjAiHeroOnProcessAutoAttack;
                Obj_AI_Base.OnProcessSpellCast += this.Obj_AI_Base_OnProcessSpellCast;
                Game.OnUpdate += this.Game_OnUpdate;
                SpellBook.OnStopCast += this.SpellBook_OnStopCast;
                Render.OnRender += this.RenderManager_OnRender;
            }
            else
            {
                this.Logger.Info("This Orbwalker instance is already attached to a Menu.");
            }
        }

        public override bool Attack(AttackableUnit target)
        {
            var preAttackargs = this.FirePreAttack(target);

            if (!preAttackargs.Cancel)
            {
                var targetToAttack = preAttackargs.Target;
                if (this.ForcedTarget != null)
                {
                    targetToAttack = this.ForcedTarget;
                }

                if (Player.IssueOrder(OrderType.AttackUnit, targetToAttack))
                {
                    this.LastAttackCommandSentTime = Game.TickCount;
                    return true;
                }
            }

            return false;
        }

        public bool BlindCheck()
        {
            if (!this.Config["Attacking"]["NoBlindAA"].Enabled)
            {
                return true;
            }

            if (!Player.ChampionName.Equals("Kalista") &&
                !Player.ChampionName.Equals("Twitch"))
            {
                if (Player.HasBuffOfType(BuffType.Blind))
                {
                    return false;
                }
            }

            return true;
        }

        public bool JaxECheck()
        {
            if (!this.Config["Attacking"]["NoCounterStrikeAA"].Enabled)
            {
                return true;
            }

            var target = Orbwalker.Implementation.GetOrbwalkingTarget();
            if (target == null)
            {
                return true;
            }

            var heroTarget = target as Obj_AI_Hero;
            if (heroTarget == null)
            {
                return true;
            }

            if (heroTarget.ChampionName.Equals("Jax"))
            {
                if (heroTarget.HasBuff("JaxCounterStrike"))
                {
                    return false;
                }
            }

            return true;
        }

        public bool IsValidAttackableObject(AttackableUnit unit)
        {
            //Valid check
            if (!unit.IsValidAutoRange())
            {
                return false;
            }

            if (unit is Obj_AI_Hero || unit is Obj_AI_Turret || unit.Type == GameObjectType.obj_BarracksDampener || unit.Type == GameObjectType.obj_HQ)
            {
                return true;
            }

            //J4 flag
            if (unit.Name.Contains("Beacon"))
            {
                return false;
            }

            var mBase = unit as Obj_AI_Base;

            if (mBase == null || !mBase.IsFloatingHealthBarActive) 
            {
                return false;
            }

            var minion = unit as Obj_AI_Minion;

            if (minion == null)
            {
                return false;
            }


            var name = minion.UnitSkinName.ToLower();
            if (!this.Config["Farming"]["AttackPlants"].Enabled && name.Contains("sru_plant_"))
            {
                return false;
            }

            if (!this.Config["Farming"]["AttackWards"].Enabled && name.Contains("ward"))
            {
                return false;
            }

            if (this.GangPlank != null)
            {
                if (name.Contains("gangplankbarrel"))
                {
                    if (!this.Config["Farming"]["AttackBarrels"].Enabled)
                    {
                        return false;
                    }

                    //dont attack ally barrels
                    if (this.GangPlank.IsAlly)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public override bool CanAttack()
        {
            return this.CanAttack(this.GetActiveMode());
        }

        public bool CanAttack(OrbwalkerMode mode)
        {
            if (mode == null)
            {
                return false;
            }

            if (!this.AttackingEnabled || !mode.AttackingEnabled)
            {
                return false;
            }

            if (Player.HasBuffOfType(BuffType.Polymorph))
            {
                return false;
            }

            if (!this.BlindCheck() || !this.JaxECheck())
            {
                return false;
            }

            if (Player.ChampionName.Equals("Jhin") && Player.HasBuff("JhinPassiveReload"))
            {
                return false;
            }

            if (Player.ChampionName.Equals("Graves") && !Player.HasBuff("GravesBasicAttackAmmo1"))
            {
                return false;
            }

            if (this.NoCancelChamps.Contains(Player.ChampionName))
            {
                return true;
            }

            if (this.IsWindingUp)
            {
                return false;
            }

            return this.AttackReady;
        }

        public override bool CanMove()
        {
            return this.CanMove(this.GetActiveMode());
        }

        public bool CanMove(OrbwalkerMode mode)
        {
            if (mode == null)
            {
                return false;
            }

            if (!this.MovingEnabled || !mode.MovingEnabled)
            {
                return false;
            }

            if (Player.Distance(Game.CursorPos) < this.HoldPositionRadius)
            {
                return false;
            }

            if (this.NoCancelChamps.Contains(Player.ChampionName))
            {
                return true;
            }

            if (this.IsWindingUp)
            {
                return false;
            }

            return true;
        }

        public override void Dispose()
        {
            this.Config.Dispose();
            Obj_AI_Base.OnProcessAutoAttack -= this.ObjAiHeroOnProcessAutoAttack;
            Obj_AI_Base.OnProcessSpellCast -= this.Obj_AI_Base_OnProcessSpellCast;
            Game.OnUpdate -= this.Game_OnUpdate;
            SpellBook.OnStopCast -= this.SpellBook_OnStopCast;
            Render.OnRender -= this.RenderManager_OnRender;
            this.Attached = false;
        }

        public override void ForceTarget(AttackableUnit unit)
        {
            this.ForcedTarget = unit;
        }

        public override AttackableUnit GetOrbwalkingTarget()
        {
            return this.LastTarget;
        }

        public override AttackableUnit FindTarget(OrbwalkerMode mode)
        {
            if (this.ForcedTarget != null &&
                this.ForcedTarget.IsValidAutoRange())
            {
                return this.ForcedTarget;
            }

            return mode?.GetTarget();
        }

        public override bool Move(Vector3 movePosition)
        {
            var preMoveArgs = this.FirePreMove(movePosition);

            if (!preMoveArgs.Cancel)
            {
                if (Player.IssueOrder(OrderType.MoveTo, preMoveArgs.MovePosition))
                {
                    return true;
                }
            }

            return false;
        }

        public override void Orbwalk()
        {
            var mode = this.GetActiveMode();
            if (mode == null)
            {
                return;
            }
            
            if (this.ForcedTarget != null &&
                !this.ForcedTarget.IsValidTarget())
            {
                this.ForcedTarget = null;
            }

#pragma warning disable 1587
            /// <summary>
            ///     Execute the specific logic for this mode if any
            /// </summary>
#pragma warning restore 1587
            mode.Execute();

            if (!mode.BaseOrbwalkingEnabled)
            {
                return;
            }

            if (this.CanAttack(mode))
            {
                var target = this.LastTarget = this.FindTarget(mode);
                if (target != null)
                {
                    this.Attack(target);
                }
            }

            if (this.CanMove(mode))
            {
                this.Move(Game.CursorPos);
            }
        }

        public override void ResetAutoAttackTimer()
        {
            this.ServerAttackDetectionTick = 0;
            this.LastAttackCommandSentTime = 0;
        }

        #endregion

        #region Methods

        protected void Game_OnUpdate()
        {
            if (Player.IsDead)
            {
                return;
            }

            this.Orbwalk();
        }

        protected void ObjAiHeroOnProcessAutoAttack(Obj_AI_Base sender, Obj_AI_BaseMissileClientDataEventArgs args)
        {
            if (sender.IsMe)
            {
                if (args.Target is AttackableUnit targ)
                {
                    this.ServerAttackDetectionTick = Game.TickCount - Game.Ping / 2;
                    this.LastTarget = targ;
                    this.ForcedTarget = null;
                    DelayAction.Queue((int)this.WindUpTime, () => this.FirePostAttack(targ));
                }
            }
        }

        private bool CanKillMinion(Obj_AI_Base minion, int time = 0)
        {
            var rtime = time == 0 ? this.TimeForAutoToReachTarget(minion) : time;

            var pred = this.GetPredictedHealth(minion, rtime);

            //The minions health will already be 0 by the time our auto attack reaches it, so no point attacking it...
            if (pred <= 0)
            {
                this.FireNonKillableMinion(minion);
                return false;
            }

            var dmg = Player.GetAutoAttackDamage(minion);

            var result = dmg - pred >= 0;

            return result;
        }

        /*
        float DamageDealtInTime(Obj_AI_Base sender, Obj_AI_Base minion, int time)
        {
            var autos = this.NumberOfAutoAttacksInTime(sender, minion, time);
            var dmg = autos * sender.GetAutoAttackDamage(minion);

            return (float)(autos * dmg);
        }
        */

        private static AttackableUnit GetHeroTarget()
        {
            return TargetSelector.Implementation.GetTarget(0, true);
        }

        private AttackableUnit GetLaneClearTarget()
        {
            if (UnderTurretMode())
            {
                //Temporarily...
                return this.GetLastHitTarget();
            }

            var attackable = ObjectManager.Get<AttackableUnit>().Where(this.IsValidAttackableObject);
            var attackableUnits = attackable as AttackableUnit[] ?? attackable.ToArray();

            IEnumerable<Obj_AI_Base> minions = attackableUnits
                .Where(x => x is Obj_AI_Base).Cast<Obj_AI_Base>().OrderByDescending(x => x.MaxHealth);

            //Killable
            AttackableUnit killableMinion = minions.FirstOrDefault(x => this.CanKillMinion(x));

            if (killableMinion != null)
            {
                return killableMinion;
            }

            var waitableMinion = minions.Any(this.ShouldWaitMinion);

            if (waitableMinion)
            {
                Player.IssueOrder(OrderType.Stop, Player.Position);
                return null;
            }

            var structure = GetStructureTarget(attackableUnits);

            if (structure != null)
            {
                return structure;
            }

            if (this.LastTarget != null && this.LastTarget.IsValidAutoRange())
            {
                if (this.LastTarget is Obj_AI_Base b)
                {
                    var predHealth = this.GetPredictedHealth(b);

                    //taking damage
                    if (Math.Abs(this.LastTarget.Health - predHealth) < 0)
                    {
                        return this.LastTarget;
                    }
                }
            }


            foreach (var minion in minions)
            {
                var predHealth = this.GetPredictedHealth(minion);

                //taking damage
                if (minion.Health - predHealth > 0)
                {
                    continue;
                }

                return minion;
            }

            var first = minions.MaxBy(x => x.Health);

            if (first != null)
            {
                return first;
            }

            //Heros
            var hero = GetHeroTarget();
            if (hero != null)
            {
                return hero;
            }

            return null;
        }

        public static bool UnderTurretMode()
        {
            var nearestTurret = TurretAttackManager.GetNearestTurretData(Player, TurretAttackManager.TurretTeam.Ally);
            if (nearestTurret != null && nearestTurret.Turret.IsValid && nearestTurret.Turret.Distance(Player) + Player.AttackRange * 1.1 <= 950)
            {
                return true;
            }

            return false;
        }

        public AttackableUnit GetUnderTurret()
        {
            var attackable = ObjectManager.Get<AttackableUnit>().Where(this.IsValidAttackableObject);

            var nearestTurret = TurretAttackManager.GetNearestTurretData(Player, TurretAttackManager.TurretTeam.Ally);

            if (nearestTurret != null)
            {
                var attackableUnits = attackable as AttackableUnit[] ?? attackable.ToArray();
                var underTurret = attackableUnits.Where(x => x.ServerPosition.Distance(nearestTurret.Turret.ServerPosition) < 900 && x.IsValidAutoRange());

                if (underTurret.Any())
                {
                    var tData = TurretAttackManager.GetTurretData(nearestTurret.Turret.NetworkId);
                    if (tData != null && tData.TurretActive)
                    {
                        var tTarget = tData.LastTarget;
                        if (tTarget.IsValidAutoRange())
                        {
                            var attacks = tData.Attacks.Where(x => !x.Inactive);

                            foreach (var attack in attacks)
                            {
                                //turret related
                                var arrival = attack.PredictedLandTime;
                                var eta = arrival - Game.TickCount;
                                var tDmg = tData.Turret.GetAutoAttackDamage(tTarget);

                                //var tWillKill = tDmg > tTarget.Health;
                                var numTurretAutosToKill = (int)Math.Ceiling(tTarget.Health / tDmg);
                                var turretDistance = tData.Turret.Distance(tTarget) - Player.BoundingRadius - tTarget.BoundingRadius;
                                var tCastDelay = tData.Turret.AttackCastDelay * 1000;
                                var tTravTime = turretDistance / tData.Turret.BasicAttack.MissileSpeed * 1000;
                                var tTotalTime = tCastDelay + tTravTime + Game.Ping / 2f;

                                //myattack related
                                var castDelay = Player.AttackCastDelay * 1000;
                                //var minDelay = castDelay;
                                var dist = Player.Distance(tTarget) - Player.BoundingRadius - tTarget.BoundingRadius;
                                var travTime = dist / Player.BasicAttack.MissileSpeed * 1000;
                                var totalTime = (int)(castDelay + travTime + Game.Ping / 2f);

                                //minion hpred
                                var tMinionDmgPredHealth = HealthPrediction.Implementation.GetPrediction(tTarget, totalTime);

                                //myattack
                                const int ExtraBuffer = 50;
                                //if total time > turret attack arrival time by buffer (can be early/late)
                                var canReachSooner = totalTime - eta > ExtraBuffer;

                                var myAutoDmg = Player.GetAutoAttackDamage(tTarget);

                                //if my attk reach sooner than turret & my auto can kill it
                                if (canReachSooner && myAutoDmg >= tMinionDmgPredHealth)
                                {
                                    return tTarget;
                                }

                                var remHealth = tMinionDmgPredHealth - tDmg;
                                //var tNextAttackReachTime = tData.LastFireTime + tData.Turret.AttackDelay * 1000 + tCastDelay - Game.Ping / 2f;
                                //var myAttackReachTime = Game.TickCount + totalTime;
                                //var iReachSooner = myAttackReachTime - tNextAttackReachTime > 50;

                                //Minion wont die
                                if (remHealth > 0)
                                {
                                    if (remHealth <= myAutoDmg)
                                    {
                                        return null;
                                    }

                                    if (totalTime - tTotalTime < 50)
                                    {
                                        return null;
                                    }

                                    for (var i = 1; i <= numTurretAutosToKill; i++)
                                    {
                                        var dmg = i * tDmg;
                                        var health = tTarget.Health - dmg;
                                        if (health > 0 && health < myAutoDmg)
                                        {
                                            break;
                                        }

                                        if (i == numTurretAutosToKill)
                                        {
                                            return tTarget;
                                        }
                                    }
                                }

                                //Turret will kill min and nothing i can do about it
                                else
                                {
                                    foreach (var min in attackableUnits)
                                    {
                                        if (min.NetworkId == tTarget.NetworkId)
                                        {
                                            continue;
                                        }

                                        var minBase = min as Obj_AI_Base;
                                        if (minBase == null)
                                        {
                                            continue;
                                        }

                                        //myattack related
                                        var castDelay1 = Player.AttackCastDelay * 1000;
                                        var dist1 = Player.Distance(min) - Player.BoundingRadius - min.BoundingRadius;
                                        var travTime1 = dist1 / Player.BasicAttack.MissileSpeed * 1000;
                                        var totalTime1 = (int)(castDelay1 + travTime1 + Game.Ping / 2f);

                                        var dmg1 = Player.GetAutoAttackDamage(minBase);
                                        var pred1 = HealthPrediction.Implementation.GetPrediction(minBase, totalTime1);
                                        if (dmg1 > pred1)
                                        {
                                            return min;
                                        }
                                    }
                                }
                            }

                            /*
                            if (!attacks.Any())
                            {
                                var target = tData.LastTarget;
                                if (tData.LastTarget != null)
                                {
                                    var castDelay1 = Player.AttackCastDelay * 1000;
                                    var dist1 = Player.Distance(target) - Player.BoundingRadius - target.BoundingRadius;
                                    var travTime1 = (dist1 / Player.BasicAttack.MissileSpeed) * 1000;
                                    int totalTime1 = (int)(castDelay1 + travTime1 + Game.Ping / 2);
                                    var dmg1 = Player.GetAutoAttackDamage(target);
                                    var pred = HealthPrediction.Instance.GetPrediction(target, totalTime1);

                                    if (pred <= dmg1)
                                    {
                                        return target;
                                    }
                                }
                            }
                            */
                        }
                    }
                }
            }

            return null;
        }

        private AttackableUnit GetLastHitTarget()
        {
            return this.GetLastHitTarget(null);
        }

        private AttackableUnit GetLastHitTarget(IEnumerable<AttackableUnit> attackable)
        {
            if (attackable == null)
            {
                attackable = ObjectManager.Get<AttackableUnit>().Where(this.IsValidAttackableObject);
            }

            var availableMinionTargets = attackable
                .OfType<Obj_AI_Base>().Where(x => this.CanKillMinion(x));

            var bestMinionTarget = availableMinionTargets
                .OrderByDescending(x => x.MaxHealth)
                .ThenBy(x => x.Health).FirstOrDefault();

            return bestMinionTarget;
        }

        //In mixed mode we prioritize killable units, then structures, then heros. If none are found, then we don't attack anything.
        private AttackableUnit GetMixedModeTarget()
        {
            var attackable = ObjectManager.Get<AttackableUnit>().Where(this.IsValidAttackableObject);

            var attackableUnits = attackable as AttackableUnit[] ?? attackable.ToArray();

            var killable = this.GetLastHitTarget(attackableUnits);

            //Killable unit 
            if (killable != null)
            {
                return killable;
            }

            //Structures
            var structure = GetStructureTarget(attackableUnits);
            if (structure != null)
            {
                return structure;
            }

            //Heros
            var hero = GetHeroTarget();
            if (hero != null)
            {
                return hero;
            }

            return null;
        }

        private int GetPredictedHealth(Obj_AI_Base minion, int time = 0)
        {
            var rtime = time == 0 ? this.TimeForAutoToReachTarget(minion) : time;
            return (int)Math.Ceiling(HealthPrediction.Implementation.GetPrediction(minion, rtime));
        }

        //Gets a structure target based on the following order (Nexus, Turret, Inihibitor)
        private static AttackableUnit GetStructureTarget(IEnumerable<AttackableUnit> attackable)
        {
            //Nexus
            var attackableUnits = attackable as AttackableUnit[] ?? attackable.ToArray();
            var nexus = attackableUnits.Where(x => x.Type == GameObjectType.obj_HQ).MinBy(x => x.Distance(Player));
            if (nexus != null && nexus.IsValidAutoRange())
            {
                return nexus;
            }

            //Turret
            var turret = attackableUnits.Where(x => x is Obj_AI_Turret).MinBy(x => x.Distance(Player));
            if (turret != null && turret.IsValidAutoRange())
            {
                return turret;
            }

            //Inhib
            var inhib = attackableUnits.Where(x => x.Type == GameObjectType.obj_BarracksDampener)
                                       .MinBy(x => x.Distance(Player));
            if (inhib != null && inhib.IsValidAutoRange())
            {
                return inhib;
            }

            return null;
        }

        private void Initialize()
        {
            this.Config = new Menu("Orbwalker", "Orbwalker")
            {
                new Menu("Advanced", "Advanced")
                {
                    new MenuSlider("AttackDelayReduction", "Attack Delay Reduction", 90, 0, 180, true)
                },

                new Menu("Attacking", "Attacking") {

                    new MenuSlider("ExtraWindup", "Additional Windup", Game.Ping / 2 + 10, 0, 200, true),
                    new MenuBool("NoBlindAA", "No AA when Blind", true, true),
                    new MenuBool("NoCounterStrikeAA", "No AA against E'ing Jax", true, true)
                },

                new Menu("Farming", "Farming")
                {
                    new MenuSlider("FarmDelay", "Farm Delay", 0, 0, 120, true).SetToolTip("Additional Delay for auto attack when farming"),
                    new MenuBool("AttackPlants", "Attack Plants", false, true),
                    new MenuBool("AttackWards", "Attack Wards", true, true),
                    new MenuBool("AttackBarrels", "Attack Barrels", true, true)
                },

                new Menu("Misc", "Misc")
                {
                    new MenuSlider("HoldPositionRadius", "Hold Radius", 50, 0, 400, true)
                },

                new Menu("Drawings", "Drawings")
                {
                    new MenuBool("DrawAttackRange", "Draw Attack Range"),
                    new MenuBool("DrawHoldRadius", "Draw Hold Radius"),
                    new MenuBool("DrawKillableMinion", "Indicate Killable")
                }
            };

            this.AddMode(this.Combo = new OrbwalkerMode("Combo", GlobalKeys.ComboKey, GetHeroTarget, null));
            this.AddMode(this.LaneClear = new OrbwalkerMode("Laneclear", GlobalKeys.WaveClearKey, this.GetLaneClearTarget, null));
            this.AddMode(this.LastHit = new OrbwalkerMode("Lasthit", GlobalKeys.LastHitKey, this.GetLastHitTarget, null));
            this.AddMode(this.Mixed = new OrbwalkerMode("Mixed", GlobalKeys.MixedKey, this.GetMixedModeTarget, null));

            this.GpCheck();

            GameEvents.GameStart += this.GameEventsGameStart;
        }

        private void GpCheck()
        {
            var gp = ObjectManager.Get<Obj_AI_Hero>().FirstOrDefault(x => x.ChampionName.ToLower().Equals("gangplank"));
            if (gp != null)
            {
                this.GangPlank = gp;
            }
        }

        private void GameEventsGameStart()
        {
            this.GpCheck();
        }

        /*
        private int NumberOfAutoAttacksInTime(Obj_AI_Base sender, AttackableUnit minion, int time)
        {
            var basetimePerAuto = this.TimeForAutoToReachTarget(minion);

            var numberOfAutos = 0;
            var adjustedTime = 0;

            if (basetimePerAuto > time)
            {
                return 0;
            }

            if (this.AttackReady)
            {
                numberOfAutos++;
                adjustedTime = time - basetimePerAuto;
            }

            var fullTimePerAuto = basetimePerAuto + sender.AttackDelay * 1000;
            var additionalAutos = (int)Math.Ceiling(adjustedTime / fullTimePerAuto);

            numberOfAutos += additionalAutos;

            return numberOfAutos;
        }
        */

        private void Obj_AI_Base_OnProcessSpellCast(Obj_AI_Base sender, Obj_AI_BaseMissileClientDataEventArgs e)
        {
            if (sender.IsMe)
            {
                var name = e.SpellData.Name.ToLower();

                if (this.specialAttacks.Contains(name))
                {
                    this.ObjAiHeroOnProcessAutoAttack(sender, e);
                }

                if (this.IsReset(name))
                {
                    this.ResetAutoAttackTimer();
                }
            }
        }

        private void RenderManager_OnRender()
        {
            if (this.DrawAttackRange)
            {
                Render.Circle(Player.Position, Player.AttackRange + Player.BoundingRadius, 30, Color.DeepSkyBlue);
            }

            if (this.DrawHoldPosition)
            {
                Render.Circle(Player.Position, this.HoldPositionRadius, 30, Color.White);
            }

            if (this.DrawKillable)
            {
                foreach (var m in ObjectManager.Get<Obj_AI_Minion>().Where(x => x.IsValidTarget(Player.AttackRange * 2) && x.Health <= Player.GetAutoAttackDamage(x)))
                {
                    Render.Circle(m.Position, 50, 30, Color.LimeGreen);
                }
            }
        }

        private bool ShouldWaitMinion(Obj_AI_Base minion)
        {
            var time = this.TimeForAutoToReachTarget(minion) + (int)Player.AttackDelay * 1000 + 100;
            var pred = HealthPrediction.Implementation.GetLaneClearHealthPrediction(minion, (int)(time * 2f));
            var dmg = Player.GetAutoAttackDamage(minion);

            if (pred < dmg)
            {
                return true;
            }

            return false;
        }

        private void SpellBook_OnStopCast(Obj_AI_Base sender, SpellBookStopCastEventArgs e)
        {
            if (sender.IsMe && (e.DestroyMissile || e.ForceStop || e.StopAnimation))
            {
                this.ResetAutoAttackTimer();
            }
        }

        // ReSharper disable once SuggestBaseTypeForParameter
        private int TimeForAutoToReachTarget(AttackableUnit minion, bool applyDelay = false)
        {
            var dist = Player.Distance(minion) - Player.BoundingRadius - minion.BoundingRadius;
            var ms = Player.IsMelee ? int.MaxValue : Player.BasicAttack.MissileSpeed;
            var attackTravelTime = dist / ms * 1000f;
            var totalTime = (int)(this.AnimationTime + attackTravelTime + Game.Ping / 2f - 100);
            return totalTime + (applyDelay ? this.FarmDelay : 0);
        }

        #endregion
    }
}
