namespace DeepSeekBalanceWidget.Credentials;

/// <summary>凭据存取抽象（正式实现 = Windows Credential Manager；单元测试可注入内存实现）。</summary>
public interface ICredentialStore
{
    /// <summary>读取 API Key。返回 false = 无凭据（win32=1168 ERROR_NOT_FOUND）或读取失败。</summary>
    bool TryRead(out string? secret, out int win32Error);

    /// <summary>写入/覆盖 API Key。</summary>
    bool Write(string secret, out int win32Error);

    /// <summary>删除 API Key（不存在视为成功）。</summary>
    bool Delete(out int win32Error);
}
