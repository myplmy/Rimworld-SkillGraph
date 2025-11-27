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

        // ========== [개선 1] 범례 토글 상태 관리 ==========
        private HashSet<SkillDef> disabledSkills = new HashSet<SkillDef>();

        // ========== [개선 2] 색상 커스터마이징 저장소 ==========
        private Dictionary<SkillDef, Color> customSkillColors = new Dictionary<SkillDef, Color>();

        // ========== [개선 3] 렌더링 캐시 ==========
        private Dictionary<SkillDef, List<SkillSnapshot>> cachedFilteredData;
        private int cachedMinTick = -1;
        private int cachedMaxTick = -1;
        private GraphRange cachedRange = GraphRange.All;
        private bool cacheValid = false;
        private int cachedCurrentTick = -1;

        // 캐시 유효 시간 (틱 단위, 60틱 = 약 1초)
        // 매 프레임 계산하는 것을 방지합니다.
        private const int CacheDuration = 250;

        public ITab_Pawn_SkillHistory()
        {
            this.size = TabSize;
            this.labelKey = "TabSkillHistory";
            this.tutorTag = "SkillHistory";
        }

        public override bool IsVisible => SelPawn != null && SelPawn.RaceProps.Humanlike;

        // ========== [색상 개선 1] 동적 색상 생성 (HSL 기반) ==========
        private Color GenerateColorForSkill(SkillDef skill)
        {
            if (customSkillColors.TryGetValue(skill, out Color customColor))
            {
                return customColor;
            }

            int hash = Math.Abs(skill.shortHash);
            float hue = (hash % 360) / 360f;
            float saturation = 0.7f + ((hash / 1000f) % 0.3f);
            float lightness = 0.5f + ((hash / 10000f) % 0.15f);

            return HSLToRGB(hue, saturation, lightness);
        }

        private Color HSLToRGB(float h, float s, float l)
        {
            float c = (1 - Mathf.Abs(2 * l - 1)) * s;
            float x = c * (1 - Mathf.Abs((h * 6) % 2 - 1));
            float m = l - c / 2;

            float r = 0, g = 0, b = 0;
            if (h < 1f / 6f) { r = c; g = x; b = 0; }
            else if (h < 2f / 6f) { r = x; g = c; b = 0; }
            else if (h < 3f / 6f) { r = 0; g = c; b = x; }
            else if (h < 4f / 6f) { r = 0; g = x; b = c; }
            else if (h < 5f / 6f) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            return new Color(Mathf.Clamp01(r + m), Mathf.Clamp01(g + m), Mathf.Clamp01(b + m), 1f);
        }

        public void SetCustomColorForSkill(SkillDef skill, Color color)
        {
            customSkillColors[skill] = color;
            InvalidateCache();
        }

        public void ResetCustomColorForSkill(SkillDef skill)
        {
            customSkillColors.Remove(skill);
            InvalidateCache();
        }

        private Color GetColorForSkill(SkillDef skill, float alphaMult = 1f)
        {
            Color baseColor = GenerateColorForSkill(skill);
            baseColor.a *= alphaMult;
            return baseColor;
        }

        // ========== [범례 토글] ==========
        private void ToggleSkillEnabled(SkillDef skill)
        {
            if (disabledSkills.Contains(skill))
            {
                disabledSkills.Remove(skill);
            }
            else
            {
                disabledSkills.Add(skill);
            }
            InvalidateCache();
        }

        private bool IsSkillEnabled(SkillDef skill)
        {
            return !disabledSkills.Contains(skill);
        }

        // ========== [렌더링 캐싱] ==========
        private void InvalidateCache()
        {
            cacheValid = false;
            cachedFilteredData = null;
        }

        private bool IsCacheValid(GraphRange range, int currentTick)
        {
            if (!cacheValid) return false;
            if (cachedRange != range) return false;

            // [수정됨] 매 틱마다 무효화되지 않도록 간격(CacheDuration)을 둠
            if (currentTick - cachedCurrentTick > CacheDuration) return false;

            return true;
        }

        private Dictionary<SkillDef, List<SkillSnapshot>> GetFilteredData(
            PawnSkillHistory history, GraphRange range, int currentTick, out int minTick, out int maxTick)
        {
            // 캐시 확인
            if (IsCacheValid(range, currentTick))
            {
                minTick = cachedMinTick;
                maxTick = cachedMaxTick;
                return cachedFilteredData;
            }

            // 캐시 재생성
            minTick = currentTick;
            maxTick = currentTick;

            int rangeTicks = GetTicksForRange(range);
            int minTickCutoff = currentTick - rangeTicks;
            minTick = int.MaxValue;
            maxTick = currentTick;

            var filteredData = new Dictionary<SkillDef, List<SkillSnapshot>>();

            foreach (var kvp in history.skillRecords)
            {
                if (!IsSkillEnabled(kvp.Key)) continue;

                var validShots = kvp.Value.Where(s => s.tickAbs >= minTickCutoff).ToList();
                if (validShots.Count > 0)
                {
                    filteredData[kvp.Key] = validShots;
                    if (validShots[0].tickAbs < minTick) minTick = validShots[0].tickAbs;
                }
            }

            if (minTick == int.MaxValue) minTick = maxTick - 60000;

            // 캐시 저장
            cachedFilteredData = filteredData;
            cachedMinTick = minTick;
            cachedMaxTick = maxTick;
            cachedRange = range;
            cachedCurrentTick = currentTick;
            cacheValid = true;

            return filteredData;
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

            // 상단 컨트롤 (드롭다운)
            Rect topBarRect = rect.TopPartPixels(30f);
            Rect dropdownRect = new Rect(topBarRect.x, topBarRect.y, 200f, 30f);
            string buttonLabel = selectedSkill == null ? "All Skills" : selectedSkill.LabelCap.ToString();

            if (Widgets.ButtonText(dropdownRect, buttonLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                options.Add(new FloatMenuOption("All Skills", () => { selectedSkill = null; InvalidateCache(); })); // 변경 시 캐시 초기화
                foreach (var skill in pawn.skills.skills)
                {
                    options.Add(new FloatMenuOption(skill.def.LabelCap, () => { selectedSkill = skill.def; InvalidateCache(); }));
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
                DrawLegendWithToggle(legendRect, pawn);
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
            // 버튼 클릭 시 InvalidateCache 호출
            if (Widgets.ButtonText(new Rect(rect.x, rect.y, width, rect.height), "Last 30 Days"))
            { currentRange = GraphRange.Days30; InvalidateCache(); }
            if (Widgets.ButtonText(new Rect(rect.x + width, rect.y, width, rect.height), "Last 100 Days"))
            { currentRange = GraphRange.Days100; InvalidateCache(); }
            if (Widgets.ButtonText(new Rect(rect.x + width * 2, rect.y, width, rect.height), "Last 300 Days"))
            { currentRange = GraphRange.Days300; InvalidateCache(); }
            if (Widgets.ButtonText(new Rect(rect.x + width * 3, rect.y, width, rect.height), "All"))
            { currentRange = GraphRange.All; InvalidateCache(); }
        }

        private void DrawLegendWithToggle(Rect rect, Pawn pawn)
        {
            var skills = pawn.skills.skills.OrderByDescending(s => s.Level).ToList();
            float x = rect.x;
            float y = rect.y;
            float itemWidth = 110f;
            float itemHeight = 20f;

            foreach (var skill in skills)
            {
                if (y + itemHeight > rect.yMax) break;

                bool isEnabled = IsSkillEnabled(skill.def);
                Color c = GetColorForSkill(skill.def, isEnabled ? 1f : 0.3f);

                Rect colorBoxRect = new Rect(x, y + 4f, 12f, 12f);
                Widgets.DrawBoxSolid(colorBoxRect, c);

                // [수정됨] OnGUI 호환성: Event.current 사용
                if (Mouse.IsOver(colorBoxRect))
                {
                    Widgets.DrawBox(colorBoxRect);
                    if (Event.current.type == EventType.MouseDown && Event.current.button == 1)
                    {
                        // 우클릭 이벤트 (현재는 동작 없음)
                        Event.current.Use();
                    }
                }

                Rect labelRect = new Rect(x + 16f, y, itemWidth - 16f, itemHeight);
                Text.Font = GameFont.Tiny;
                Widgets.Label(labelRect, $"{skill.def.LabelCap} ({skill.Level})");
                Text.Font = GameFont.Small;

                if (Widgets.ButtonInvisible(new Rect(x, y, itemWidth, itemHeight)))
                {
                    ToggleSkillEnabled(skill.def);
                }

                if (!isEnabled)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.5f);
                    Widgets.DrawLine(new Vector2(x, y + itemHeight / 2),
                                     new Vector2(x + itemWidth, y + itemHeight / 2),
                                     Color.gray, 1f);
                    GUI.color = Color.white;
                }

                x += itemWidth;
                if (x + itemWidth > rect.xMax) { x = rect.x; y += itemHeight; }
            }
        }

        private void DrawGraphWithInteraction(Rect outerRect, PawnSkillHistory history, Pawn pawn)
        {
            Rect graphRect = new Rect(outerRect.x + 35f, outerRect.y + 10f, outerRect.width - 50f, outerRect.height - 35f);

            Widgets.DrawBoxSolid(graphRect, new Color(0.05f, 0.05f, 0.05f));
            Widgets.DrawBox(graphRect);

            int currentTick = Find.TickManager.TicksGame;

            // [캐싱 적용]
            var filteredData = GetFilteredData(history, currentRange, currentTick,
                                               out int minTick, out int maxTick);

            if (filteredData.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(outerRect, "No data in this time range");
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }

            float timeRange = maxTick - minTick;
            if (timeRange < 60000) timeRange = 60000;

            float maxLevel = 20f;

            DrawXAxisGridAndLabels(graphRect, minTick, maxTick, pawn);

            // Y축
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

            // 호버 인터랙션
            Vector2 mousePos = Event.current.mousePosition;
            bool isHovering = Mouse.IsOver(graphRect);
            SkillDef hoveredSkill = null;
            SkillSnapshot hoveredSnapshot = null;

            if (isHovering)
            {
                float mouseTimeRatio = Mathf.Clamp01((mousePos.x - graphRect.x) / graphRect.width);
                int mouseTick = minTick + (int)(mouseTimeRatio * timeRange);

                float closestDist = 15f;
                foreach (var kvp in filteredData)
                {
                    // 선택된 단일 스킬이 있다면 그것만 체크
                    if (selectedSkill != null && kvp.Key != selectedSkill) continue;

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

            // 그래프 그리기
            foreach (var kvp in filteredData)
            {
                if (selectedSkill != null && kvp.Key != selectedSkill) continue;

                SkillDef skillDef = kvp.Key;
                List<SkillSnapshot> snapshots = kvp.Value;
                Color skillColor = GetColorForSkill(skillDef);

                // 하이라이트 처리
                if (hoveredSkill != null && skillDef != hoveredSkill)
                    skillColor.a = 0.15f;
                else if (hoveredSkill != null && skillDef == hoveredSkill)
                    skillColor.a = 1.0f;

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
                    // 전체 요약 툴팁
                    float mouseTimeRatio = Mathf.Clamp01((mousePos.x - graphRect.x) / graphRect.width);
                    int mouseTick = minTick + (int)(mouseTimeRatio * timeRange);

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
                case GraphRange.Days30: tickInterval = 60000 * 3; break;
                case GraphRange.Days100: tickInterval = 60000 * 10; break;
                case GraphRange.Days300: tickInterval = 60000 * 15; break;
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

            int startTickAligned = (minTick / tickInterval) * tickInterval;
            if (startTickAligned < minTick) startTickAligned += tickInterval;

            for (int tick = startTickAligned; tick <= maxTick; tick += tickInterval)
            {
                float x = graphRect.x + ((float)(tick - minTick) / timeRange) * graphRect.width;
                Widgets.DrawLine(new Vector2(x, graphRect.y), new Vector2(x, graphRect.yMax), new Color(0.3f, 0.3f, 0.3f), 0.3f);

                string dateStr = GetShortDateString(tick, pawn);
                Rect labelRect = new Rect(x - 30f, graphRect.yMax + 2f, 60f, 20f);

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
    }
}