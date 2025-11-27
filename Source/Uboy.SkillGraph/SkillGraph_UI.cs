using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace SkillGraph
{
    public class ITab_Pawn_SkillHistory : ITab
    {
        private static readonly Vector2 TabSize = new Vector2(600f, 520f);

        private enum GraphRange { Days30, Days100, Days300, All }
        private GraphRange currentRange = GraphRange.All;

        private SkillDef selectedSkill = null;

        public ITab_Pawn_SkillHistory()
        {
            this.size = TabSize;
            this.labelKey = "TabSkillHistory";
            this.tutorTag = "SkillHistory";
        }

        public override bool IsVisible => SelPawn != null && SelPawn.RaceProps.Humanlike;

        private static readonly Color[] SkillColors = new Color[]
        {
            Color.red, Color.green, Color.blue, Color.yellow,
            Color.cyan, Color.magenta, new Color(1f, 0.5f, 0f),
            new Color(0.5f, 0f, 0.5f), new Color(0f, 0.5f, 0.5f),
            new Color(0.5f, 1f, 0.5f), new Color(1f, 0.5f, 0.5f),
            Color.white
        };

        private Color GetColorForSkill(SkillDef skill)
        {
            int index = Math.Abs(skill.shortHash) % SkillColors.Length;
            return SkillColors[index];
        }

        private int GetTicksForRange(GraphRange range)
        {
            switch (range)
            {
                case GraphRange.Days30: return 60000 * 30;
                case GraphRange.Days100: return 60000 * 100;
                case GraphRange.Days300: return 60000 * 300;
                default: return int.MaxValue;
            }
        }

        protected override void FillTab()
        {
            Pawn pawn = SelPawn;
            if (pawn == null) return;

            SkillGraphGameComponent comp = Current.Game.GetComponent<SkillGraphGameComponent>();
            if (comp == null) return;

            PawnSkillHistory history = comp.GetHistory(pawn);

            Rect rect = new Rect(0f, 0f, TabSize.x, TabSize.y).ContractedBy(10f);

            // 상단 컨트롤
            Rect topBarRect = rect.TopPartPixels(30f);
            Rect dropdownRect = new Rect(topBarRect.x, topBarRect.y, 200f, 30f);
            string buttonLabel = selectedSkill == null ? "All Skills" : selectedSkill.LabelCap.ToString();

            if (Widgets.ButtonText(dropdownRect, buttonLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                options.Add(new FloatMenuOption("All Skills", () => selectedSkill = null));
                foreach (var skill in pawn.skills.skills)
                {
                    options.Add(new FloatMenuOption(skill.def.LabelCap, () => selectedSkill = skill.def));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            // 하단 필터 버튼
            Rect bottomBarRect = rect.BottomPartPixels(30f);
            DrawFilterButtons(bottomBarRect);

            // 그래프 영역 계산
            Rect baseGraphRect;
            if (selectedSkill == null)
            {
                Rect legendRect = new Rect(rect.x, rect.y + 40f, rect.width, 60f);
                DrawLegend(legendRect, pawn);
                baseGraphRect = new Rect(rect.x, rect.y + 110f, rect.width, rect.height - 150f);
            }
            else
            {
                baseGraphRect = new Rect(rect.x, rect.y + 40f, rect.width, rect.height - 80f);
            }

            if (history == null || history.skillRecords.Count == 0)
            {
                Widgets.DrawBoxSolid(baseGraphRect, new Color(0.1f, 0.1f, 0.1f, 0.5f));
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(baseGraphRect, "SG_NoData".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }

            DrawGraphWithInteraction(baseGraphRect, history, pawn);
        }

        private void DrawFilterButtons(Rect rect)
        {
            float width = rect.width / 4f;
            if (Widgets.ButtonText(new Rect(rect.x, rect.y, width, rect.height), "Last 30 Days")) currentRange = GraphRange.Days30;
            if (Widgets.ButtonText(new Rect(rect.x + width, rect.y, width, rect.height), "Last 100 Days")) currentRange = GraphRange.Days100;
            if (Widgets.ButtonText(new Rect(rect.x + width * 2, rect.y, width, rect.height), "Last 300 Days")) currentRange = GraphRange.Days300;
            if (Widgets.ButtonText(new Rect(rect.x + width * 3, rect.y, width, rect.height), "All")) currentRange = GraphRange.All;
        }

        private void DrawGraphWithInteraction(Rect outerRect, PawnSkillHistory history, Pawn pawn)
        {
            Rect graphRect = new Rect(outerRect.x + 35f, outerRect.y + 10f, outerRect.width - 50f, outerRect.height - 35f);

            Widgets.DrawBoxSolid(graphRect, new Color(0.05f, 0.05f, 0.05f));
            Widgets.DrawBox(graphRect);

            int currentTick = Find.TickManager.TicksGame;
            int rangeTicks = GetTicksForRange(currentRange);
            int minTickCutoff = currentTick - rangeTicks;

            int minTick = int.MaxValue;
            int maxTick = currentTick;
            bool hasData = false;

            Dictionary<SkillDef, List<SkillSnapshot>> filteredData = new Dictionary<SkillDef, List<SkillSnapshot>>();

            foreach (var kvp in history.skillRecords)
            {
                if (selectedSkill != null && kvp.Key != selectedSkill) continue;

                var validShots = kvp.Value.Where(s => s.tickAbs >= minTickCutoff).ToList();
                if (validShots.Count > 0)
                {
                    filteredData[kvp.Key] = validShots;
                    if (validShots[0].tickAbs < minTick) minTick = validShots[0].tickAbs;
                    hasData = true;
                }
            }

            if (!hasData)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(outerRect, "No data in this time range");
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }

            if (minTick == int.MaxValue) minTick = maxTick - 60000;
            float timeRange = maxTick - minTick;
            if (timeRange < 60000) timeRange = 60000;

            float maxLevel = 20f;

            // X축 그리드 (수정된 로직 사용)
            DrawXAxisGridAndLabels(graphRect, minTick, maxTick, pawn);

            // Y축 라벨
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = Color.gray;
            for (int i = 0; i <= 20; i += 5)
            {
                float yLine = graphRect.yMax - (i / 20f) * graphRect.height;
                Widgets.DrawLine(new Vector2(graphRect.x, yLine), new Vector2(graphRect.xMax, yLine), new Color(0.3f, 0.3f, 0.3f), 0.5f);
                Widgets.Label(new Rect(outerRect.x, yLine - 10f, 30f, 20f), i.ToString());
            }
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            // 마우스 인터랙션
            Vector2 mousePos = Event.current.mousePosition;
            bool isHovering = Mouse.IsOver(graphRect);
            SkillDef hoveredSkill = null;
            SkillSnapshot hoveredSnapshot = null;

            float mouseTimeRatio = Mathf.Clamp01((mousePos.x - graphRect.x) / graphRect.width);
            int mouseTick = minTick + (int)(mouseTimeRatio * timeRange);

            if (isHovering)
            {
                float closestDist = 15f;
                foreach (var kvp in filteredData)
                {
                    var closestShot = kvp.Value.OrderBy(s => Math.Abs(s.tickAbs - mouseTick)).First();
                    float preciseLevel = closestShot.level + closestShot.xpProgress;
                    float y = graphRect.yMax - (preciseLevel / maxLevel) * graphRect.height;

                    float dist = Math.Abs(mousePos.y - y);
                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        hoveredSkill = kvp.Key;
                        hoveredSnapshot = closestShot;
                    }
                }
            }

            foreach (var kvp in filteredData)
            {
                SkillDef skillDef = kvp.Key;
                List<SkillSnapshot> snapshots = kvp.Value;
                Color skillColor = GetColorForSkill(skillDef);

                if (hoveredSkill != null && skillDef != hoveredSkill) skillColor.a = 0.15f;
                else if (hoveredSkill != null && skillDef == hoveredSkill) skillColor.a = 1.0f;

                GUI.color = skillColor;
                Vector2? prevPoint = null;

                foreach (var shot in snapshots)
                {
                    float x = graphRect.x + ((shot.tickAbs - minTick) / timeRange) * graphRect.width;
                    float preciseLevel = shot.level + shot.xpProgress;
                    float y = graphRect.yMax - (preciseLevel / maxLevel) * graphRect.height;
                    Vector2 currentPoint = new Vector2(x, y);

                    if (x < graphRect.x || x > graphRect.xMax)
                    {
                        prevPoint = null;
                        continue;
                    }

                    if (snapshots.Count == 1 && x >= graphRect.x && x <= graphRect.xMax)
                        Widgets.DrawBoxSolid(new Rect(x - 2f, y - 2f, 4f, 4f), skillColor);

                    if (prevPoint.HasValue)
                        Widgets.DrawLine(prevPoint.Value, currentPoint, skillColor, hoveredSkill == skillDef ? 2.5f : 1.2f);

                    prevPoint = currentPoint;
                }
            }
            GUI.color = Color.white;

            if (isHovering)
            {
                Widgets.DrawLine(new Vector2(mousePos.x, graphRect.y), new Vector2(mousePos.x, graphRect.yMax), Color.white, 0.5f);

                if (hoveredSkill != null && hoveredSnapshot != null)
                {
                    float y = graphRect.yMax - ((hoveredSnapshot.level + hoveredSnapshot.xpProgress) / maxLevel) * graphRect.height;
                    Rect markerRect = new Rect(mousePos.x - 4f, y - 4f, 8f, 8f);
                    GUI.color = GetColorForSkill(hoveredSkill);
                    GUI.DrawTexture(markerRect, BaseContent.WhiteTex);
                    GUI.color = Color.white;

                    string date = GenDate.DateFullStringAt(hoveredSnapshot.tickAbs, Find.WorldGrid.LongLatOf(pawn.MapHeld?.Tile ?? 0));
                    string tip = $"{date}\n{hoveredSkill.LabelCap}: {hoveredSnapshot.level} ({hoveredSnapshot.xpProgress:P0})";
                    TooltipHandler.TipRegion(graphRect, tip);
                }
                else
                {
                    string date = GenDate.DateFullStringAt(mouseTick, Find.WorldGrid.LongLatOf(pawn.MapHeld?.Tile ?? 0));
                    string tip = date + "\n";
                    var activeSkills = filteredData.Select(kvp =>
                    {
                        var shot = kvp.Value.OrderBy(s => Math.Abs(s.tickAbs - mouseTick)).First();
                        return new { Skill = kvp.Key, Shot = shot };
                    })
                    .OrderByDescending(x => x.Shot.level + x.Shot.xpProgress)
                    .Take(10);

                    foreach (var item in activeSkills)
                    {
                        tip += $"\n{item.Skill.LabelCap}: {item.Shot.level}";
                    }
                    TooltipHandler.TipRegion(graphRect, tip);
                }
            }
        }

        private void DrawXAxisGridAndLabels(Rect graphRect, int minTick, int maxTick, Pawn pawn)
        {
            float timeRange = maxTick - minTick;
            int tickInterval;

            switch (currentRange)
            {
                case GraphRange.Days30:
                    tickInterval = 60000 * 3; // 3일
                    break;
                case GraphRange.Days100:
                    tickInterval = 60000 * 10; // 10일
                    break;
                case GraphRange.Days300:
                    tickInterval = 60000 * 15; // 15일 (1분기)
                    break;
                case GraphRange.All:
                default:
                    float targetLabels = 8f;
                    float rawInterval = timeRange / targetLabels;
                    int days = Mathf.Max(1, Mathf.RoundToInt(rawInterval / 60000f));
                    if (days > 60) days = 60;
                    tickInterval = days * 60000;
                    break;
            }

            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = Color.gray;

            // [수정된 로직] 눈금 시작점을 '절대 시간' 기준으로 정렬 (Alignment)
            // minTick보다 크거나 같은 첫 번째 '정각' 틱을 찾음
            int startTickAligned = (minTick / tickInterval) * tickInterval;
            if (startTickAligned < minTick) startTickAligned += tickInterval;

            for (int tick = startTickAligned; tick <= maxTick; tick += tickInterval)
            {
                // 그래프 상 위치 계산
                float x = graphRect.x + ((float)(tick - minTick) / timeRange) * graphRect.width;

                // 세로 그리드 선
                Widgets.DrawLine(new Vector2(x, graphRect.y), new Vector2(x, graphRect.yMax), new Color(0.3f, 0.3f, 0.3f), 0.3f);

                // 라벨 표시
                string dateStr = GetShortDateString(tick, pawn);
                Rect labelRect = new Rect(x - 30f, graphRect.yMax + 2f, 60f, 20f);

                // 라벨이 그래프 영역을 크게 벗어나지 않을 때만 표시
                if (labelRect.xMax <= graphRect.xMax + 15f && labelRect.x >= graphRect.x - 15f)
                {
                    Widgets.Label(labelRect, dateStr);
                }
            }

            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
        }

        private string GetShortDateString(int tick, Pawn pawn)
        {
            int absTicks = tick;
            int ticksPerDay = 60000;
            int ticksPerYear = 60000 * 60;

            int year = absTicks / ticksPerYear;
            int dayOfYear = (absTicks % ticksPerYear) / ticksPerDay;

            return $"Y{year + 5500} D{dayOfYear + 1}";
        }

        private void DrawLegend(Rect rect, Pawn pawn)
        {
            var skills = pawn.skills.skills.OrderByDescending(s => s.Level).ToList();
            float x = rect.x;
            float y = rect.y;
            float itemWidth = 110f;
            float itemHeight = 20f;

            foreach (var skill in skills)
            {
                if (y + itemHeight > rect.yMax) break;
                Color c = GetColorForSkill(skill.def);
                Widgets.DrawBoxSolid(new Rect(x, y + 4f, 12f, 12f), c);

                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(x + 16f, y, itemWidth - 16f, itemHeight), $"{skill.def.LabelCap} ({skill.Level})");
                Text.Font = GameFont.Small;

                x += itemWidth;
                if (x + itemWidth > rect.xMax) { x = rect.x; y += itemHeight; }
            }
        }
    }
}