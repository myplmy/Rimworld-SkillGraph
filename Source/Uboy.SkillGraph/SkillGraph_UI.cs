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

#if DEBUG
        // 테스트: 작은 범위로 설정
        private const int RecordInterval = 60;
        private enum GraphRange { Seconds30, Minutes5, Minutes30, All }
#else
        // 프로덕션
        private const int RecordInterval = 60000;
        private enum GraphRange { Days30, Days100, Days300, All }
#endif

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

        private Pawn lastSelectedPawn = null;

        // ========== [STAGE 1] 다운샘플링 캐시 ==========
        private Dictionary<SkillDef, List<SkillSnapshot>> cachedDownsampledData;
        private float cachedGraphWidth = -1;
        private bool downsampleCacheValid = false;

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
            downsampleCacheValid = false;
            cachedDownsampledData = null;
        }

        private bool IsCacheValid(GraphRange range, int currentTick)
        {
            if (!cacheValid) return false;
            if (cachedRange != range) return false;
            if (cachedCurrentTick != currentTick) return false;
            return true;
        }

        // ========== [STAGE 1] UI 다운샘플링 ==========
        /// <summary>
        /// 데이터 포인트를 화면 너비에 맞춰 다운샘플링
        /// 
        /// 원리:
        /// - 600px 너비에 3000개 포인트를 그리는 것은 낭비
        /// - 대부분 겹치므로 시각적으로는 동일하지만 1/5로 렌더링 비용 감소
        /// </summary>
        // ==========================================
        // [개선] 시간 기반 균등 선택 + 범위별 조건부 처리
        // ==========================================
        private List<SkillSnapshot> DownsampleData(List<SkillSnapshot> data, float graphWidth,
                                                  int minTick, int maxTick, int adjustedTimeRange)
        {
            if (data == null || data.Count == 0)
                return data;
            int maxPoints = (int)graphWidth;
            // 데이터가 적으면 다운샘플링 안 함
            if (data.Count <= maxPoints)
                return data;
            // ==========================================
            // 시간 기반 균등 선택 + 방안 3 (데이터 범위 감지)
            // ==========================================
            int timeRange = adjustedTimeRange;
            var downsampled = new List<SkillSnapshot>();

            // ← 추가: 데이터의 실제 범위 파악
            int dataStartTick = data[0].tickAbs;
            int dataEndTick = data[data.Count - 1].tickAbs;

            // 균등 시간 간격
            float timeInterval = (float)timeRange / maxPoints;

            for (int i = 0; i < maxPoints; i++)
            {
                // 선택할 시간 위치
                int targetTick = minTick + (int)(timeInterval * i);

                // ← 추가: 데이터 범위 밖이면 건너뛰기
                if (targetTick < dataStartTick)
                    continue;
                if (targetTick > dataEndTick)
                    break;

                // 이 시간과 가장 가까운 데이터 찾기 (이진탐색)
                int idx = FindNearestTickIndex(data, targetTick);
                if (idx >= 0 && idx < data.Count)
                {
                    SkillSnapshot candidate = data[idx];
                    // 중복 제거
                    if (downsampled.Count == 0 || downsampled[downsampled.Count - 1].tickAbs != candidate.tickAbs)
                    {
                        downsampled.Add(candidate);
                    }
                }
            }

            // 마지막 포인트 항상 추가 (현재 상태)
            if (downsampled.Count > 0 && downsampled[downsampled.Count - 1] != data[data.Count - 1])
            {
                downsampled.Add(data[data.Count - 1]);
            }

            return downsampled;
        }

        /// <summary>
        /// 모든 데이터를 다운샘플링 (캐싱 지원, 하이브리드 방식)
        /// </summary>
        private Dictionary<SkillDef, List<SkillSnapshot>> GetDownsampledData(
            Dictionary<SkillDef, List<SkillSnapshot>> filteredData, float graphWidth,
            int minTick, int maxTick, int adjustedTimeRange)
        {
            // 캐시 확인
            if (downsampleCacheValid && Mathf.Abs(cachedGraphWidth - graphWidth) < 1f)
            {
                return cachedDownsampledData;
            }

            var downsampled = new Dictionary<SkillDef, List<SkillSnapshot>>();

            // ==========================================
            // 하이브리드 방식: 범위에 따라 다르게 처리
            // ==========================================
            bool shouldDownsample = ShouldDownsampleForCurrentRange();

            foreach (var kvp in filteredData)
            {
                if (shouldDownsample)
                {
                    downsampled[kvp.Key] = DownsampleData(kvp.Value, graphWidth, minTick, maxTick, adjustedTimeRange);
                }
                else
                {
                    // 작은 범위: 다운샘플링 안 함 (모든 데이터 표시)
                    downsampled[kvp.Key] = kvp.Value;
                }
            }

            // 캐시 저장
            cachedDownsampledData = downsampled;
            cachedGraphWidth = graphWidth;
            downsampleCacheValid = true;

            return downsampled;
        }

        // ==========================================
        // 보조 메서드: 범위별 다운샘플링 여부 결정
        // ==========================================
        private bool ShouldDownsampleForCurrentRange()
        {
#if DEBUG
            switch (currentRange)
            {
                case GraphRange.Seconds30:  // 30초: 다운샘플링 X
                    return false;
                case GraphRange.Minutes5:   // 5분: 다운샘플링 X
                    return false;
                case GraphRange.Minutes30:  // 30분: 다운샘플링 O
                    return true;
                case GraphRange.All:        // 전체: 다운샘플링 O
                    return true;
                default:
                    return true;
            }
#else
    switch (currentRange)
    {
        case GraphRange.Days30:     // 30일: 다운샘플링 X
            return false;
        case GraphRange.Days100:    // 100일: 다운샘플링 X
            return false;
        case GraphRange.Days300:    // 300일: 다운샘플링 O
            return true;
        case GraphRange.All:        // 전체: 다운샘플링 O
            return true;
        default:
            return true;
    }
#endif
        }

        private Dictionary<SkillDef, List<SkillSnapshot>> GetFilteredData(
            PawnSkillHistory history, GraphRange range, int currentTick, out int minTick, out int maxTick)
        {
            minTick = currentTick;
            maxTick = currentTick;

            // 캐시 확인
            if (IsCacheValid(range, currentTick))
            {
                minTick = cachedMinTick;
                maxTick = cachedMaxTick;
                return cachedFilteredData;
            }

            // 캐시 없음 → 재계산
            int rangeTicks = GetTicksForRange(range);
            int minTickLocal = Mathf.Max(0, currentTick - rangeTicks);  // ← 음수 방지 + 로컬 변수 사용
            maxTick = currentTick;

            var filteredData = new Dictionary<SkillDef, List<SkillSnapshot>>();

            foreach (var kvp in history.skillLayers)
            {
                // [범례 토글] 비활성화된 스킬은 건너뛰기
                if (!IsSkillEnabled(kvp.Key)) continue;

                // [2계층 개선] GetAllData()로 모든 계층 데이터 합치기
                var allSnapshots = kvp.Value.GetAllData();
                var validShots = allSnapshots.Where(s => s.tickAbs >= minTickLocal).ToList();  // ← 로컬 변수 사용

                if (validShots.Count > 0)
                {
                    filteredData[kvp.Key] = validShots;
                }
            }

            minTick = minTickLocal;  // ← 마지막에 out 파라미터에 할당

            // 캐시 저장
            cachedFilteredData = filteredData;
            cachedMinTick = minTick;
            cachedMaxTick = maxTick;
            cachedRange = range;
            cachedCurrentTick = currentTick;
            cacheValid = true;
            downsampleCacheValid = false;
            cachedDownsampledData = null;

            return filteredData;
        }

        private int GetTicksForRange(GraphRange range)
        {
#if DEBUG
            // 테스트: 기록 60틱마다 (약 1초)
            // 역으로 계산하면 최대 기록 수로 판단
            switch (range)
            {
                case GraphRange.Seconds30: return 1800;   // 약 30초 (30 × 60)
                case GraphRange.Minutes5: return 18000;  // 약 5분 (300 × 60)
                case GraphRange.Minutes30: return 108000; // 약 30분 (1800 × 60)
                default: return int.MaxValue;
            }
#else
    // 프로덕션
    switch (range)
    {
        case GraphRange.Days30: return 60000 * 30;
        case GraphRange.Days100: return 60000 * 100;
        case GraphRange.Days300: return 60000 * 300;
        default: return int.MaxValue;
    }
#endif
        }

        protected override void FillTab()
        {
            Pawn pawn = SelPawn;
            if (pawn == null) return;

            // ← 추가: 정착민 변경 감지
            if (lastSelectedPawn != pawn)
            {
                InvalidateAllCaches();
                lastSelectedPawn = pawn;
            }

            SkillGraphGameComponent comp = Current.Game.GetComponent<SkillGraphGameComponent>();
            if (comp == null) return;

            PawnSkillHistory history = comp.GetHistory(pawn);

            Rect rect = new Rect(0f, 0f, TabSize.x, TabSize.y).ContractedBy(10f);

            // 상단 컨트롤
            Rect topBarRect = rect.TopPartPixels(30f);
            Rect dropdownRect = new Rect(topBarRect.x, topBarRect.y, 200f, 30f);
            TaggedString buttonLabel = selectedSkill == null ? "SG_AllSkills".Translate() : selectedSkill.LabelCap;

            if (Widgets.ButtonText(dropdownRect, buttonLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                options.Add(new FloatMenuOption("SG_AllSkills".Translate(), () => { selectedSkill = null; InvalidateCache(); }));
                foreach (var skill in pawn.skills.skills)
                {
                    options.Add(new FloatMenuOption(skill.def.LabelCap, () => { selectedSkill = skill.def; InvalidateCache(); }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            // [디버그 기능 추가] 데이터 포인트 개수 표시 (DevMode일 때만)
            if (Prefs.DevMode && history != null)
            {
                int totalPoints = history.skillLayers.Values.Sum(layers => layers.layer0.Count + layers.layer1.Count);
                Rect debugRect = new Rect(dropdownRect.xMax + 10f, topBarRect.y, 200f, 30f);
                Text.Anchor = TextAnchor.MiddleLeft;
                Text.Font = GameFont.Tiny;
                Widgets.Label(debugRect, $"Total Points: {totalPoints}");
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;
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

            if (history == null || history.skillLayers.Count == 0)
            {
                Widgets.DrawBoxSolid(baseGraphRect, new Color(0.1f, 0.1f, 0.1f, 0.5f));
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(baseGraphRect, "SG_NoData".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }

            DrawGraphWithInteraction(baseGraphRect, history, pawn);
        }

        private void InvalidateAllCaches()
        {
            cacheValid = false;
            cachedFilteredData = null;
            downsampleCacheValid = false;
            cachedDownsampledData = null;
        }

        private void DrawFilterButtons(Rect rect)
        {
            float width = rect.width / 4f;

#if DEBUG
            // 테스트용: 초/분 단위 범위
            if (Widgets.ButtonText(new Rect(rect.x, rect.y, width, rect.height), "30s"))
            { currentRange = GraphRange.Seconds30; InvalidateCache(); }
            if (Widgets.ButtonText(new Rect(rect.x + width, rect.y, width, rect.height), "5m"))
            { currentRange = GraphRange.Minutes5; InvalidateCache(); }
            if (Widgets.ButtonText(new Rect(rect.x + width * 2, rect.y, width, rect.height), "30m"))
            { currentRange = GraphRange.Minutes30; InvalidateCache(); }
            if (Widgets.ButtonText(new Rect(rect.x + width * 3, rect.y, width, rect.height), "All"))
            { currentRange = GraphRange.All; InvalidateCache(); }
#else
    // 프로덕션용: 일 단위 범위
    if (Widgets.ButtonText(new Rect(rect.x, rect.y, width, rect.height), "SG_Days30".Translate() ))
    { currentRange = GraphRange.Days30; InvalidateCache(); }
    if (Widgets.ButtonText(new Rect(rect.x + width, rect.y, width, rect.height), "SG_Days100".Translate() ))
    { currentRange = GraphRange.Days100; InvalidateCache(); }
    if (Widgets.ButtonText(new Rect(rect.x + width * 2, rect.y, width, rect.height), "SG_Days300".Translate() ))
    { currentRange = GraphRange.Days300; InvalidateCache(); }
    if (Widgets.ButtonText(new Rect(rect.x + width * 3, rect.y, width, rect.height), "SG_RangeAll".Translate() ))
    { currentRange = GraphRange.All; InvalidateCache(); }
#endif
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

                if (Mouse.IsOver(colorBoxRect))
                {
                    Widgets.DrawBox(colorBoxRect);
                    if (Event.current.type == EventType.MouseDown && Event.current.button == 1)
                    {
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

        // ========== [STAGE 2] 호버 이진탐색 ==========
        /// <summary>
        /// 이진탐색으로 가장 가까운 틱의 인덱스를 찾기
        /// </summary>
        private int FindNearestTickIndex(List<SkillSnapshot> data, int targetTick)
        {
            if (data.Count == 0)
                return -1;

            int left = 0, right = data.Count - 1;

            // 범위 밖 체크
            if (targetTick < data[left].tickAbs)
                return left;
            if (targetTick > data[right].tickAbs)
                return right;

            // 이진탐색
            while (left < right)
            {
                int mid = (left + right) / 2;
                if (data[mid].tickAbs < targetTick)
                    left = mid + 1;
                else
                    right = mid;
            }

            return left;
        }

        private void DrawGraphWithInteraction(Rect outerRect, PawnSkillHistory history, Pawn pawn)
        {
            Rect graphRect = new Rect(outerRect.x + 35f, outerRect.y + 10f, outerRect.width - 50f, outerRect.height - 35f);

            Widgets.DrawBoxSolid(graphRect, new Color(0.05f, 0.05f, 0.05f));
            Widgets.DrawBox(graphRect);

            int currentTick = Find.TickManager.TicksGame;

            // [필터링 및 캐싱]
            var filteredData = GetFilteredData(history, currentRange, currentTick,
                                               out int minTick, out int maxTick);

            if (filteredData.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(outerRect, "No data in this time range");
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }

            // [STAGE 1] 다운샘플링 적용 (하이브리드 방식)

            float timeRange = maxTick - minTick;
            if (timeRange < 60000) timeRange = 60000;
            var downsampledData = GetDownsampledData(filteredData, graphRect.width, minTick, maxTick, (int)timeRange);

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

            // [STAGE 2] 호버 인터랙션 개선
            Vector2 mousePos = Event.current.mousePosition;
            bool isHovering = Mouse.IsOver(graphRect);
            SkillDef hoveredSkill = null;
            SkillSnapshot hoveredSnapshot = null;

            // ==========================================
            // 개선: X 위치의 모든 스킬 저장
            // ==========================================
            Dictionary<SkillDef, SkillSnapshot> allSkillsAtMouseX = new Dictionary<SkillDef, SkillSnapshot>();

            if (isHovering)
            {
                // 1 X 위치의 모든 스킬 데이터 먼저 찾기
                float mouseTimeRatio = Mathf.Clamp01((mousePos.x - graphRect.x) / graphRect.width);
                int mouseTickAtX = minTick + (int)(mouseTimeRatio * timeRange);

                // X 위치에서의 픽셀 좌표 계산 (범위 판정용)
                float mousePixelX = graphRect.x + mouseTimeRatio * graphRect.width;
                const float XTolerancePixels = 15f;  // X 위치 ± 15픽셀 범위

                foreach (var kvp in downsampledData)
                {
                    if (selectedSkill != null && kvp.Key != selectedSkill) continue;

                    // X 위치에 가장 가까운 데이터 찾기
                    int nearestIdx = FindNearestTickIndex(kvp.Value, mouseTickAtX);
                    if (nearestIdx >= 0 && nearestIdx < kvp.Value.Count)
                    {
                        // 추가: X 범위 내에 있는지 확인
                        var snapshot = kvp.Value[nearestIdx];
                        float snapshotPixelX = graphRect.x + ((float)(snapshot.tickAbs - minTick) / timeRange) * graphRect.width;
                        float distX = Math.Abs(snapshotPixelX - mousePixelX);

                        if (distX <= XTolerancePixels)  // X 범위 내에만 저장
                        {
                            allSkillsAtMouseX[kvp.Key] = snapshot;
                        }
                    }
                }

                // 2 Y축이 가까운 스킬 찾기 (마커 표시용)
                float closestDist = 15f;
                foreach (var kvp in allSkillsAtMouseX)
                {
                    var shot = kvp.Value;
                    float pointX = graphRect.x + ((float)(shot.tickAbs - minTick) / timeRange) * graphRect.width;
                    float preciseLevel = shot.level + shot.xpProgress;
                    float pointY = graphRect.yMax - (preciseLevel / maxLevel) * graphRect.height;

                    // 마우스와의 거리 계산 (X, Y 모두 고려)
                    float distX = Math.Abs(mousePos.x - pointX);
                    float distY = Math.Abs(mousePos.y - pointY);
                    float dist = Mathf.Sqrt(distX * distX + distY * distY);

                    // 가장 가까운 포인트 선택
                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        hoveredSkill = kvp.Key;
                        hoveredSnapshot = shot;
                    }
                }
            }

            // 그래프 그리기 (다운샘플링된 데이터 사용)
            foreach (var kvp in downsampledData)
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

                // 경우 1️: X,Y 모두 데이터 있음
                // → 마커 표시, 해당 기술명, 레벨, 퍼센테이지만 표시
                if (hoveredSkill != null && hoveredSnapshot != null && allSkillsAtMouseX.Count > 0)
                {
                    // Y축 가까운 스킬: 마커와 팁 표시
                    float y = graphRect.yMax - ((hoveredSnapshot.level + hoveredSnapshot.xpProgress) / maxLevel) * graphRect.height;
                    Rect markerRect = new Rect(mousePos.x - 4f, y - 4f, 8f, 8f);
                    GUI.color = GetColorForSkill(hoveredSkill);
                    GUI.DrawTexture(markerRect, BaseContent.WhiteTex);
                    GUI.color = Color.white;

                    string date = GenDate.DateFullStringAt(hoveredSnapshot.tickAbs, Find.WorldGrid.LongLatOf(pawn.MapHeld?.Tile ?? 0));
                    string tip = $"{date}\n{hoveredSkill.LabelCap}: {hoveredSnapshot.level} ({hoveredSnapshot.xpProgress:P0})";

                    TooltipHandler.TipRegion(graphRect, tip);
                }
                // 경우 2️: X만 데이터 있음 (Y는 15px 범위 내에 없음)
                // → 마커 비표시, 해당 x좌표 일자의 모든 기술명, 레벨, 퍼센테이지 표시
                else if (allSkillsAtMouseX.Count > 0 && hoveredSkill == null)
                {
                    // X 위치의 모든 스킬 표시
                    float mouseTimeRatio = Mathf.Clamp01((mousePos.x - graphRect.x) / graphRect.width);
                    int mouseTick = minTick + (int)(mouseTimeRatio * timeRange);
                    string date = GenDate.DateFullStringAt(mouseTick, Find.WorldGrid.LongLatOf(pawn.MapHeld?.Tile ?? 0));
                    string tip = date + "\n";

                    var sortedSkills = allSkillsAtMouseX.OrderByDescending(x => x.Value.level + x.Value.xpProgress).ToList();
                    foreach (var kvp in sortedSkills)
                    {
                        tip += $"\n{kvp.Key.LabelCap}: {kvp.Value.level} ({kvp.Value.xpProgress:P0})";
                    }

                    TooltipHandler.TipRegion(graphRect, tip);
                }
                // 경우 3️: Y만 데이터 있음 (X에는 없음)
                // → 마커 비표시, 일자 표시, 기술명 및 레벨 등 비표시
                else if (hoveredSkill != null && allSkillsAtMouseX.Count == 0)
                {
                    // 일자만 표시
                    float mouseTimeRatio = Mathf.Clamp01((mousePos.x - graphRect.x) / graphRect.width);
                    int mouseTick = minTick + (int)(mouseTimeRatio * timeRange);
                    string date = GenDate.DateFullStringAt(mouseTick, Find.WorldGrid.LongLatOf(pawn.MapHeld?.Tile ?? 0));

                    TooltipHandler.TipRegion(graphRect, date);
                }
                // 경우 4️: X,Y 모두 데이터 없음
                // → 마커 비표시, 일자 표시, 기술명 및 레벨 등 비표시
                else
                {
                    // 일자만 표시
                    float mouseTimeRatio = Mathf.Clamp01((mousePos.x - graphRect.x) / graphRect.width);
                    int mouseTick = minTick + (int)(mouseTimeRatio * timeRange);
                    string date = GenDate.DateFullStringAt(mouseTick, Find.WorldGrid.LongLatOf(pawn.MapHeld?.Tile ?? 0));

                    TooltipHandler.TipRegion(graphRect, date);
                }
            }
        }

        private void DrawXAxisGridAndLabels(Rect graphRect, int minTick, int maxTick, Pawn pawn)
        {
            float timeRange = maxTick - minTick;
            int tickInterval;

#if DEBUG
            // 테스트: 더 짧은 간격
            switch (currentRange)
            {
                case GraphRange.Seconds30: tickInterval = 300; break;    // 5초마다
                case GraphRange.Minutes5: tickInterval = 3000; break;    // 50초마다
                case GraphRange.Minutes30: tickInterval = 18000; break;  // 약 5분마다
                case GraphRange.All:
                default:
                    float targetLabels = 8f;
                    float rawInterval = timeRange / targetLabels;
                    int tickIntervalSecs = Mathf.Max(100, Mathf.RoundToInt(rawInterval / 60f));
                    // if (tickIntervalSecs > 600) tickIntervalSecs = 600;  ← 제한 제거 (범위 넓어지면 간격도 자동 조정)
                    tickInterval = tickIntervalSecs * 60;
                    break;
            }
#else
    // 프로덕션
    switch (currentRange)
    {
        case GraphRange.Days30: tickInterval = 60000 * 6; break;      // 6일마다
        case GraphRange.Days100: tickInterval = 60000 * 20; break;    // 20일마다
        case GraphRange.Days300: tickInterval = 60000 * 60; break;    // 60일마다
        case GraphRange.All:
        default:
            float targetLabels = 8f;
            float rawInterval = timeRange / targetLabels;
            int days = Mathf.Max(1, Mathf.RoundToInt(rawInterval / 60000f));
            if (days > 60) days = 60;
            tickInterval = days * 60000;
            break;
    }
#endif

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