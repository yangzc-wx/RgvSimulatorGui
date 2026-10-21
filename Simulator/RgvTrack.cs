using System;
using System.Numerics;

namespace Kean.Tools.RgvSimulatorGui
{
    /// <summary>
    /// 重庆四向车二维码轨道布局（数据来自 WCS_CELL：cell_code 为「层-列-行」）。
    /// 每层 47 列，前 40 列满 13 行，尾部 7 列部分行无码；三层布局一致，层首码分别为 8267、8829、9391。
    /// 用于把 taskType=1 的逐格推进映射成真实货位码，避免 X 方向逐个加 1 串列。
    /// </summary>
    public sealed class RgvTrack
    {
        private static readonly int[] TierStart = { 8267, 8829, 9391 };

        // 每列存在的行位掩码：bit0 = f_y1 ... bit12 = f_y13。
        private static readonly int[] ColumnRows =
        {
            0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF,
            0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF,
            0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF,
            0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF, 0x1FFF,
            0x181F, // 列41：1,2,3,4,5,12,13
            0x1C1F, // 列42：1,2,3,4,5,11,12,13
            0x181F, // 列43：1,2,3,4,5,12,13
            0x181F, // 列44：1,2,3,4,5,12,13
            0x1803, // 列45：1,2,12,13
            0x1807, // 列46：1,2,3,12,13
            0x1803, // 列47：1,2,12,13
        };

        private static readonly int[] ColumnStart = BuildColumnStart();
        private static readonly int TierSize = ColumnStart[ColumnStart.Length - 1] + PopCount(ColumnRows[^1]);

        private static int[] BuildColumnStart()
        {
            var start = new int[ColumnRows.Length];
            int sum = 0;
            for (int i = 0; i < ColumnRows.Length; i++)
            {
                start[i] = sum;
                sum += PopCount(ColumnRows[i]);
            }
            return start;
        }

        public bool TryLocate(int cellId, out int tier, out int col, out int rank)
        {
            tier = col = rank = 0;
            for (int t = 0; t < TierStart.Length; t++)
            {
                int offset = cellId - TierStart[t];
                if (offset < 0 || offset >= TierSize)
                {
                    continue;
                }
                int rem = offset;
                for (int c = 0; c < ColumnRows.Length; c++)
                {
                    int width = PopCount(ColumnRows[c]);
                    if (rem < width)
                    {
                        tier = t;
                        col = c;
                        rank = rem;
                        return true;
                    }
                    rem -= width;
                }
                break;
            }
            return false;
        }

        /// <summary>
        /// 把码值换算成 WCS 货位编码「层-列-行」（如 01-07-02）；码不存在返回 false。
        /// </summary>
        public bool TryGetCellCode(int cellId, out string cellCode)
        {
            cellCode = null;
            if (!TryLocate(cellId, out int tier, out int col, out int rank))
            {
                return false;
            }
            cellCode = $"{tier + 1:D2}-{col + 1:D2}-{RowAt(ColumnRows[col], rank):D2}";
            return true;
        }

        /// <summary>
        /// 沿 direction 走一格：1 X+、2 X-、3 Y-、4 Y+。目标位置无码或越界返回 false。
        /// </summary>
        public bool TryStep(int cellId, int direction, out int next)
        {
            next = cellId;
            if (!TryLocate(cellId, out int tier, out int col, out int rank))
            {
                return false;
            }
            int mask = ColumnRows[col];
            int width = PopCount(mask);
            switch (direction)
            {
                case 4: // Y+：同一列内码值 +1
                    if (rank < width - 1)
                    {
                        next = cellId + 1;
                        return true;
                    }
                    return false;
                case 3: // Y-：同一列内码值 -1
                    if (rank > 0)
                    {
                        next = cellId - 1;
                        return true;
                    }
                    return false;
                case 1: // X+：跨到下一列同一行
                    return StepColumn(tier, col, RowAt(mask, rank), +1, out next);
                case 2: // X-：跨到上一列同一行
                    return StepColumn(tier, col, RowAt(mask, rank), -1, out next);
                default:
                    return false;
            }
        }

        private bool StepColumn(int tier, int col, int row, int delta, out int next)
        {
            next = 0;
            int target = col + delta;
            if (target < 0 || target >= ColumnRows.Length)
            {
                return false;
            }
            int targetMask = ColumnRows[target];
            if ((targetMask & (1 << (row - 1))) == 0)
            {
                return false;
            }
            next = TierStart[tier] + ColumnStart[target] + Rank(targetMask, row);
            return true;
        }

        private static int RowAt(int mask, int rank)
        {
            for (int row = 0; row < 13; row++)
            {
                if ((mask & (1 << row)) != 0 && rank-- == 0)
                {
                    return row + 1;
                }
            }
            return 1;
        }

        private static int Rank(int mask, int row) => PopCount(mask & ((1 << (row - 1)) - 1));

        private static int PopCount(int mask) => BitOperations.PopCount((uint)mask);
    }
}
