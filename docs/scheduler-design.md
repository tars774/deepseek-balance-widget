# 定时巡查机制设计（Scheduler）— 阶段 3 设计文档

| 项目 | 内容 |
| --- | --- |
| 文档阶段 | 阶段 3：定时巡查机制设计与探针 |
| 完成日期 | 2026-09-29 |
| 文档性质 | 阶段 4 正式实现 Scheduler 模块的**直接输入**；本文档不改动任何既有文件 |
| 直接输入 | `docs/final-technical-plan.md` §3（Scheduling/Scheduler 占位）、§4（D-08/D-13/D-14）、§5.1（心跳主回路）、§8（8 条输入约束） |
| 探针实证 | `probes/probe-e-scheduler/PROBE-E-REPORT.md`（本文 §11 引用其全部实测数字） |
| 探针代码 | `probes/probe-e-scheduler/`（WPF net8.0-windows + H.NotifyIcon.Wpf 2.3.2，real/sim 双模式） |

---

## 0. 对 §8 八条输入约束的逐条映射（导览）

| # | 约束（final-technical-plan §8） | 本文档落点 |
| --- | --- | --- |
| 1 | 1 秒心跳（DispatcherTimer vs 专用线程由阶段 3 定夺） | §2.1 选型论证、§2.2 tick 管线 |
| 2 | 状态重算每秒、事件仅翻转沿 | §2.2 流程①–④、§3 事件契约 |
| 3 | 余额刷新三触发源（定时 30min 可配置 / 开面板即刷 / 手动按钮）+ 显示期快轮询 5s（需求变更 2026-10-02） | §4、§4.6 |
| 4 | 跨天检测（UTC 16:00） | §5 |
| 5 | 退避复用 PeakService 1/5/15 全局门 + 成功清零即刷 | §6 |
| 6 | 巡查日志 0 Key / 0 Authorization | §8 |
| 7 | ITimeProvider 可注入时钟 | §7 |
| 8 | 计数器回归基线（沿用探针 C E/F 套件做法） | §7.3、§9.3 |

---

## 1. 目标与定位

Scheduler 是全应用的**时间基准与状态驱动源**：以 1 秒心跳驱动"读时钟 → 峰谷快照重算 → UI 直刷 → 状态翻转沿检测 → 跨天检测 → 余额调度检查"六步管线，并对外发出 4 类事件。它在核心功能（Balance/Peak 的 UI 呈现）之前独立实现与验证（需求 15 流程约束），探针 E 已按本设计完整实装并双模式实测。

设计原则（按优先级）：

1. **tick 内绝无长耗时操作**——HTTP/IO 一律异步外发，心跳只做"内存计算 + 事件发布"；
2. **倒计时永远按绝对时间差计算**，不做任何累加——单 tick 晚到不影响显示正确性；
3. **事件按翻转沿发**——托盘/tooltip 只在状态翻转时重建，倒计时/进度每 tick 直刷（面板可见时）；
4. **一切时间读取经 ITimeProvider**——生产用真实时钟，测试用可步进/可加速假时钟；
5. **全部间隔参数可注入**——心跳、余额间隔、显示期快轮询间隔/退避、去抖窗、退避序列均出自 `SchedulerOptions`。

---

## 2. 心跳管线

### 2.1 心跳源选型：DispatcherTimer(1s, UI 线程) —— 定夺

R15 给出两个候选：DispatcherTimer(1s) 或专用心跳线程。**定夺：DispatcherTimer**，理由：

| 维度 | DispatcherTimer(1s, UI 线程) | 专用心跳线程 |
| --- | --- | --- |
| 线程封送 | 天然在 UI 线程，事件零封送成本，WPF 控件直更新 | 每次更新需 `Dispatcher.Invoke/BeginInvoke`，多一道竞态面 |
| 精度 | 受系统计时器量化 ~15.6ms；**配合 §2.3 绝对对齐后无累积漂移**（探针 E 实测 5 分钟累计偏差 <0.1s，§11） | 可做到更细粒度，但秒级 UI 完全用不上 |
| 功耗/CPU | 空闲即挂起，tick 间隙零开销（探针 E 实测浸泡稳态 CPU 1–2%·核、tick 本身仅 0.09ms，§11.3；2026-09-29 探针 E 实测修订） | 常驻线程即使空转也要挂起/唤醒 |
| 失败模式 | tick 处理器异常被 Dispatcher 捕获 → 全局异常兜底（与 UI 同一兜底链） | 线程内异常需自带 try/catch，否则静默死亡（心跳停摆无感知） |
| 与挂起/恢复 | 系统睡眠时 Dispatcher 暂停、恢复后继续，且因到期计算基于 ITimeProvider 绝对时间，醒来后首个 tick 自动按真实时间重算（§4.2） | 睡眠期间线程计时漂移需自行处理 |

专用线程的唯一优势（亚秒精度）对秒级 UI 无意义，其代价（封送、异常、睡眠）都是真实风险。**结论：DispatcherTimer，`Interval=1s` 起步，配合 §2.3 的绝对时刻对齐消除累积漂移。**

### 2.2 tick 处理流程（六步管线，单 tick 全内存操作）

每个 tick（UI 线程）按序执行，目标耗时 ≤1ms 量级（探针 E 实测 mean ≈0.02–0.2ms，§11）：

```
OnTick():
  ① 读时钟      t = ITimeProvider.UtcNow（绝对时刻，永不累加）
  ② 漂移记账    记录与上一 tick 的实际间隔、与绝对对齐槽位的迟到量（§2.3）；
                异常（间隔越界 / 迟到 >100ms）按 §8 记日志
  ③ 峰谷快照    同步部分：北京墙上时间 → 时段判定 → 倒计时(精确差值,HH>24,D-08) → 进度(剩余÷总长,
                需求变更 2026-10-02,取代旧 D-09「已过比例」口径——绿条与倒计时同源同值)
                （节假日数据取自 HolidayService 内存缓存，未命中且不在退避窗时异步拉取，见 ④'）
  ④ 直刷 UI     面板可见时更新倒计时文字/进度条（tabular-nums）；每 tick 都刷，不发事件
  ⑤ 翻转沿检测  newState != _lastState → 发 StateChanged(旧→新, Degraded)（仅此时刻，§3.2）
                跨天检测   北京日期 != _lastDate → 走 §5 跨天流程（先于 ⑥ 记账）
  ⑥ 余额调度    到期检查见 §4.2 / §4.6（显示期快轮询）；到期 → 异步触发刷新（绝不在 tick 内 await HTTP，§2.4）
```

要点：

- ③ 的**同步部分不含任何网络**：节假日数据多数命中内存缓存；未命中（首启/跨天/前瞻新日期）时按 §5.3 的异步路径补拉，拉取完成前的快照按已知数据计算（未知日期按工作日处理 + Degraded 标志），下一 tick 自然修正——**tick 永远不等待网络**。
- ④ 与 ⑤ 的分工（约束 2 的落地）：**托盘图标/tooltip/状态胶囊 = 翻转沿更新；倒计时/进度 = 每 tick 直刷**。托盘 tooltip 重建采纳探针 A 建议 A-1（状态切换时 NIM_DELETE+NIM_ADD 重建，避免 Shell 层名称拼接问题）。
- ⑤ 先于 ⑥：跨天可能改变"今天是否工作日"，先完成状态与日期记账，再检查余额到期，避免跨天 tick 用旧日期判断。

### 2.3 绝对时刻对齐（自校正 Interval）—— 消除累积漂移的关键（2026-09-29 探针 E 实测修订）

DispatcherTimer 的触发时刻被系统计时器量化（~15.6ms 档）——若不干预，每 tick 实际周期 ≈ 1s + 处理耗时 + 量化余数，**漂移逐 tick 累积**（探针 E 实测：朴素模式呈累积趋势，0.03–0.85 s/分钟逐轮波动，见 §11.2 与 PROBE-E-REPORT §4.2）。

**平台事实修正（2026-09-29 探针 E 实测修订）**：原设计假设"DispatcherTimer 每次触发后按'当时时刻 + Interval'重排下次触发"，实测不成立——WPF 在 Tick 处理器内**修改 Interval** 时按**旧到期点**重排（仅 Interval 恒定不变时才按触发时刻重排）。原公式 `Interval = nextSlot − fireAt` 在该语义下把量化迟到做了二次补偿：一旦本次触发早于槽位即算出 ~15ms 的"追赶 Interval"，每 3–8 tick 成对多打 1 拍、每对吞掉 ~1s（三模式对照实验：朴素 / 原公式 / 伺服式 55s 漂移 **+4.9 / −2978.6 / +3.4ms**，验收方独立复测伺服式漂移 **+7.2ms**，见 PROBE-E-REPORT §4.1）。因此实现采用**伺服式重排**：

```
Start():
  _sw 启动（Stopwatch，单调时钟）
  _nextSlot = _sw.Elapsed + HeartbeatInterval
  _timer.Interval = HeartbeatInterval

OnTimer():                                # UI 线程
  fireAt = _sw.Elapsed
  e = fireAt - _nextSlot                  # 带符号误差（负=早于槽位触发）
  while (e >= HeartbeatInterval)          # 长停顿：跳槽位追赶，不爆发
    _nextSlot += HeartbeatInterval;  e -= HeartbeatInterval
  _nextSlot += HeartbeatInterval          # 每 tick 恰推进一个槽位
  _timer.Interval = clamp(HeartbeatInterval - e, 250ms, 2000ms)   # 伺服式重排（关键）
  ...执行 §2.2 六步管线...
```

- 伺服式 `Interval = Heartbeat − e` 在"按到期点重排"与"按触发时刻重排"两种平台语义下均收敛（e→0 或在量化档内振荡），无累积漂移、无追赶拍；
- clamp 下界 250ms 防忙转（异常情况下单步重排不得小于该值），上界 2000ms 限制单步补偿幅度；
- 单次 tick 处理超时（>1s）时，`while` 循环跳过被吃掉的槽位——补一次而非爆发多次（心跳语义 = "反映当前状态"，不是"补队列"）；跳过数记账并按 §8 记日志；
- 探针 E 实测（详见 §11.2）：伺服式对齐 330 tick（5.5 分钟）累计漂移 **+2.5ms**（要求 <1s；验收方独立复测 +7.2ms），单 tick 间隔 mean **1000.0ms**。

### 2.4 长耗时操作纪律：异步触发 + 单飞重入保护

- **HTTP 绝不在 tick 内同步执行**：余额刷新、节假日拉取都是 `async` 触发（fire-and-observe，非 fire-and-forget——完成回调统一回到 UI 线程后发事件/记账，异常必被观察）。
- **单飞（single-flight）重入保护**：全局 `_balanceInFlight` 标志，任一触发源要发起刷新时：若上一次未完成 → **跳过本次触发并 `_reentrySkips++`**（按 §8 记日志），不排队、不并发。理由：刷新结果只取最新（旧响应到达即过时），并发请求只会浪费与竞态。
- 节假日拉取由 HolidayService 自身缓存 + 全局门保证同日期单飞（D-12/D-13 移植），Scheduler 侧无需再加。
- 探针 E 实测：刷新挂起期间到期触发被跳过、`_reentrySkips` 恰 +1、不产生第二个请求（PROBE-E-REPORT 用例 C-4）。

---

## 3. 状态机与事件契约

### 3.1 状态定义

```
PeakState: Peak(高峰) | Idle(空闲)          ← 由 PeakService 按北京时刻重算（D-11 规则 2）
Degraded:  bool                              ← 节假日双源失败或退避窗口内为 true（独立于 Peak/Idle）
```

峰谷状态机本身只有两态、由时间唯一决定（无用户交互态）；Degraded 是快照上的**伴随标志**（UI 显示"数据离线"小图标，A1 约定 3），随每次快照重算更新。Degraded 变化**不发独立事件**，但其恢复动作（立即刷新快照）在 §6.4 定义。

### 3.2 事件契约（4 个，全部在 UI 线程触发）

| 事件 | 载荷 | 触发条件（且仅在此时刻） | 订阅者 | 订阅者动作 |
| --- | --- | --- | --- | --- |
| `StateChanged` | `(PeakState Old, PeakState New, bool Degraded, DateTimeOffset AtUtc)` | tick ⑤ 检测到 `New != Old` 的**翻转沿**（Peak→Idle 或 Idle→Peak；首 tick 初始化不发事件，直接记 `_lastState`） | ① TrayController：换图标（橙⇄绿）+ NIM_DELETE/NIM_ADD 重建 tooltip；② PanelViewModel：状态胶囊文案/配色、倒计时前缀切换 | 托盘换色与状态标签**只**由此事件驱动（探针 A 建议 A-2：订阅去重，同一翻转只刷一次） |
| `DayChanged` | `(DateOnly OldDate, DateOnly NewDate)` | tick ⑤ 检测到北京日期变化（跨天点 = UTC 16:00，D-14/G 套件） | Scheduler 内部（触发 §5 跨天流程）；PanelViewModel（可刷新日期相关展示） | 触发节假日缓存失效与新日期拉取（§5），随后重算快照（可能级联出 StateChanged） |
| `BalanceRefreshRequested` | `(BalanceRefreshSource Source, DateTimeOffset AtUtc)`；Source ∈ `Timer / Panel / Manual` | §4 三触发源之一命中且通过单飞检查（被单飞拦截的触发**不发**此事件，只记跳过） | BalanceService 执行层（探针 E 为 stub，正式版为 `BalanceClient.RefreshAsync`） | 发起 HTTP 刷新（异步），完成后回发 `BalanceRefreshed` |
| `BalanceRefreshed` | `(BalanceResult Result)`（成功/未配置/401/DNS/超时/畸形，D-07 分类） | 刷新完成回 UI 线程时（成功或失败都发） | PanelViewModel（余额区四态渲染）；Scheduler（成功→重置退避+更新 `_lastSuccessUtc`；失败→进入退避序列 §6.3） | 更新余额区与"上次刷新"行 |

补充契约：

- **线程封送**：心跳在 UI 线程 → `StateChanged/DayChanged/BalanceRefreshRequested` 天然在 UI 线程；`BalanceRefreshed` 在异步完成回调里，**必须经 `Dispatcher.BeginInvoke` 回 UI 线程后再触发**（探针 E 对 4 类事件逐一断言 `ThreadId == UIThreadId`，全部命中）。规则一句话：**事件只在 UI 线程发，订阅者无需自行封送**。
- **重入保护**：事件处理器不得同步回调 Scheduler 的触发接口（如 StateChanged 处理器里再点手动刷新）——由单飞标志兜底防死循环，设计上禁止。
- **每 tick 直刷不走事件**：倒计时/进度由 PanelViewModel 暴露的可绑定属性每 tick 直写（面板不可见时可跳过写入），避免 1Hz × N 属性 × 事件风暴。

---

## 4. 余额刷新调度

### 4.1 三触发源统一入口

```
RequestBalanceRefresh(source):
  if (_balanceInFlight) { _reentrySkips++; log(skip, source); return; }   // §2.4 单飞
  _balanceInFlight = true;
  RaiseBalanceRefreshRequested(source);        // UI 线程
  → 异步执行 RefreshAsync → 完成回调（回 UI 线程）:
      _balanceInFlight = false;
      RaiseBalanceRefreshed(result);
      成功: _retry.OnSuccess(); _lastSuccessUtc = now;  _nextTimerDue = now + BalanceInterval
      失败: _retry.OnFailure();  _nextTimerDue = now + _retry.CurrentBackoff   // §6.3
```

### 4.2 定时到期（source=Timer）

- 到期条件：`_nextTimerDue <= now`（now = ITimeProvider.UtcNow，**逐 tick 用绝对时间比较，不受系统挂起影响**——睡眠唤醒后首个 tick 立即补判，D-14 同源）。
- `_nextTimerDue` 初值 = Start 时刻（启动即安排一次定时刷新；正式版可改为"启动即刷一次再计时"，属阶段 4 参数选择，本设计两种均兼容——到期模型不变）。
- 间隔：`BalanceInterval`，默认 **30 分钟**，可配置范围 **5–1440 分钟**（下限 5 分钟防误配成高频轮询；上限 1440 = 一天一次）。配置在设置窗口（阶段 4），存 `%APPDATA%` JSON（D-20）。（需求变更 2026-10-02：该周期为**面板隐藏时**的后台节奏；面板显示期间改走固定 5s 快轮询，见 §4.6）
- 成功后 `_nextTimerDue = now + BalanceInterval`；失败后改写为 `now + CurrentBackoff`（§6.3）——退避窗口内定时**静默**（不触发请求，"窗口内静默"约束 5 的余额侧落地）。
- 同一窗口内只发一次：触发后立刻把 `_nextTimerDue` 推进到 `max(now + BalanceInterval, now + CurrentBackoff)`，即使该 tick 因单飞被跳过（在-flight 时到期重排同样执行），杜绝同一到期点重复触发。

### 4.3 面板打开即刷（source=Panel）+ 30 秒去抖

- 触发：面板从隐藏→显示的时刻（`Show()` 路径；探针 A 的 `TogglePanelViaTray` 显示半边）调用 `RequestBalanceRefresh(Panel)`。
- **去抖窗 = 30 秒**：记录 `_lastPanelTriggerUtc`，距上次 Panel 触发 <30s 的打开**不发**（记 debug 日志）。参数定值依据：默认刷新间隔 30 分钟的 1/60，足以覆盖"反复开关面板"场景（人工开关频率远低于 30s/次），又不会让用户在快速关开后看到超过 30s 前的旧数据；若距上次**成功刷新** <去抖窗也一并抑制（余额数据 30s 内视为新鲜）。探针 E 实测：30s 窗内连续两次开面板 → 恰 1 次 `Requested(Panel)`；第 31s 再开 → 第 2 次（用例 C-2）。
- 面板打开触发同样受单飞保护（在-flight 时跳过，余额到达后 `BalanceRefreshed` 自然更新 UI）。

### 4.4 手动按钮（source=Manual）

- A1 顶行刷新按钮点击 → `RequestBalanceRefresh(Manual)`，**立即触发、不去抖**（用户显式意图优先）；仅受单飞保护（刷新中点击 = 跳过，按钮态由 `BalanceRefreshed` 复位，A1 约定 4）。

### 4.5 未配置 Key（探针 B/正式版行为，调度层视角）

未配置 Key 时 `RefreshAsync` 不发请求、直接返回 `NotConfigured` 结果（映射 A1 约定 1 的"请先在设置中配置 API Key"）；调度层把它当作**普通完成**处理：发 `BalanceRefreshed(NotConfigured)`、`_lastSuccessUtc` 不更新（避免无 Key 时 30 分钟定时白白空转——到期检查前先看 Key 状态，未配置则定时到期**不触发**请求，仅日志记"未配置，跳过"）。

### 4.6 显示期快轮询（需求变更 2026-10-02）

- **触发源 `FastPoll`**：面板显示期间（`NotifyPanelVisibility(true)` → `_panelVisible`）余额刷新提到**固定每 5 秒**一次（`FastPollInterval`，固定策略不设设置项）。到期检查仍挂在 1s tick 记账逻辑（⑥），复用单飞保护——**不新建线程/定时器，心跳管线与伺服对齐零改动**。
- **面板隐藏**（`NotifyPanelVisibility(false)`）：快轮询完全停止，回到设定周期——`_nextTimerDue = (_lastSuccessUtc ?? now) + BalanceInterval`（锚点口径同 UpdateBalanceInterval）；快轮询退避序列复位（下次显示从 5s 重新起算）。
- **重新显示立即先刷一次**：复用 §4.3 `NotifyPanelOpened` 的 Panel 源与 30s 去抖——显隐抖动 / 30s 新鲜度窗内的重显**不放大请求量**；同时把 `_nextTimerDue` 钳到 `now + FastPollInterval`，保证去抖抑制时 5s 节奏仍然接管（抑制期间数据 ≤30s 旧且 ≤5s 内必刷）。
- **快轮询失败退避**：独立 `_fastRetry` 实例，序列 5→10→20→40→60s（60s 封顶循环，`FastPollBackoffSequence`）；成功恢复 5s 并清零。与后台 1/5/15 分钟 `_balanceRetry` **分实例不共享**（同一机制两份实例口径同 §6.1——显示期与后台的失败域节奏不同，互不放大）；失败沿用「失败不回退」UI 语义（D-07）。
- **与心跳解耦（不许退化硬标准）**：快轮询只改写 `_nextTimerDue` 并 fire-observe 异步触发（§2.4 单飞），任何阻塞/退避/失败都不进入 tick 同步路径——1s 心跳伺服对齐不受影响。
- **未配置 Key**：§4.5 定时静默口径不变（到期点仍按设定周期推进，不做 5s 空转；Key 保存走 keyChanged→Manual 即刷，成功完成后自然按当前节奏接管）。
- **设置窗口**：刷新间隔项文案同步改为「后台（隐藏时）刷新周期」语义，取值范围（5–1440 分钟）与持久化机制不变。

---

## 5. 跨天检测

### 5.1 检测规则

- 每 tick ⑤：`today = DateOnly.FromDateTime(BeijingTime.ToBeijing(now))`（`China Standard Time` 优先、固定 +08:00 回退，D-14）；`today != _lastDate` → 跨天。
- 跨天点 = **UTC 16:00**（北京 00:00），探针 C G 套件秒级验证；挂起跨过 UTC 16:00 的情形由"醒来首个 tick 重算日期"覆盖——检测基于绝对时间差，不依赖 tick 连续性。

### 5.2 跨天流程（一个 tick 内完成记账，网络部分异步）

```
跨天 tick:
  1. 旧日期 → 新日期记账，发 DayChanged(old, new)
  2. 节假日缓存：按日期键隔离（D-12），旧日期条目自然失效（无需主动删除；容量有界——
     缓存仅保留 [今天-1, 今天+400] 窗口，跨天时顺手清除窗外旧键）
  3. 新日期数据：调 HolidayService.GetDayDataAsync(newDate)
     → 命中前瞻缓存（23:59 前瞻已预取次日，探针 C E 套件）→ 同步返回，零请求
     → 未命中 → 走退避全局门（§6.2）异步拉取；本 tick 快照先按"工作日 + Degraded(若拉取失败)"计算
  4. 立即重算全天状态（新日期是否全天空闲）→ 可能引发 StateChanged（如工作日→节假日的翻转沿）
  5. 日志：DayChanged、重拉计数（§7.3 计数器基线）
```

探针 E 实测（用例 B）：周五 23:59:58 → 周六 00:00:02，`DayChanged` 恰 1 次；新日期（周六）拉取计数恰 +1 且后续 tick 不再增加（同日不重复，计数 stub 证明）；旧日期计数跨天后不变。

---

## 6. 退避与容错

### 6.1 RetryPolicy 复用（同一机制，两份实例）

复用探针 C 的 `RetryPolicy`（1min → 5min → 15min，封顶后保持 15 分钟循环；成功清零）：

| 实例 | 持有者 | 全局门语义 |
| --- | --- | --- |
| `_holidayRetry` | HolidayService（自探针 C 移植） | 失败后 `now + CurrentBackoff` 内 `GetDayDataAsync` 直接返回 null（Degraded），不消耗请求、不发起 HTTP |
| `_balanceRetry` | Scheduler（余额侧） | 失败后把 `_nextTimerDue` 改写为 `now + CurrentBackoff`（§4.2/§6.3） |

**两份实例、同一机制**：不为余额另造退避算法（约束 5"不另建退避机制"的落地）；也不共享同一实例——节假日与余额的失败域不同（不同 API、不同故障面），共享会互相放大退避。

### 6.2 节假日全局门（窗口内静默）

- 退避窗口内：快照重算对"未缓存日期"一律拿 null → `Degraded=true`，峰谷判定走本地星期逻辑（周一至五工作日 + 周末空闲，P3 常驻降级）；UI 按 A1 约定 3 显示"数据离线"小图标。
- 窗口期满后的首个需要该日期的 tick 自动重试（无需专门定时器——到期判断内嵌在 `GetDayDataAsync`）。

### 6.3 余额退避序列

- 余额刷新失败（401/DNS/超时/畸形，D-07 四类）→ `_balanceRetry.OnFailure()`；下一次**定时**到期 = `now + CurrentBackoff`（1/5/15 分钟序列），序列内定时静默。（需求变更 2026-10-02：该序列仅作用于面板隐藏时的后台节奏；面板显示期快轮询走独立的秒级退避序列 5→10→20→40→60s，见 §4.6）
- 手动/开面板触发**始终放行**（用户显式意图是天然恢复路径），其结果同样计入 `_balanceRetry`——失败延长退避，成功清零。这是"全局门"在余额侧的形态：**定时序列被门控，人工通道保持可用**（避免"失败后用户点刷新无响应"的反 UX）。
- 探针 E 实测（用例 D-1）：持续失败 stub 下实际尝试间隔 = 1min → 5min → 15min → 15min（Fake 时间步进验证，不真等）。

### 6.4 Degraded 恢复即刷（C-3，阶段 4 必须处理项）

- 退避成功（节假日或余额任一实例 `OnSuccess`）→ **立即触发快照重算**（不等下一 tick 的也行，但实现上取"下一 tick 前插队重算"或"当 tick 完成回调内重算"，二者等效——探针 E 取后者：成功回调内同步重算快照并更新 UI）。
- 恢复后的首次重算若改变峰谷结论（如 Degraded 期间误判的工作日恢复为法定节假日全天空闲）→ 自然走 StateChanged 翻转沿。
- 余额成功同理：`BalanceRefreshed(success)` 处理器内即刷新快照（余额区 + 上次刷新行立即更新，不等心跳）。
- 探针 E 实测（用例 D-2）：失败序列中注入成功 → `ConsecutiveFailures` 清零、快照重算计数 +1、UI 立即更新（PROBE-E-REPORT §2-D）。

---

## 7. 可测试性

### 7.1 ITimeProvider 抽象（自探针 C 移植，探针 E 扩展）

```csharp
public interface ITimeProvider { DateTimeOffset UtcNow { get; } }
public sealed class SystemTimeProvider : ITimeProvider { ... }        // 生产
public sealed class FakeTimeProvider  : ITimeProvider { ... }         // 测试
```

`FakeTimeProvider`（探针 E 实装）支持：

1. **手动步进**：`SetUtc(DateTimeOffset)` / `SetBeijing(DateTime)`（直接给北京墙上时间）/ `Advance(TimeSpan)`——确定性单步，用例 a/b/c/d 全部基于它；
2. **加速倍率**：`SpeedMultiplier`（如 60 = 虚拟时间 60× 流速）——用于"把 30 分钟压缩到秒级"的**流式**验证：虚拟时刻 = 锚点 + 真实流逝 × 倍率，配合真实 DispatcherTimer 心跳可在大加速下观察完整调度序列（探针 E 用例 C-1 的压缩验证走"手动大步长 + 逐 tick 断言"路径，倍率模式作为等价能力实装并冒烟）；
3. 步进语义约定：**先评估后推进**——tick n 的评估时刻 = 锚点 + (n−1)×步长（用例 a"11:59:58 起走 5 tick、第 3 tick 翻转"依赖此约定）。

### 7.2 依赖注入面

| 可注入项 | 生产实现 | 测试实现 |
| --- | --- | --- |
| `ITimeProvider` | SystemTimeProvider | FakeTimeProvider |
| 心跳/余额/去抖/退避/快轮询各间隔 | `SchedulerOptions`（默认值即 §2/§4/§6 数值） | 任意压缩值 |
| 节假日数据源 | 主/备 HTTP 客户端（探针 C 移植） | 计数 stub（per-date 拉取计数、可编程失败 N 次） |
| 余额客户端 | `BalanceClient`（探针 B 移植） | 恒成功/恒失败/TCS 受控挂起 stub |

### 7.3 计数器回归基线（沿用探针 C E/F 套件口径）

Scheduler 暴露只读计数器供断言（正式版进单元测试，探针 E 已按同口径回归）：

- 节假日：`FetchCount[DateOnly]`（每日期恰 1 次）、`FetchRounds` 总数；
- 退避：`ConsecutiveFailures`、尝试时刻序列（对照 1/5/15/15）、成功清零；
- 调度：`BalanceRequested[source]` 计数、`ReentrySkips`、`PanelDebounceSuppressions`、`DayChangedCount`、`StateChangedCount`。

---

## 8. 巡查日志（0 Key / 0 Authorization）

### 8.1 事件驱动 + 统计摘要（心跳不逐条记）

| 记什么 | 何时 |
| --- | --- |
| **统计摘要行**（tick 数、平均间隔、最大迟到、重入跳过累计、面板直刷次数） | 每 N tick 一次（N=60，即每分钟一行）+ Stop 时终值 |
| tick 漂移异常 | 单 tick 间隔越界（<0.5s 或 >1.5s）或迟到 >100ms 时逐条记（含实测值） |
| 状态翻转 | `StateChanged old→new (Degraded=…)` 逐条 |
| 跨天 | `DayChanged old→new` + 新日期拉取结果 逐条 |
| 余额调度触发/结果 | `Requested(source=…)` / `Refreshed(结果, 耗时ms)` 逐条；去抖抑制记 debug 级 |
| 退避进出 | 进入退避（含下次重试时刻）、序列推进、成功清零 逐条 |
| 重入跳过 | `skip source=… skips=…` 逐条 |

### 8.2 格式与脱敏

- 行格式：`yyyy-MM-dd HH:mm:ss.fff [LEVEL] [sess:<8hex>] 消息`——**会话 tag** 区分多次运行（探针 E 已实装，报告按 tag 切分）。
- **脱敏兜底（B-1 的探针版）**：日志写入层统一过 `SecretMasker`——正则匹配 `sk-` 前缀令牌、`Authorization: Bearer <token>`、长高熵赋值片段（`key=...`/`api_key=...`），命中即整段替换 `***MASKED***`。正式版按完整 Key 及常见片段做包含式匹配（阶段 4 必须处理项 B-1 的加强版）；探针 E 用假 Key 字符串注入验证兜底有效（用例 E：日志输出 0 明文）。
- 探针日志**0 Key、0 Authorization 头**（本探针无真实 Key；余额 stub 不携带敏感头，断言对全部日志行做形态扫描）。

### 8.3 落盘约定

- **正式版**：`%APPDATA%\DeepSeekBalanceWidget\logs\`（D-16；单文件形态不落 exe 旁——探针 D §3 教训）。
- **探针 E**：落工程目录 `probes/probe-e-scheduler/probe-e.log`（会话 tag 切分 real/sim 两次运行）。

---

## 9. 资源与限制预估（探针 E 实测校准）

### 9.1 系统计时器精度与 DispatcherTimer

- Windows 默认计时器分辨率 ~15.6ms（64Hz）→ DispatcherTimer 1s 间隔的单次触发迟到量 0~15.6ms 量级（探针 E 实测 max lateness 15.6ms 档，§11.2）；
- 量化迟到**不累积**的前提是 §2.3 绝对对齐；倒计时显示因此始终与真实时钟差 ≤1s（显示粒度 1s 本身就吸收了量化误差）。

### 9.2 CPU / 内存量级

- 单 tick 工作量 = 一次时区转换 + 少量 TimeSpan 运算 + （面板可见时）两个绑定属性写入 + 偶发事件 → **µs 级**；探针 E 实测单 tick 处理耗时 mean ≈0.09ms / max 0.20ms，浸泡稳态进程 CPU **1.04–2.29%·核**（tick 本身 ≈0.01%·核，差额为面板每 tick 文本直刷的 WPF 渲染与 H.NotifyIcon/运行时后台；阶段 4 通过"面板不可见时跳过直刷"进一步压缩，§3.2 已预留），工作集稳定 **~134MB 恒定**（200% DPI + 阴影特效口径），无增长趋势（§11.3；2026-09-29 探针 E 实测修订）。
- 挂起/睡眠：到期判定基于 ITimeProvider 绝对时间，唤醒后首个 tick 立即补齐（无需额外代码路径）。

### 9.3 漂移保证（对倒计时秒级显示的影响评估）

- **保证方式：每 tick 用绝对时间计算倒计时**（`下一切换点 − now`，剩余秒向上取整，D-08），不做任何"上次值 ±1"式累加——即使某 tick 晚到 100ms 甚至跳拍，下一 tick 显示值立即回到正确秒数；
- 累计漂移指标（心跳对齐质量，2026-09-29 探针 E 实测修订）：探针 E 实测 5.5 分钟 **+2.5ms**（验收方独立复测 +7.2ms，要求 <1s）；
- 结论：1Hz DispatcherTimer + 伺服式绝对对齐 + 绝对差值倒计时，**秒级显示精度在系统计时器原生能力内即可保证**，无需多媒体计时器（timeBeginPeriod）或专用线程。

---

## 10. 与阶段 4 的映射

### 10.1 放置目录

```
src/DeepSeekBalanceWidget/Scheduling/     ← 独立目录（final-technical-plan §3.1 已预留）
  Scheduler.cs                            本设计的 SchedulerCore（含 §2 管线 + §3 事件 + §4 余额调度 + §5 跨天）
  SchedulerOptions.cs                     全部间隔参数
  BalanceRefreshSource.cs / BalanceResult.cs（或并入 Balance/ 模块，阶段 4 定）
src/DeepSeekBalanceWidget/Infrastructure/ ITimeProvider.cs（探针 E 版直接移植，含 FakeTimeProvider——
                                          测试实现随产品源码走，供 tests/ 工程引用）
```

### 10.2 公开 API 草案（探针 E 已按此实装并验证）

```csharp
public sealed class Scheduler : IDisposable
{
    public Scheduler(ITimeProvider time, IPeakSnapshotProvider peak, IBalanceService balance,
                     SchedulerOptions options, ILogger log);

    public void Start();          // 启动心跳（DispatcherTimer）；首 tick 完成状态/日期初始化
    public void Stop();           // 停止心跳；输出统计摘要终行

    // 事件（全部 UI 线程触发）
    public event EventHandler<StateChangedArgs>?      StateChanged;       // 翻转沿
    public event EventHandler<DayChangedArgs>?        DayChanged;         // 北京日期变更
    public event EventHandler<BalanceRequestedArgs>?  BalanceRefreshRequested;
    public event EventHandler<BalanceRefreshedArgs>?  BalanceRefreshed;

    // 三触发源的显式入口（面板/设置按钮调用；Timer 源由内部到期产生）
    public void RequestBalanceRefresh(BalanceRefreshSource source);       // 内含单飞+去抖
    public void NotifyPanelOpened();                                      // = RequestBalanceRefresh(Panel) + 30s 去抖

    // 可观测性（回归断言用，§7.3）
    public SchedulerCounters Counters { get; }

    // 探针专用（正式版删除）：注入状态覆盖以驱动翻转沿（real 模式换色验证）
    public void SetStateOverrideForProbe(PeakState? state);
}
```

阶段 4 装配：`App.OnStartup` 构造 `Scheduler` → `Start()`；`TrayController` 订阅 `StateChanged`；`PanelWindow` 订阅其余；`NotifyPanelOpened` 接入探针 A 的面板显示路径；退出清理链中 `Stop()` 先于托盘释放（A-3 顺序）。

### 10.3 阶段 4 实现检查单（来自本设计 + 探针 E 经验）

1. DispatcherTimer 必须实现绝对对齐（§2.3 伺服式）——朴素 Interval=1s 会累积漂移（实测 0.03–0.85 s/分钟逐轮波动；2026-09-29 探针 E 实测修订）；
2. 4 类事件全部在 UI 线程触发（`BalanceRefreshed` 完成回调 `Dispatcher.BeginInvoke`）；
3. 余额失败改写 `_nextTimerDue`（1/5/15 序列），定时静默但手动/面板通道保持放行；
4. 面板打开去抖 30s（含"距上次成功刷新 <30s"抑制）；
5. 跨天流程：DayChanged → 缓存窗口清理 → 新日期拉取（经全局门）→ 立即重算；
6. 日志经 SecretMasker、每 60 tick 统计摘要、`%APPDATA%` 落盘；
7. `SetStateOverrideForProbe` 探针后门不得带进正式版；
8. 显示期快轮询（需求变更 2026-10-02，§4.6）：`NotifyPanelVisibility(true/false)` 接入面板显隐回调切换节奏；快轮询退避独立 `_fastRetry` 实例（5→10→20→40→60s）；触发只挂 1s tick 记账逻辑，心跳管线零改动。

---

## 11. 探针 E 实测结果摘要（2026-09-29 实测修订；详见 probes/probe-e-scheduler/PROBE-E-REPORT.md）

> 以下数字为 2026-09-29 在本机（Win11 26200，3200×2000@200%）实机运行结果；real 模式为真实时钟连续浸泡（朴素对照 90 tick + 伺服对齐主浸泡 330 tick，≈7 分钟），sim 模式为 Fake 时钟注入的全事件逻辑用例。本节原为设计期预写数字，已按 PROBE-E-REPORT §6 登记项替换为终跑实测值（2026-09-29 探针 E 实测修订）。

### 11.1 用例结论总表（实测引用：PROBE-E-REPORT §2/§3）

| 组 | 用例 | 实测结论（终跑） | 关键数字（实测） |
| --- | --- | --- | --- |
| real | R1 心跳浸泡 ≥300 tick | PASS（报告 §2-R1） | 330 tick，间隔 min/mean/max = 970.1/**1000.0**/1026.1ms；σ=8.8ms |
| real | R2 累计漂移 <1s | PASS（报告 §2-R2） | 对齐段 **+2.5ms**（验收方独立复测 +7.2ms）；对照朴素段 89 tick +44.1ms |
| real | R3 倒计时每秒正确 | PASS（报告 §2-R3） | 330 快照格式全对；逐对递减违规 0；去重值 330 |
| real | R4 CPU/内存抽样 | PASS（报告 §2-R4） | 稳态 CPU 1.04–2.29%·核；工作集 133.7MB 恒定 |
| real | R5/T1/T2 翻转换色 + 零异常 | PASS（报告 §1/§2） | ICO 像素橙→绿→橙；4 类事件全 UI 线程；Dispatcher/AppDomain/未观察 Task 异常均 0 |
| sim | A 状态翻转恰一次 | PASS（报告 §3-A，7/7） | 第 3 tick（12:00:00）Peak→Idle 恰 1 次；ICO 换绿；标签同步 |
| sim | B 跨天恰一次 | PASS（报告 §3-B，4/4） | DayChanged ×1；per-date 拉取 周五/周六/周一 各 1、总计 3 |
| sim | C1 定时到期压缩验证 | PASS（报告 §3-C1，4/4） | 30min 到期恰 1 次（source=timer） |
| sim | C2 面板去抖 | PASS（报告 §3-C2，3/3） | 30s 窗内 2 次开面板 → 恰 1 次；31s 后放行第 2 次 |
| sim | C3 手动立即触发 | PASS（报告 §3-C3） | Requested(Manual)=1，当 tick 即发 |
| sim | C4 重入保护 | PASS（报告 §3-C4，4/4） | 在飞到期跳过，ReentrySkips=+1，无第二请求 |
| sim | D1 失败退避序列 | PASS（报告 §3-D1，3/3） | 尝试时刻 10:00→10:01→10:06→10:21，间隔恰 1/5/15min |
| sim | D2 成功清零即刷 | PASS（报告 §3-D2，3/3） | ConsecutiveFailures=0、快照重算 +1（C-3） |
| sim | E 日志脱敏兜底 | PASS（报告 §3-E，4/4） | 假 Key 三形态注入 → 全日志 0 明文；0 Authorization |
| sim | F 加速倍率 | PASS（报告 §3-F） | 60× 冒烟：真实 5.0s → 虚拟 5.02min |

> 实测说明（报告 §6-4）：首轮 real（sess:547740aa）R1/R2/R3/R4 曾 FAIL，根因为 §2.3 原公式与 WPF 重排语义冲突（§2.3 已修订）；伺服式修复后终跑全 PASS。

### 11.2 心跳精度实测（real 模式，2026-09-29 探针 E 实测修订）

| 指标 | 伺服对齐模式（本设计，330 tick） | 朴素模式（对照，90 tick） |
| --- | --- | --- |
| 单 tick 间隔 min / mean / max | 970.1 / **1000.0** / 1026.1 ms | 989.3 / 1000.5 / 1025.4 ms |
| 间隔 σ | **8.8 ms**（验收方独立复测 σ=12.5ms） | 7.7 ms |
| 对齐槽位最大迟到 | 28.9 ms（≈2 个系统量化档） | —（无槽位概念） |
| **5 分钟级累计偏差** | **+2.5 ms**（验收方独立复测 +7.2ms，要求 <1s） | +44.1ms@90tick，折合 **0.03–0.85 s/分钟逐轮波动**（随运行时长线性累积，不可接受） |
| 单 tick 处理耗时 mean / max | **0.09** / 0.20 ms | 0.38 / 21.33 ms（含首轮 JIT） |

### 11.3 资源占用（real 模式，每 30s 抽样；2026-09-29 探针 E 实测修订）

| 指标 | 实测范围 | 结论 |
| --- | --- | --- |
| 进程 CPU（TotalProcessorTime 增量/墙钟） | 浸泡稳态 **1.04–2.29%·核**（启动段 6.14% 与收尾分析突发不计；tick 本身 mean 0.09ms ≈0.01%·核，差额来自面板每 tick 文本直刷的 WPF 渲染与 H.NotifyIcon/运行时后台） | 心跳开销可忽略；阶段 4 以"面板不可见时跳过直刷"进一步压缩 |
| 工作集 | **133.7 MB 全程恒定**（200% DPI + 阴影特效口径），零增长 | 无泄漏迹象 |
| 未处理异常 | Dispatcher=0 / AppDomain=0 / 未观察 Task=0 | 零崩溃 |

### 11.4 观察与限制（如实记录）

- max lateness 15.6ms 与系统计时器量化吻合；未见 >1 个量化档的异常迟到（本机前台空闲场景）。
- 系统睡眠/高负载行为**未做破坏性测试**（探针机不睡眠；未人为制造 100% 负载挤压）——对齐机制的"跳槽位追赶"路径已实现并单元可测，但真实睡眠唤醒下的补判行为标注为阶段 4/6 观察项。
- sim 模式的翻转/跨天用例走真实 DispatcherTimer 心跳（Fake 时钟 1s/tick 步进），余额/退避用例走手动 Step（精确控时）——两条路径共用同一 OnTick 核心，均已在 UI 线程断言事件封送。

---

*本文档为阶段 3 唯一新建 docs 产出物。阶段 4（正式实现）以本文档 + `docs/final-technical-plan.md` 为直接输入；探针工程 `probes/probe-e-scheduler/` 归档不进正式代码库。*
