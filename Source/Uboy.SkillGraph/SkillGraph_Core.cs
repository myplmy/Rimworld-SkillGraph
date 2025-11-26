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

    // 특정 시점의 스킬 상태를 저장하는 클래스
    public class SkillSnapshot : IExposable
    {
        public int tickAbs;       // 기록된 절대 시간 (GenTicks.TicksGame)
        public int level;         // 스킬 레벨 (0-20)
        public float xpProgress;  // 다음 레벨까지의 경험치 비율 (0.0 ~ 1.0) - 그래프를 부드럽게 그리기 위함

        public void ExposeData()
        {
            Scribe_Values.Look(ref tickAbs, "tickAbs");
            Scribe_Values.Look(ref level, "level");
            Scribe_Values.Look(ref xpProgress, "xpProgress");
        }
    }

    // 한 폰(Pawn)의 모든 스킬 기록을 담는 컨테이너
    public class PawnSkillHistory : IExposable
    {
        // Key: SkillDef (사격, 격투 등), Value: 시간순 기록 리스트
        public Dictionary<SkillDef, List<SkillSnapshot>> skillRecords = new Dictionary<SkillDef, List<SkillSnapshot>>();

        public void ExposeData()
        {
            // Dictionary 저장 시 Key는 Def로, Value는 Deep(내부 데이터까지) 저장
            Scribe_Collections.Look(ref skillRecords, "skillRecords", LookMode.Def, LookMode.Deep);

            // 로드 후 null 방지
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (skillRecords == null) skillRecords = new Dictionary<SkillDef, List<SkillSnapshot>>();
            }
        }
    }

    // ==========================================
    // 2. 게임 컴포넌트 (GameComponent)
    // ==========================================

    // 게임 전체 데이터를 관리하고 매일 기록을 수행하는 컴포넌트
    public class SkillGraphGameComponent : GameComponent
    {
        // Pawn 객체 대신 ThingID(string)를 Key로 사용하여 데이터 안전성 확보
        private Dictionary<string, PawnSkillHistory> historyData = new Dictionary<string, PawnSkillHistory>();

        public SkillGraphGameComponent(Game game)
        {
        }

        // 데이터를 가져오는 헬퍼 메서드
        public PawnSkillHistory GetHistory(Pawn pawn)
        {
            if (pawn == null) return null;
            if (historyData.TryGetValue(pawn.ThingID, out PawnSkillHistory history))
            {
                return history;
            }
            return null;
        }

        // 데이터 저장/로드
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref historyData, "historyData", LookMode.Value, LookMode.Deep);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (historyData == null) historyData = new Dictionary<string, PawnSkillHistory>();
            }
        }

        // 매 틱마다 실행 (여기서 하루 1회 체크)
        public override void GameComponentTick()
        {
            base.GameComponentTick();

            // 60,000틱 = 1일. 하루에 한 번만 실행
            if (Find.TickManager.TicksGame % 60000 == 0)
            {
                RecordSkills();
            }
        }

        private void RecordSkills()
        {
            // 수정됨: PawnsFinder의 올바른 속성 이름 사용 (TravellingTransporters)
            // 현재 맵에 있거나 캐러밴 등에 있는 '플레이어 소속' 림들을 찾음
            var pawns = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_OfPlayerFaction;

            foreach (Pawn pawn in pawns)
            {
                // 인간형이고 스킬이 있는 존재만 기록
                if (pawn.RaceProps.Humanlike && pawn.skills != null)
                {
                    RecordPawn(pawn);
                }
            }

            // 죄수 별도 처리 (플레이어 팩션이 아니므로 따로 검색)
            // AllMaps_PrisonersOfColony는 제공해주신 리스트에 존재하므로 유지
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

                // 스냅샷 생성 및 저장
                SkillSnapshot snapshot = new SkillSnapshot
                {
                    tickAbs = currentTick,
                    level = skill.Level,
                    // XpProgressPercent는 0.0~1.0 사이 값
                    xpProgress = skill.XpProgressPercent
                };

                history.skillRecords[skill.def].Add(snapshot);
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
            // 게임 내 모든 ThingDef를 순회하며 인간형 종족에게 탭 주입
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                if (def.race != null && def.race.Humanlike)
                {
                    if (def.inspectorTabs == null)
                        def.inspectorTabs = new List<Type>();

                    // 중복 방지 후 추가
                    if (!def.inspectorTabs.Contains(typeof(ITab_Pawn_SkillHistory)))
                    {
                        def.inspectorTabs.Add(typeof(ITab_Pawn_SkillHistory));
                    }
                }
            }
        }
    }
}