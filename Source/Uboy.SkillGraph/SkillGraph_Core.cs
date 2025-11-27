using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace SkillGraph
{
    // ==========================================
    // 1. 데이터 모델 (Data Models)
    // ==========================================

    public class SkillSnapshot : IExposable
    {
        public int tickAbs;
        public int level;
        public float xpProgress;

        public void ExposeData()
        {
            Scribe_Values.Look(ref tickAbs, "tickAbs");
            Scribe_Values.Look(ref level, "level");
            Scribe_Values.Look(ref xpProgress, "xpProgress");
        }
    }

    public class PawnSkillHistory : IExposable
    {
        public Dictionary<SkillDef, List<SkillSnapshot>> skillRecords = new Dictionary<SkillDef, List<SkillSnapshot>>();

        public void ExposeData()
        {
            Scribe_Collections.Look(ref skillRecords, "skillRecords", LookMode.Def, LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (skillRecords == null) skillRecords = new Dictionary<SkillDef, List<SkillSnapshot>>();
            }
        }
    }

    // ==========================================
    // 2. 게임 컴포넌트 (GameComponent)
    // ==========================================

    public class SkillGraphGameComponent : GameComponent
    {
        private Dictionary<string, PawnSkillHistory> historyData = new Dictionary<string, PawnSkillHistory>();

        private int lastRecordedTick = -1;

        private const int RecordInterval = 60000; // 1일 간격
        private const int MaxRecordsPerSkill = 1200;

        public SkillGraphGameComponent(Game game)
        {
        }

        public PawnSkillHistory GetHistory(Pawn pawn)
        {
            if (pawn == null) return null;
            if (historyData.TryGetValue(pawn.ThingID, out PawnSkillHistory history))
            {
                return history;
            }
            return null;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref historyData, "historyData", LookMode.Value, LookMode.Deep);
            Scribe_Values.Look(ref lastRecordedTick, "lastRecordedTick", -1);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (historyData == null) historyData = new Dictionary<string, PawnSkillHistory>();
            }
        }

        public override void GameComponentTick()
        {
            // [최적화] LongTick 구현
            // GameComponent는 LongTick이 없으므로, 직접 구현합니다.
            // 2000틱(약 33.33초)마다 한 번만 로직을 수행하여 CPU 연산을 1/2000로 줄입니다.
            int currentTick = Find.TickManager.TicksGame;
            if (currentTick % 2000 != 0) return;

            // 기록 주기가 되었는지 확인 (LongTick 주기만큼의 오차는 허용)
            if (currentTick - lastRecordedTick >= RecordInterval)
            {
                RecordSkills();
                lastRecordedTick = currentTick;
            }
        }

        private void RecordSkills()
        {
            var pawns = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_OfPlayerFaction;
            foreach (Pawn pawn in pawns)
            {
                if (pawn.RaceProps.Humanlike && pawn.skills != null)
                {
                    RecordPawn(pawn);
                }
            }

            var prisoners = PawnsFinder.AllMaps_PrisonersOfColony;
            foreach (Pawn prisoner in prisoners)
            {
                if (prisoner.RaceProps.Humanlike && prisoner.skills != null)
                {
                    RecordPawn(prisoner);
                }
            }
        }

        private void RecordPawn(Pawn pawn)
        {
            if (!historyData.ContainsKey(pawn.ThingID))
            {
                historyData[pawn.ThingID] = new PawnSkillHistory();
            }

            PawnSkillHistory history = historyData[pawn.ThingID];
            int currentTick = Find.TickManager.TicksGame;

            foreach (SkillRecord skill in pawn.skills.skills)
            {
                if (!history.skillRecords.ContainsKey(skill.def))
                {
                    history.skillRecords[skill.def] = new List<SkillSnapshot>();
                }

                List<SkillSnapshot> records = history.skillRecords[skill.def];

                if (records.Count >= MaxRecordsPerSkill)
                {
                    CullOldData(records);
                }

                SkillSnapshot snapshot = new SkillSnapshot
                {
                    tickAbs = currentTick,
                    level = skill.Level,
                    xpProgress = skill.XpProgressPercent
                };
                records.Add(snapshot);
            }
        }

        private void CullOldData(List<SkillSnapshot> records)
        {
            if (records.Count < 10) return;

            int preserveCount = records.Count / 5;
            int cullEndIndex = records.Count - preserveCount;

            List<SkillSnapshot> keptRecords = new List<SkillSnapshot>();
            keptRecords.Add(records[0]);

            for (int i = 1; i < records.Count; i++)
            {
                if (i >= cullEndIndex)
                {
                    keptRecords.Add(records[i]);
                }
                else if (i % 2 == 0)
                {
                    keptRecords.Add(records[i]);
                }
            }

            records.Clear();
            records.AddRange(keptRecords);
        }
    }

    // ==========================================
    // 3. 탭 주입기 (Injector)
    // ==========================================

    [StaticConstructorOnStartup]
    public static class TabInjector
    {
        static TabInjector()
        {
            Log.Message("[SkillGraph] Injector started...");

            int count = 0;
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                if (def.race != null && def.race.Humanlike)
                {
                    if (def.inspectorTabs == null)
                        def.inspectorTabs = new List<Type>();

                    if (def.inspectorTabs.Contains(typeof(ITab_Pawn_SkillHistory)))
                    {
                        def.inspectorTabs.Remove(typeof(ITab_Pawn_SkillHistory));
                    }
                    def.inspectorTabs.Add(typeof(ITab_Pawn_SkillHistory));

                    if (def.inspectorTabsResolved == null)
                        def.inspectorTabsResolved = new List<InspectTabBase>();

                    def.inspectorTabsResolved.RemoveAll(t => t is ITab_Pawn_SkillHistory);

                    try
                    {
                        InspectTabBase tabInstance = InspectTabManager.GetSharedInstance(typeof(ITab_Pawn_SkillHistory));
                        if (tabInstance != null)
                        {
                            def.inspectorTabsResolved.Add(tabInstance);
                            count++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[SkillGraph] Failed to inject tab into {def.defName}: {ex}");
                    }
                }
            }
            Log.Message($"[SkillGraph] Injector finished. Tab forced to end for {count} races.");
        }
    }
}