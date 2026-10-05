using System;
using System.Collections.Generic;
using System.Globalization;

namespace NameTool.Services;

/// <summary>
/// 自然排序比较器（用户定死的规则，2026-10-05）：
/// **逐字对比，优先级为 英文(不分大小写) &gt; 数字 &gt; 中文拼音首字母**，数字段按数值比。
/// <para>
/// - 数字段（如「副本 (10)」的 10）整段按数值比较，绝不允许 1/10/11/…/2/20/21 这种字符串序；
/// - 英文字母排在数字前、数字排在汉字前（如「a」&lt;「2」&lt;「中」）；
/// - 英文不分大小写，中文按 zh-CN 拼音逐字比；
/// - 标点 / 空格 / 括号等符号**不参与定序**（两边都是符号就跳过；符号对上文字则文字优先）——
///   这样「新建文本文档.txt」的「.txt」不会抢在「- 副本」前面，主文件固定排在副本组之前：
///   新建文本文档.txt → 新建文本文档 - 副本.txt → 副本 (1).txt → 副本 (2).txt …
/// </para>
/// 两张表（本地列表 / 115 列表）共用这一个实例。
/// </summary>
public sealed class NaturalNameComparer : IComparer<string?>
{
    public static readonly NaturalNameComparer Instance = new();

    /// <summary>英文 / 中文单字符比较：中文按拼音（zh-CN）、忽略大小写。</summary>
    private static readonly StringComparer TextComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), true);

    /// <summary>
    /// 字符优先级（升序时小者在前）：英文 0 &lt; 数字 1 &lt; 中文 2 &lt; 其他符号 3。
    /// 只认 ASCII 英文与数字、CJK 基本区+扩展A 汉字；全角数字 / 变形字母按「其他符号」处理。
    /// </summary>
    private static int Rank(char c)
    {
        if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) return 0;
        if (c >= '0' && c <= '9') return 1;
        if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF)) return 2;
        return 3;
    }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int ix = 0, iy = 0;

        while (ix < x.Length && iy < y.Length)
        {
            var cx = x[ix];
            var cy = y[iy];

            // 两边都是数字：整段数字按数值比（去前导零 → 比位数 → 逐位），位数不同也正确
            if (cx >= '0' && cx <= '9' && cy >= '0' && cy <= '9')
            {
                int endX = ix, endY = iy;
                while (endX < x.Length && x[endX] >= '0' && x[endX] <= '9') endX++;
                while (endY < y.Length && y[endY] >= '0' && y[endY] <= '9') endY++;

                var cmp = CompareNumericRuns(x, ix, endX, y, iy, endY);
                if (cmp != 0) return cmp;

                ix = endX;
                iy = endY;
                continue;
            }

            var rankX = Rank(cx);
            var rankY = Rank(cy);

            // 优先级不同：英文 &lt; 数字 &lt; 中文 &lt; 符号，小者在前
            if (rankX != rankY) return rankX - rankY;

            if (rankX == 3)
            {
                // 两边都是标点 / 空格 / 括号：跳过，不让符号干扰主顺序
                ix++;
                iy++;
                continue;
            }

            // 同级（英文 或 中文）：逐字比（英文不分大小写、中文按拼音）
            var textCmp = TextComparer.Compare(cx.ToString(), cy.ToString());
            if (textCmp != 0) return textCmp;

            ix++;
            iy++;
        }

        // 一方先耗尽：短的一方在前（如「副本」排在「副本 (1)」前面）
        return (x.Length - ix).CompareTo(y.Length - iy);
    }

    /// <summary>
    /// 比较两段数字（[startX,endX) / [startY,endY)，都是 ASCII 数字）：
    /// 去掉前导零后先比位数，再逐位比。数值相等（如「002」与「2」）返回 0，
    /// 交由后续字符或整体长度决定先后。
    /// </summary>
    private static int CompareNumericRuns(string x, int startX, int endX, string y, int startY, int endY)
    {
        while (startX < endX && x[startX] == '0') startX++;
        while (startY < endY && y[startY] == '0') startY++;

        var lenX = endX - startX;
        var lenY = endY - startY;
        if (lenX != lenY) return lenX.CompareTo(lenY);

        for (var i = 0; i < lenX; i++)
        {
            var cx = x[startX + i];
            var cy = y[startY + i];
            if (cx != cy) return cx.CompareTo(cy);
        }

        return 0;
    }
}
