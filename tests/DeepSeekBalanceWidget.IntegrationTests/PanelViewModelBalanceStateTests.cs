using DeepSeekBalanceWidget.Balance;
using Xunit;

namespace DeepSeekBalanceWidget.IntegrationTests;

/// <summary>
/// PanelViewModel 余额区四态渲染（D-07 + A1 §5-1/2）：
/// 未配置 → "请先在设置中配置 API Key"；401 → "API Key 无效，请检查设置"；
/// 网络失败 → "余额获取失败"；三类失败态一律不显示余额数字（不显示过期缓存）。
/// </summary>
public sealed class PanelViewModelBalanceStateTests
{
    [Theory]
    [InlineData(BalanceStatus.NotConfigured, "请先在设置中配置 API Key")]
    [InlineData(BalanceStatus.Unauthorized, "API Key 无效，请检查设置")]
    [InlineData(BalanceStatus.NetworkFailure, "余额获取失败")]
    [InlineData(BalanceStatus.MalformedResponse, "余额获取失败")]
    public void Failure_States_Show_Hint_Without_Balance_Number(BalanceStatus status, string expectedHint)
    {
        // 先置于成功态（模拟"有缓存余额"），再刷新失败——验证不回退显示过期余额
        var vm = new DeepSeekBalanceWidget.Panel.PanelViewModel();
        vm.UpdateBalance(new BalanceResult(BalanceStatus.Success, 110.00m, "CNY", IsAvailable: true, Note: null),
            manualBalanceEnabled: false, manualBalanceText: "", atUtc: DateTimeOffset.UtcNow);
        Assert.True(vm.BalanceNumberVisible);

        vm.UpdateBalance(new BalanceResult(status, null, null, IsAvailable: true, Note: null),
            manualBalanceEnabled: false, manualBalanceText: "", atUtc: DateTimeOffset.UtcNow);

        Assert.False(vm.BalanceNumberVisible);           // 不显示任何余额数字
        Assert.False(vm.CaptionVisible);
        Assert.True(vm.BalanceHintVisible);
        Assert.Equal(expectedHint, vm.BalanceHint);
        if (status is BalanceStatus.NotConfigured or BalanceStatus.Unauthorized)
            Assert.True(vm.IsBalanceClickable);           // A1 约定 1：整块可点击打开设置
    }

    [Fact]
    public void Success_With_Zero_Balance_Is_Normal_Success_With_Unavailable_Hint()
    {
        var vm = new DeepSeekBalanceWidget.Panel.PanelViewModel();
        vm.UpdateBalance(new BalanceResult(BalanceStatus.Success, 0.00m, "CNY", IsAvailable: false, Note: null),
            manualBalanceEnabled: false, manualBalanceText: "", atUtc: DateTimeOffset.UtcNow);
        Assert.True(vm.BalanceNumberVisible);             // 0 余额普通成功（探针 B §三）
        Assert.Contains("0.00", vm.BalanceText);
        Assert.True(vm.UnavailableVisible);               // is_available=false → 附加提示，非错误
    }
}
