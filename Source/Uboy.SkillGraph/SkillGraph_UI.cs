using RimWorld;
using SkillGraph;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace SkillGraph
{
    public class ITab_Pawn_SkillHistory : ITab
    {
        // 탭 크기 설정
        private static readonly Vector2 TabSize = new Vector2(500f, 400f);

        // 현재 선택된 스킬 (기본값 null)
        private SkillDef selectedSkill;

        public ITab_Pawn_SkillHistory()
        {
            this.size = TabSize;
            this.labelKey = "TabSkillHistory"; // XML의 <TabSkillHistory> 키를 참조함
            this.tutorTag = "SkillHistory";
        }

        // 탭의 이름 (라벨) 오버라이드 (언어 파일이 없을 경우를 대비한 안전장치)
        public override bool IsVisible => SelPawn != null && SelPawn.RaceProps.Humanlike;

        protected override void FillTab()
        {
            Pawn pawn = SelPawn;
            if (pawn == null) return;

            // 1. 데이터 가져오기
            SkillGraphGameComponent comp = Current.Game.GetComponent<SkillGraphGameComponent>();
            if (comp == null) return;

            PawnSkillHistory history = comp.GetHistory(pawn);

            // 2. 기본 UI 영역 설정
            Rect rect = new Rect(0f, 0f, TabSize.x, TabSize.y).ContractedBy(10f);
            Rect topBar = rect.TopPartPixels(30f);
            Rect graphRect = new Rect(rect.x, rect.y + 40f, rect.width, rect.height - 50f);

            // 3. 스킬 선택 드롭다운 (Top Bar)
            // 번역 적용: "SG_SelectSkill".Translate()
            string btnLabel = selectedSkill != null ? selectedSkill.LabelCap : "SG_SelectSkill".Translate();
            if (Widgets.ButtonText(topBar.LeftPart(0.5f), btnLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();

                // 폰이 가진 모든 스킬 목록 생성
                foreach (var skill in pawn.skills.skills)
                {
                    options.Add(new FloatMenuOption(skill.def.LabelCap, () => {
                        selectedSkill = skill.def;
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            // 데이터가 없거나 스킬이 선택되지 않았으면 안내 문구 표시
            if (history == null || selectedSkill == null || !history.skillRecords.ContainsKey(selectedSkill))
            {
                Widgets.DrawBoxSolid(graphRect, new Color(0.1f, 0.1f, 0.1f, 0.5f));
                Text.Anchor = TextAnchor.MiddleCenter;
                // 번역 적용: "SG_NoData".Translate()
                Widgets.Label(graphRect, "SG_NoData".Translate());
                Text.Anchor = TextAnchor.UpperLeft;

                // 처음 탭 열었을 때 가장 높은 레벨의 스킬 자동 선택
                if (selectedSkill == null && pawn.skills != null)
                {
                    selectedSkill = pawn.skills.skills.OrderByDescending(s => s.Level).FirstOrDefault()?.def;
                }
                return;
            }

            // 4. 그래프 그리기 로직
            DrawGraph(graphRect, history.skillRecords[selectedSkill]);
        }

        private void DrawGraph(Rect rect, List<SkillSnapshot> snapshots)
        {
            // 배경 박스
            Widgets.DrawBoxSolid(rect, new Color(0.05f, 0.05f, 0.05f));
            Widgets.DrawBox(rect);

            if (snapshots.Count < 2) return;

            // X축 범위 (시간)
            int minTick = snapshots.Min(s => s.tickAbs);
            int maxTick = Find.TickManager.TicksGame; // 현재 시간까지 표시
            float timeRange = maxTick - minTick;
            if (timeRange < 60000) timeRange = 60000; // 최소 1일 범위

            // Y축 범위 (레벨 0 ~ 20 고정)
            float maxLevel = 20f;

            // 선 그리기 준비
            Vector2? prevPoint = null;
            GUI.color = Color.green; // 그래프 색상

            foreach (var shot in snapshots)
            {
                // 좌표 변환 로직
                // X: (현재시간 - 시작시간) / 전체시간범위 * 너비
                float x = rect.x + ((shot.tickAbs - minTick) / timeRange) * rect.width;

                // Y: 박스 바닥 - ( (레벨 + 진행도) / 20 * 높이 )
                // xpProgress를 더해줌으로써 정수 레벨 사이의 부드러운 변화 표현
                float preciseLevel = shot.level + shot.xpProgress;
                float y = rect.yMax - (preciseLevel / maxLevel) * rect.height;

                Vector2 currentPoint = new Vector2(x, y);

                if (prevPoint.HasValue)
                {
                    // 점과 점 사이를 잇는 선 그리기
                    Widgets.DrawLine(prevPoint.Value, currentPoint, Color.green, 1.5f);
                }

                prevPoint = currentPoint;

                // 5. 마우스 오버 툴팁 (Interaction)
                if (Mouse.IsOver(rect) && Math.Abs(Event.current.mousePosition.x - x) < 5f)
                {
                    // 마우스가 점 근처에 있을 때 툴팁 표시
                    // 수직선 표시
                    Widgets.DrawLine(new Vector2(x, rect.y), new Vector2(x, rect.yMax), Color.white, 0.5f);

                    string dateStr = GenDate.DateFullStringAt(shot.tickAbs, Find.WorldGrid.LongLatOf(SelPawn.MapHeld?.Tile ?? 0));

                    // 번역 적용: "SG_LevelTooltip".Translate(인자1, 인자2)
                    string tooltipText = $"{dateStr}\n{"SG_LevelTooltip".Translate(shot.level, shot.xpProgress.ToString("P0"))}";

                    TooltipHandler.TipRegion(new Rect(x - 5f, rect.y, 10f, rect.height), tooltipText);
                }
            }

            GUI.color = Color.white; // 색상 초기화

            // Y축 가이드라인 (5레벨 단위)
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            for (int i = 0; i <= 20; i += 5)
            {
                float yLine = rect.yMax - (i / maxLevel) * rect.height;
                Widgets.DrawLine(new Vector2(rect.x, yLine), new Vector2(rect.xMax, yLine), new Color(0.3f, 0.3f, 0.3f), 1f);
                Widgets.Label(new Rect(rect.x + 2f, yLine - 10f, 30f, 20f), i.ToString());
            }
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }
    }
}