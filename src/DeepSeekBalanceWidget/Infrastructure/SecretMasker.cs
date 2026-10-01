using System.Text.RegularExpressions;

namespace DeepSeekBalanceWidget.Infrastructure;

/// <summary>
/// 日志脱敏兜底层（B-1 加强版，写入层"出现即打码"）：
/// ① 运行期注册的完整 Key（RegisterSecret，设置保存/凭据读取后调用）做包含式整段替换——正式版要求；
/// ② 形态兜底：Authorization: Bearer 头、sk- 前缀令牌、key/token 赋值片段。
/// 探针 E 用例 E 已验证同构实现的兜底有效（假 Key 三形态注入 → 日志 0 明文）。
/// </summary>
public static partial class SecretMasker
{
    private static readonly object Gate = new();
    private static readonly List<string> RegisteredSecrets = new();

    [GeneratedRegex(@"(?i)(authorization\s*[:=]\s*bearer\s+)[A-Za-z0-9._\-]{4,}")]
    private static partial Regex BearerRx();

    [GeneratedRegex(@"sk-[A-Za-z0-9._\-]{4,}")]
    private static partial Regex SkRx();

    [GeneratedRegex(@"(?i)\b(api[_\-]?key|access[_\-]?token|token|secret|key)\s*[:=]\s*[""']?[A-Za-z0-9._\-]{8,}")]
    private static partial Regex AssignRx();

    /// <summary>注册完整 Key：日志中出现该串即整段打码（B-1：完整 Key 包含式匹配）。</summary>
    public static void RegisterSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 8) return;
        lock (Gate)
        {
            if (!RegisteredSecrets.Contains(secret)) RegisteredSecrets.Add(secret);
        }
    }

    public static string Mask(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var s = input;
        lock (Gate)
        {
            foreach (var sec in RegisteredSecrets)
                s = s.Replace(sec, "sk-***MASKED***", StringComparison.Ordinal);
        }
        s = BearerRx().Replace(s, m => m.Groups[1].Value + "***MASKED***");
        s = SkRx().Replace(s, "sk-***MASKED***");
        s = AssignRx().Replace(s, m =>
        {
            var idx = m.Value.IndexOfAny(['=', ':']);
            return idx >= 0 ? m.Value[..(idx + 1)] + "***MASKED***" : "***MASKED***";
        });
        return s;
    }

    /// <summary>形态扫描：是否残留疑似敏感明文（日志 0 泄漏断言用）。</summary>
    public static bool LooksSensitive(string line) => SkRx().IsMatch(line) || BearerRx().IsMatch(line);
}
