using System.Net.Http;
using System.Text.Json;

namespace DeepSeekBalanceWidget.Peak;

public sealed record ApiResult(bool Success, string? Error, HolidayData? Data)
{
    public static ApiResult Ok(HolidayData data) => new(true, null, data);
    public static ApiResult Fail(string error) => new(false, error, null);
}

public interface IHolidaySource
{
    string Name { get; }
    Task<ApiResult> FetchAsync(DateOnly date, CancellationToken ct = default);
}

/// <summary>
/// 主源（探针 C HolidayClients 移植）：GET {base}YYYY-MM-DD
/// （默认 https://publicapi.xiaoai.me/holiday/day?date=）。
/// 实测返回 {"code":0,"msg":"ok","data":[{daytype:0-3, holiday, rest, date, week, ...}]}；
/// daytype: 0=工作日 1=法定节假日 2=双休日 3=调休补班。
/// 成功判定 = 业务层 code∈{0,200}（缺 code 容忍），不得按"非 200 判失败"（D-10）。
/// 【C-1 必改】解析必须校验 data 内日期回显与请求日期匹配；无匹配日期判失败，禁止静默取 data[0]。
/// </summary>
public sealed class PrimaryHolidayClient : IHolidaySource
{
    public const string DefaultBaseUrl = "https://publicapi.xiaoai.me/holiday/day?date=";
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    public string Name => "primary(xiaoai)";

    public PrimaryHolidayClient(HttpClient http, string? baseUrl = null)
    {
        _http = http;
        _baseUrl = baseUrl ?? DefaultBaseUrl;
    }

    public async Task<ApiResult> FetchAsync(DateOnly date, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + date.ToString("yyyy-MM-dd"), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if ((int)resp.StatusCode != 200)
                return ApiResult.Fail($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            return ParsePrimary(body, date, Name) is { } data
                ? ApiResult.Ok(data)
                : ApiResult.Fail("主源响应畸形(无 code=ok/data 数组/daytype 或日期不匹配)");
        }
        catch (Exception ex)
        {
            return ApiResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static HolidayData? ParsePrimary(string json, DateOnly date, string sourceName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // 实测成功码为 0（"code":0,"msg":"ok"）；兼容 200；缺 code 字段时容忍（D-10）
            if (root.TryGetProperty("code", out var codeEl))
            {
                int? code = codeEl.ValueKind switch
                {
                    JsonValueKind.Number when codeEl.TryGetInt32(out var c) => c,
                    JsonValueKind.String when int.TryParse(codeEl.GetString(), out var c) => c,
                    _ => null,
                };
                if (code is not (0 or 200)) return null;
            }

            if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array) return null;
            if (dataEl.GetArrayLength() < 1) return null;

            // 【C-1 必改】日期回显校验：必须存在与请求日期匹配的元素；禁止静默取 data[0]
            JsonElement? matched = null;
            foreach (var e in dataEl.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.Object &&
                    e.TryGetProperty("date", out var de) && de.ValueKind == JsonValueKind.String &&
                    DateOnly.TryParse(de.GetString(), out var dd) && dd == date)
                {
                    matched = e;
                    break;
                }
            }
            if (matched is null) return null;   // 无匹配日期判失败
            var elem = matched.Value;

            if (!elem.TryGetProperty("daytype", out var dtEl)) return null;
            int? daytype = dtEl.ValueKind switch
            {
                JsonValueKind.Number when dtEl.TryGetInt32(out var v) => v,
                JsonValueKind.String when int.TryParse(dtEl.GetString(), out var v) => v,
                _ => null,
            };
            if (daytype is null || daytype < 0 || daytype > 3) return null;
            int dt = daytype.Value;

            string? holiday = elem.TryGetProperty("holiday", out var hEl) && hEl.ValueKind == JsonValueKind.String
                ? hEl.GetString() : null;

            return new HolidayData(
                IsReportedHoliday: dt == 1,
                IsMakeupWorkday: dt == 3,
                IsWeekendReported: dt == 2,
                sourceName, holiday);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// 备源（探针 C 移植）：GET https://holiday.dreace.top?date=YYYY-MM-DD。
/// 实测返回 {"date","isHoliday":bool,"note","type":"工作日"/"假日"}。
/// 注意：dreace 会把调休补班周末报成 isHoliday=false/"工作日"——本地星期优先规则（D-11）兜住。
/// </summary>
public sealed class BackupHolidayClient : IHolidaySource
{
    public const string DefaultBaseUrl = "https://holiday.dreace.top?date=";
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    public string Name => "backup(dreace)";

    public BackupHolidayClient(HttpClient http, string? baseUrl = null)
    {
        _http = http;
        _baseUrl = baseUrl ?? DefaultBaseUrl;
    }

    public async Task<ApiResult> FetchAsync(DateOnly date, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + date.ToString("yyyy-MM-dd"), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if ((int)resp.StatusCode != 200)
                return ApiResult.Fail($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            return ParseBackup(body, date, Name) is { } data
                ? ApiResult.Ok(data)
                : ApiResult.Fail("备源响应畸形(无 isHoliday)");
        }
        catch (Exception ex)
        {
            return ApiResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static HolidayData? ParseBackup(string json, DateOnly date, string sourceName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("isHoliday", out var ihEl)) return null;

            bool isHoliday = ihEl.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(ihEl.GetString(), out var b) => b,
                _ => throw new FormatException("isHoliday 不是 bool"),
            };
            bool weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            string? type = root.TryGetProperty("type", out var tEl) && tEl.ValueKind == JsonValueKind.String
                ? tEl.GetString() : null;
            string? note = root.TryGetProperty("note", out var nEl) && nEl.ValueKind == JsonValueKind.String
                ? nEl.GetString() : null;

            return new HolidayData(
                IsReportedHoliday: isHoliday && !weekend,
                IsMakeupWorkday: !isHoliday && weekend,
                IsWeekendReported: isHoliday && weekend,
                sourceName, note ?? type);
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>节假日 HTTP 客户端工厂（探针 C 口径：统一 8s 超时 + 自动解压 + JSON Accept）。</summary>
public static class HolidayHttpFactory
{
    public static HttpClient Create()
    {
        var h = new HttpClient(new System.Net.Http.SocketsHttpHandler
        { AutomaticDecompression = System.Net.DecompressionMethods.All });
        h.Timeout = TimeSpan.FromSeconds(8);
        h.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DeepSeekBalanceWidget/1.0");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return h;
    }
}
