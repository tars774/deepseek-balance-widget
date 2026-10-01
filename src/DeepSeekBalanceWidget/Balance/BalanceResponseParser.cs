using System.Globalization;
using System.Text.Json;

namespace DeepSeekBalanceWidget.Balance;

/// <summary>
/// 余额响应解析（D-06，探针 B §二 实测结构）：
/// JsonDocument.Parse → 根须为 Object → 读顶层 is_available（缺失或非 bool 报 JsonException）→
/// 遍历 balance_infos[]，元素内取 currency / total_balance（字符串，容错 Number）→
/// decimal.TryParse（InvariantCulture）→ 优先取 CNY 元素，取不到取第一个；
/// granted_balance / topped_up_balance 一并解析备用；未知字段忽略。
/// </summary>
public static class BalanceResponseParser
{
    public sealed record Entry(string Currency, decimal Total, decimal? Granted, decimal? ToppedUp);

    /// <summary>解析成功返回（isAvailable, 首选币种余额）。失败抛 JsonException / FormatException（D-07 畸形态）。</summary>
    public static (bool IsAvailable, Entry Preferred) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);   // 畸形文本 → JsonException（探针 B m1 路径）
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException($"root element is {root.ValueKind}, expected Object");

        if (!root.TryGetProperty("is_available", out var ia))
            throw new JsonException("is_available missing");   // 缺失=结构不完整→畸形（探针 B 实测官方响应恒有该字段）
        bool isAvailable = ia.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new JsonException($"is_available is {ia.ValueKind}, expected Boolean"),
        };

        if (!root.TryGetProperty("balance_infos", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new JsonException($"balance_infos missing or not an array (got " +
                $"{(root.TryGetProperty("balance_infos", out var b) ? b.ValueKind.ToString() : "absent")})");

        Entry? preferred = null;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new JsonException($"balance_infos element is {el.ValueKind}, expected Object");

            string currency = "?";
            if (el.TryGetProperty("currency", out var c))
            {
                if (c.ValueKind != JsonValueKind.String) throw new JsonException($"currency is {c.ValueKind}, expected String");
                currency = c.GetString() ?? "?";
            }

            decimal total = ParseAmount(el, "total_balance");   // m2 路径：结构校验拒绝
            decimal? granted = TryParseAmount(el, "granted_balance");
            decimal? toppedUp = TryParseAmount(el, "topped_up_balance");

            var entry = new Entry(currency, total, granted, toppedUp);
            // 优先 CNY 元素；否则取第一个元素（D-06）
            if (preferred is null || (currency == "CNY" && preferred.Currency != "CNY"))
                preferred = entry;
        }

        if (preferred is null) throw new JsonException("balance_infos is empty");
        return (isAvailable, preferred);
    }

    private static decimal ParseAmount(JsonElement el, string name)
    {
        var v = TryParseAmount(el, name);
        return v ?? throw new JsonException($"{name} missing");
    }

    private static decimal? TryParseAmount(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var t)) return null;
        string? raw = t.ValueKind switch
        {
            JsonValueKind.String => t.GetString(),
            JsonValueKind.Number => t.GetRawText(),     // 容错 Number（实测为字符串）
            _ => throw new JsonException($"{name} is {t.ValueKind}, expected String"),
        };
        if (raw is null) return null;
        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
            throw new FormatException($"{name} \"{raw}\" is not a valid invariant-culture decimal");
        return d;
    }
}
