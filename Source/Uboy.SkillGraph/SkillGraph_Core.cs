using RimWorld;
using SkillGraph;
using System;
using System.Collections.Generic;
using System.Linq;
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

    public class SkillDataLayers : IExposable
    {
        public List<SkillSnapshot> layer0 = new List<SkillSnapshot>();
        public List<SkillSnapshot> layer1 = new List<SkillSnapshot>();
        public int removedCount = 0;  // ← 추가: layer0에서 제거된 데이터 개수 추적

        public List<SkillSnapshot> GetAllData()
        {
            var allData = new List<SkillSnapshot>();
            allData.AddRange(layer0);
            allData.AddRange(layer1);
            return allData.OrderBy(s => s.tickAbs).ToList();
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref layer0, "layer0", LookMode.Deep);
            Scribe_Collections.Look(ref layer1, "layer1", LookMode.Deep);
            Scribe_Values.Look(ref removedCount, "removedCount", 0);  // ← 추가
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (layer0 == null) layer0 = new List<SkillSnapshot>();
                if (layer1 == null) layer1 = new List<SkillSnapshot>();
            }
        }
    }

    public class PawnSkillHistory : IExposable
    {
        public Dictionary<SkillDef, SkillDataLayers> skillLayers
            = new Dictionary<SkillDef, SkillDataLayers>();

        // 기존 호환성을 위한 마이그레이션
        public Dictionary<SkillDef, List<SkillSnapshot>> skillRecords
            = new Dictionary<SkillDef, List<SkillSnapshot>>();

        public void ExposeData()
        {
            Scribe_Collections.Look(ref skillLayers, "skillLayers", LookMode.Def, LookMode.Deep);
            Scribe_Collections.Look(ref skillRecords, "skillRecords", LookMode.Def, LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (skillLayers == null) skillLayers = new Dictionary<SkillDef, SkillDataLayers>();
                if (skillRecords == null) skillRecords = new Dictionary<SkillDef, List<SkillSnapshot>>();

                if (skillRecords.Count > 0 && skillLayers.Count == 0)
                {
                    MigrateOldData();
                    skillRecords.Clear();
                }
            }
        }

        private void MigrateOldData()
        {
            const int Layer0_EndTick = 300 * 60000;
            foreach (var kvp in skillRecords)
            {
                var layers = new SkillDataLayers();
                foreach (var snapshot in kvp.Value)
                {
                    if (snapshot.tickAbs <= Layer0_EndTick)
                        layers.layer0.Add(snapshot);
                    else
                        layers.layer1.Add(snapshot);
                }
                skillLayers[kvp.Key] = layers;
            }
            Log.Message($"[SkillGraph] Migration completed: {skillRecords.Count} skills converted");
        }
    }

    // ==========================================
    // 2. 게임 컴포넌트 (GameComponent)
    // ==========================================

    public class SkillGraphGameComponent : GameComponent
    {
        // ==========================================
        // 🔧 테스트 vs 프로덕션 설정 (조건부 컴파일)
        // ==========================================
#if DEBUG
        // 테스트용 설정: RecordInterval = 60 (약 1초마다)
        private const int RecordInterval = 60;
        private const int Layer0RecordCount = 30;  // 30번 기록 (약 30초)
        private const int Layer1RecordSkip = 3;   // 3번에 1번 기록
        private const int MaxRecordsPerSkill = 1800;  // 30 + 1770

#else
        // 프로덕션 설정: RecordInterval = 60000 (약 1일마다)
        private const int RecordInterval = 60000;
        private const int Layer0RecordCount = 300;  // 300번 기록 (약 300일)
        private const int Layer1RecordSkip = 3;    // 3일에 1번 기록
        private const int MaxRecordsPerSkill = 1800;  // 300 + 1500
#endif

        private Dictionary<string, PawnSkillHistory> historyData
            = new Dictionary<string, PawnSkillHistory>();

        private int lastRecordedTick = -1;
        private int recordPawnCallCount = 0;
        private int recordCount = 0;  // 기록 횟수 추적 (테스트용)

        public SkillGraphGameComponent(Game game)
        {
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            lastRecordedTick = Find.TickManager.TicksGame;
            recordCount = 0;
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
            int currentTick = Find.TickManager.TicksGame;
            if (currentTick % 30 != 0) return;

            if (currentTick - lastRecordedTick >= RecordInterval)
            {
                RecordSkills();
                lastRecordedTick = currentTick;
                recordCount++;

#if DEBUG
                // 테스트용: 기록 횟수 로그
                if (recordCount % 10 == 0)
                {
                    Log.Message($"[SkillGraph-TEST] Records: {recordCount}, Ticks: {currentTick}");
                }
#endif
            }
        }

        private void RecordSkills()
        {
            recordPawnCallCount++;
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
                if (!history.skillLayers.ContainsKey(skill.def))
                {
                    history.skillLayers[skill.def] = new SkillDataLayers();
                }

                SkillDataLayers layers = history.skillLayers[skill.def];

                // ==========================================
                // 🎯 사용자 제안 방식: FIFO + 3개마다 1개 샘플링
                // layer0: 항상 최신 30개 유지
                // layer1: layer0에서 제거되는 데이터 중 3개마다 1개씩만 저장
                // ==========================================

                // 스냅샷 생성 (새 데이터)
                SkillSnapshot newSnapshot = new SkillSnapshot
                {
                    tickAbs = currentTick,
                    level = skill.Level,
                    xpProgress = skill.XpProgressPercent
                };

                if (layers.layer0.Count < Layer0RecordCount)
                {
                    // 1️⃣ layer0이 30개 미만: 그냥 추가
                    layers.layer0.Add(newSnapshot);
                }
                else
                {
                    // 2️⃣ layer0이 30개 이상: FIFO 작동

                    // 2-1. 제거 카운트 증가
                    layers.removedCount++;

                    // 2-2. 3개마다 1개씩 layer1에 저장 (1번째, 4번째, 7번째...)
                    if (layers.removedCount % 3 == 1)
                    {
                        // layer0[0]을 layer1로 이동
                        layers.layer1.Add(layers.layer0[0]);

#if DEBUG
                        if (layers.removedCount <= 20)
                        {
                            Log.Message($"[SkillGraph] {skill.def.LabelCap}: removedCount={layers.removedCount}, 저장됨");
                        }
#endif
                    }

                    // 2-3. layer0에서 가장 오래된 데이터 제거
                    layers.layer0.RemoveAt(0);

                    // 2-4. 새 데이터를 layer0에 추가
                    layers.layer0.Add(newSnapshot);
                }
            }
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
#if DEBUG
        Log.Message("[SkillGraph] TEST MODE: RecordInterval = 60 (약 1초마다)");
#endif

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
                def.inspectorTabs.Insert(0, typeof(ITab_Pawn_SkillHistory));

                if (def.inspectorTabsResolved == null)
                    def.inspectorTabsResolved = new List<InspectTabBase>();

                def.inspectorTabsResolved.RemoveAll(t => t is ITab_Pawn_SkillHistory);

                try
                {
                    InspectTabBase tabInstance = InspectTabManager.GetSharedInstance(typeof(ITab_Pawn_SkillHistory));
                    if (tabInstance != null)
                    {
                        def.inspectorTabsResolved.Insert(0, tabInstance);
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[SkillGraph] Failed to inject tab into {def.defName}: {ex}");
                }
            }
        }
        Log.Message($"[SkillGraph] Injector finished. Tab injected for {count} races.");
    }
}
}