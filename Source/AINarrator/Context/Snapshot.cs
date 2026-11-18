using System.Collections.Generic;

namespace Stellaris.AI
{
    /// <summary>单个殖民者的最小可见信息。只放对"该不该出事"有用的字段。</summary>
    public class PawnBrief
    {
        public string Name;
        public string Kind;
        public int Age;
        public string Mood;      // 粗粒度：低 / 中 / 高
        public bool Downed;
        public string TopSkill;
    }

    /// <summary>
    /// 一次导演请求的只读快照。
    /// 硬约束：字段少、字符串短、列表有上限，序列化后 ≤ 6KB。
    /// 绝不包含 Map / Pawn 对象引用，只含纯数据。
    /// </summary>
    public class Snapshot
    {
        public long Tick;
        public int Day;
        public string MapName = "";

        public string Biome = "";
        public string Weather = "";
        public float OutdoorTemp = 0f;

        public int Colonists = 0;
        public int Downed = 0;
        public int MentalBreak = 0;
        public float Wealth;
        public float ThreatPoints;
        public float FoodNutrition;
        public float AvgMood = -1f;

        public List<PawnBrief> Pawns = new List<PawnBrief>();

        public string LastEvent = "";
        public string RecentSummary = "";
    }
}
