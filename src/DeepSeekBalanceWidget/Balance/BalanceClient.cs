using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using DeepSeekBalanceWidget.Credentials;
using DeepSeekBalanceWidget.Infrastructure;

namespace DeepSeekBalanceWidget.Balance;

/// <summary>
/// 余额客户端（D-07 四类异常精确分类，探针 B §1-3~6 实证做法）：
/// GET https://api.deepseek.com/user/balance + Authorization: Bearer；15s 超时；
/// 未配置 Key 不发请求直接返回 NotConfigured；401 错误体不送余额解析器；
/// DNS 失败 = HttpRequestException←SocketException(HostNotFound)、超时 = TaskCanceledException←TimeoutException、
/// 畸形 = JsonException/FormatException；is_available=false 为展示态非错误。
/// </summary>
public sealed class BalanceClient : IBalanceService
{
    public const string Endpoint = "https://api.deepseek.com/user/balance";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly ICredentialStore _credentials;
    private readonly HttpClient _http;

    public BalanceClient(ICredentialStore credentials)
    {
        _credentials = credentials;
        _http = new HttpClient { Timeout = RequestTimeout };
    }

    public async Task<BalanceResult> RefreshAsync()
    {
        // 未配置 Key：不发请求，直接进入「未配置」态（§4.5 / A1 约定 1）
        if (!_credentials.TryRead(out var secret, out var win32) || string.IsNullOrEmpty(secret))
        {
            AppLog.Info(win32 == 0 || win32 == Win32CredentialStore.ErrorNotFound
                ? "未配置 Key，跳过本次余额请求"
                : $"凭据读取失败（Win32 {win32}）→ 视同未配置，不发请求（§6-7）");
            return new BalanceResult(BalanceStatus.NotConfigured, null, null, IsAvailable: true, Note: null);
        }
        SecretMasker.RegisterSecret(secret);   // B-1：完整 Key 注册进日志脱敏兜底

        using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        try
        {
            using var resp = await _http.SendAsync(req);
            int status = (int)resp.StatusCode;
            if (status == 401)
            {
                // 401 错误体不是余额 JSON，不送解析器（探针 B §1-3；服务端已自动掩码 Key 回显）
                AppLog.Warn("余额刷新：HTTP 401（Key 无效）");
                return new BalanceResult(BalanceStatus.Unauthorized, null, null, IsAvailable: true, Note: null);
            }
            if (status != 200)
            {
                AppLog.Warn($"余额刷新：HTTP {status} {resp.ReasonPhrase}");
                return new BalanceResult(BalanceStatus.NetworkFailure, null, null, IsAvailable: true, $"HTTP {status}");
            }

            var body = await resp.Content.ReadAsStringAsync();
            var (isAvailable, entry) = BalanceResponseParser.Parse(body);
            AppLog.Info($"余额刷新成功 currency={entry.Currency} balance={entry.Total} is_available={isAvailable}");
            return new BalanceResult(BalanceStatus.Success, entry.Total, entry.Currency, isAvailable, Note: null);
        }
        catch (JsonException ex)
        {
            AppLog.Error($"余额响应畸形 JSON：{ex.Message}（Line {ex.LineNumber}, Pos {ex.BytePositionInLine}）");
            return new BalanceResult(BalanceStatus.MalformedResponse, null, null, IsAvailable: true, ex.GetType().Name);
        }
        catch (FormatException ex)
        {
            AppLog.Error($"余额字段格式非法：{ex.Message}");
            return new BalanceResult(BalanceStatus.MalformedResponse, null, null, IsAvailable: true, ex.GetType().Name);
        }
        catch (HttpRequestException ex)
        {
            var (kind, detail) = ClassifyNetwork(ex);
            AppLog.Warn($"余额刷新网络失败：{detail}");
            return new BalanceResult(BalanceStatus.NetworkFailure, null, null, IsAvailable: true, kind);
        }
        catch (Exception ex) when (ex is TaskCanceledException or TimeoutException)
        {
            AppLog.Warn($"余额刷新超时（>{RequestTimeout.TotalSeconds:0}s）：{ex.GetType().Name}");
            return new BalanceResult(BalanceStatus.NetworkFailure, null, null, IsAvailable: true, "timeout");
        }
    }

    /// <summary>DNS 失败 = 内链 SocketException(HostNotFound)（探针 B §1-4 分类口径）。</summary>
    private static (string Kind, string Detail) ClassifyNetwork(HttpRequestException ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException se)
            {
                var kind = se.SocketErrorCode == System.Net.Sockets.SocketError.HostNotFound ? "dns" : "socket";
                return (kind, $"HttpRequestException←SocketException({se.SocketErrorCode})");
            }
        }
        return ("http", $"{ex.GetType().Name}: {ex.Message}");
    }
}
