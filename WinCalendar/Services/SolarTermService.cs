using System;
using System.Collections.Generic;

namespace WinCalendar.Services
{
    /// <summary>
    /// 二十四节气计算（纯算法，不需要任何外部数据）。
    ///
    /// 📖 原理：节气 = **太阳视黄经每跨过 15° 整倍数**的那个时刻。
    ///     春分 0°、清明 15°、谷雨 30° …… 秋分 180°、冬至 270°、小寒 285°、大寒 300°。
    ///     所以只要能算出任意时刻的太阳视黄经，就能反推出每个节气的日期。
    ///
    /// 🛠 实现三步：
    ///     ① 用 Meeus《Astronomical Algorithms》第 25 章的低精度太阳位置公式求视黄经（误差 &lt; 0.01°）；
    ///     ② 在目标月份里逐日扫描，找"黄经差由负转正"的那一天 —— 那天就是节气日；
    ///     ③ 按年缓存，全年 24 个节气只算一次。
    ///
    /// 🎯 精度：节气**时刻**误差约 ±15 分钟，判断"哪一天"完全够用
    ///     （只有恰好卡在午夜零点前后才可能差一天，属于可忽略边界）。
    ///
    /// ❓ 为什么不用网上流传最广的那个公式？
    ///     `525948.76 分钟 × (year - 1900)` 那种写法是拿 1900 年做基准**线性外推**的，
    ///     几十年后累积误差就能到一天 —— 算 2020 年之后的节气已经不可靠了。
    /// </summary>
    public static class SolarTermService
    {
        /// <summary>24 个节气名，按公历月份顺序排列（1 月小寒、大寒 → 12 月大雪、冬至）</summary>
        public static readonly string[] Names =
        {
            "小寒", "大寒",   // 1 月
            "立春", "雨水",   // 2 月
            "惊蛰", "春分",   // 3 月
            "清明", "谷雨",   // 4 月
            "立夏", "小满",   // 5 月
            "芒种", "夏至",   // 6 月
            "小暑", "大暑",   // 7 月
            "立秋", "处暑",   // 8 月
            "白露", "秋分",   // 9 月
            "寒露", "霜降",   // 10 月
            "立冬", "小雪",   // 11 月
            "大雪", "冬至"    // 12 月
        };

        private static readonly Dictionary<int, Dictionary<DateTime, string>> Cache = new();

        /// <summary>取某一年的全部节气（日期 → 节气名）。结果按年缓存，重复调用是 O(1)。</summary>
        public static Dictionary<DateTime, string> GetYear(int year)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(year, out var cached)) return cached;

                var map = new Dictionary<DateTime, string>();
                for (int i = 0; i < 24; i++)
                {
                    // 第 i 个节气属于公历 (i / 2 + 1) 月
                    DateTime? date = FindDate(year, i / 2 + 1, i);
                    if (date.HasValue) map[date.Value] = Names[i];
                }

                Cache[year] = map;
                return map;
            }
        }

        /// <summary>某天是节气就返回节气名，否则返回 null</summary>
        public static string? GetName(DateTime date)
            => GetYear(date.Year).TryGetValue(date.Date, out var name) ? name : null;

        // ================== 内部实现 ==================

        /// <summary>在指定公历月份里找出第 index 个节气落在哪一天</summary>
        private static DateTime? FindDate(int year, int month, int index)
        {
            // 小寒 = 285°，之后每个节气 +15°（模 360）
            double target = (285.0 + 15.0 * index) % 360.0;

            int days = DateTime.DaysInMonth(year, month);
            DateTime cursor = new DateTime(year, month, 1);

            // 逐日比较"本地零点时刻"的黄经差：
            // 差由负转正的那一天，就是节气所在的那一天
            double prev = SignedDiff(cursor, target);
            for (int i = 1; i <= days; i++)
            {
                double cur = SignedDiff(cursor.AddDays(i), target);
                if (prev < 0 && cur >= 0)
                    return cursor.AddDays(i - 1);

                prev = cur;
            }

            return null;
        }

        /// <summary>太阳视黄经 - 目标黄经，归一化到 [-180, 180)，方便做"由负转正"判断</summary>
        private static double SignedDiff(DateTime localMidnight, double target)
        {
            double lon = SunEclipticLongitude(localMidnight.ToUniversalTime());
            double diff = lon - target;

            while (diff < -180) diff += 360;
            while (diff >= 180) diff -= 360;
            return diff;
        }

        /// <summary>
        /// 太阳视黄经（度，0~360）。
        /// Meeus《Astronomical Algorithms》第 25 章低精度公式：
        /// 先算几何平黄经与平近点角，再加中心差得到真黄经，最后修正章动与光行差得到视黄经。
        /// </summary>
        private static double SunEclipticLongitude(DateTime utc)
        {
            double jd = ToJulianDay(utc);
            double t = (jd - 2451545.0) / 36525.0;   // 自 J2000.0 起的儒略世纪数

            double meanLongitude = 280.46646 + 36000.76983 * t + 0.0003032 * t * t;  // 几何平黄经
            double meanAnomaly = 357.52911 + 35999.05029 * t - 0.0001537 * t * t;    // 平近点角
            double m = meanAnomaly * Math.PI / 180.0;

            // 中心差
            double center = (1.914602 - 0.004817 * t - 0.000014 * t * t) * Math.Sin(m)
                          + (0.019993 - 0.000101 * t) * Math.Sin(2 * m)
                          + 0.000289 * Math.Sin(3 * m);

            double trueLongitude = meanLongitude + center;

            // 章动 + 光行差修正 → 视黄经
            double omega = (125.04 - 1934.136 * t) * Math.PI / 180.0;
            double apparent = trueLongitude - 0.00569 - 0.00478 * Math.Sin(omega);

            return ((apparent % 360) + 360) % 360;
        }

        /// <summary>公历时刻 → 儒略日（Meeus 第 7 章标准公式）</summary>
        private static double ToJulianDay(DateTime utc)
        {
            int y = utc.Year;
            int m = utc.Month;
            double day = utc.Day + (utc.Hour + utc.Minute / 60.0 + utc.Second / 3600.0) / 24.0;

            if (m <= 2)
            {
                y -= 1;
                m += 12;
            }

            int a = y / 100;
            int b = 2 - a + a / 4;

            return Math.Floor(365.25 * (y + 4716))
                 + Math.Floor(30.6001 * (m + 1))
                 + day + b - 1524.5;
        }
    }
}
