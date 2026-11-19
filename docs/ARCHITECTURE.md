# Codex / Claude / Antigravity 多智能体指挥舱（ADE）统一架构

## 2026-11-17：.NET 10 原生内核 + WebView2 双视图指挥舱

`MainWindow` 采用无边框 `WindowChrome` 宿主双 `WebView2` 实例：主栏 `ShellWebView` 通过 `WebUiResourceServer` 在虚拟源 `https://app.codexmultilauncher.local` 直接提供编译进单文件的嵌入式前端资源（包含四套 CSS 变量视觉主题与本地打包的 `xterm.js`），零本地端口占用、零外部散落文件；副栏 `PreviewWebView` 按需展开，承载 Design Mode 实时网页预览与 `DesignModeInspector` 元素抓取（提取选择器、HTML、计算 CSS 及 `CapturePreviewAsync` 视口截图，直接注入任务输入流）。`WebUiBridge` 提供双向类型化 JSON-RPC 与 60ms 节流的流式事件推送，前端采用持久化容器增量渲染，确保后台轮询绝不打断输入焦点或终端会话。

`AccountPoolService` 聚合全部受管与原始 Codex 工作区的实时配额快照与限流冷却窗口。当启用自动故障转移（Auto-Failover）时，`GroupChatCoordinator` 与 `ArenaService` 在预检超阈值或运行返回 429/配额错误时，自动按健康度与剩余配额选择池内最佳备用账号，在同一工作目录与共享记录中无缝接力，并在消息上记录换号来源。`MultiEngineChatRunner` 按绑定工作区类型将请求分发至 `CodexGroupChatRunner`（`CODEX_HOME`）或 `ClaudeCliRunner`（`CLAUDE_CONFIG_DIR`），统一支持 `read-only` 与 `workspace-write` 两种官方沙箱级别，同时仅将已清洗的步骤元数据（推理、命令摘要、文件变更数）暴露给 UI 步骤折叠轨，不将原始诊断流混入共享对话上下文。

`GitWorktreeService` 在受包含校验保护的 `data/worktrees/` 下管理独立任务与赛马分支（`git worktree add -b`），提供含未跟踪新文件预览的 Unified/Split 结构化 Diff 解析、行内批注提示词组装及主仓库干净状态检查后的 `--no-ff` 分支合并。`ArenaService` 并行调度 2~6 个跨账号或跨模型选手在各自 Worktree 独立实现同一需求，采集变更统计与可选验证命令退出码。`ConPtyTerminalService` 通过原生 `CreatePseudoConsole` 与 `STARTF_USESTDHANDLES` 空句柄绑定创建伪终端，注入选定账号的隔离环境变量块，与前端 `xterm.js` 双向流式同步。

## 群聊工作台

主窗口通过群聊与客户端管理两个标签保留已有功能。`GroupChatCoordinator` 将显式 @ 别名解析为固定的工作区 ID；每一批回复在添加占位消息前构建共享上下文快照，不把未完成、失败或已取消回复发送给其他身份。每个身份调用独立 `IGroupChatRunner`，同群并行、跨群独立取消；关闭窗口取消所有群聊自己的进程并等待完成。账号重绑定不改变既有消息的身份与账号来源记录。

`CodexGroupChatRunner` 使用真实安装的官方 CLI、明确的账号主目录、UTF-8 stdin 和 JSONL stdout，不复用会丢弃通知的只读用量连接。子进程禁用插件、使用只读沙箱与临时会话，不使用审批或 hook 信任绕过参数；清理只作用于该次创建的 `Process`。仅 `agent_message` 进入共享记录，推理事件、工具输出和 stderr 不转成聊天内容。已完成回复后排空输出并有界等待退出；取消、协议错误和超时回收该次进程树。

`GroupChatStore` 原子保存版本化 JSON，校验别名、重复 ID、数据大小和枚举值，并在访问前拒绝受管文件祖先的链接/联接。原文件格式损坏时不重置。进行中的消息恢复为 Interrupted 状态；没有自动重试或后台模型调用。聊天记录不包含凭据，且所在数据文件已被 Git 忽略。

测试覆盖双身份并发与账户映射、共享历史传播、单群停止不影响其他群、关闭回收、损坏记录保留、实际受控子进程的正常/取消/无效 JSON/坏 UTF-8/失败路径，以及真实 CLI 参数解析。布局测试中的对话是静态测试示例，不是模型实测结果。

## 客户端适配

`ClientKind.Antigravity` 追加在既有枚举值之后，保留旧 JSON 的 Codex/Claude 语义。`AntigravityAgentRunner` 严格采用 Google 官方公开的 `agy acp`（Agent Client Protocol，基于 `stdio` 的 JSON-RPC 2.0：`initialize` → `session/new` → `session/set_config_option` → `session/prompt` → `session/update` / `session/request_permission` / `session/cancel`）标准协议通信，绝不读取或提取 OAuth 令牌，也不调用未公开私有接口或注入 `headless` 渲染伪装。主窗口按客户端类型维护监控器字典，各平台的实例关联文件独立。Antigravity 目录同时参与路径所有权与跨平台误认领拒绝，原始工作区使用固定 ID `original-antigravity`，不注入受管环境。

`AntigravityInstallation` 从标准目录与 Windows 卸载注册项定位桌面应用，核对可执行文件名、Google 产品元数据和桌面资源布局；不使用 shell/PATH 解析，不把此检查表述为数字签名认证。启动直接使用精确可执行路径与 `ProcessStartInfo.ArgumentList`，不使用 MSIX 包激活。受管数据路径要求同一账号下的 `electron-user-data`、`antigravity-home`，并拒绝重解析点。后者通过子进程环境中的 `USERPROFILE`、`HOME` 与 `ANTIGRAVITY_GEMINI_DIR` 隔离 Electron 的主目录派生文件及原生后台 `.gemini` 状态；每个账号的 `ANTIGRAVITY_CREDENTIAL_SCOPE` 也不同。后台启动能力检查流式扫描已安装二进制的两个原生环境接口标记，不读取账号或系统凭据；接口语义仍受上游版本约束。

合约验证三平台混合持久化、跨平台误认领拒绝、同账号目录边界、原始环境不覆盖、不同凭据作用域、父进程环境不变、安装元数据/图标路径解析、能力标记跨块读取和 Codex 配置服务拒绝。`-VerifyAntigravityDesktop` 验收只创建两个未登录临时账号，确认独立主窗口及原生数据落盘后，按 PID、启动时间和精确路径回收自身进程树。它不验证真实账号的认证刷新或长期稳定性。

`Profile.Client` 与 `CodexWorkspace.Client` 区分 Codex、Claude；缺失的旧字段默认为 Codex。已有类名保留以兼容原有调用。`WorkspaceCatalog` 对不同客户端执行同一套路径所有权检查，拒绝将 Claude 进程认领到 Codex 目录或反向认领。每类客户端有独立的 `CodexInstanceMonitor` 和实例关联文件，UI 合并展示并按选中账号筛选；其中一个客户端未安装或发现失败不影响另一个。

`ClaudeInstallation` 只接受当前用户注册的 `Claude_pzs8sxrjxfjjc` x64 主包，选择最高版本并从注册安装路径定位 `app/Claude.exe`。启动共用包激活、调试恢复助手和临时环境清理逻辑，Claude 使用独立互斥锁和 AppUserModelID。受管 Claude 仅注入同账号 `CLAUDE_CONFIG_DIR`，不注入 Codex 数据或 USERPROFILE；桌面数据由 `--user-data-dir` 指定。Claude 账号不会创建 Codex 的配置和运行时缓存，后台 Codex 配置写入器与用量查询也会拒绝 Claude 工作区。

Claude 原始路径识别覆盖迁移后的 LocalAppData/Claude-Data、MSIX LocalCache/Roaming/Claude 与普通 AppData/Claude，未知外部目录不开放启动和用量。代码不读取或复制 Claude 登录令牌。合约检查覆盖旧配置兼容、混合账号持久化、跨客户端误认领拒绝、环境与存储边界、包发布者校验和 Codex 配置写入拒绝；显式 `-VerifyClaudeDesktop` 验收在临时目录启动两个未登录窗口，复核进程身份与目录后仅回收自身新建的实例。

## 身份边界

`PackageFullNameSelector` 只选择当前用户注册的合法 `OpenAI.Codex` x64 主包。`OfficialCodexInstallation` 通过该包的注册安装路径构造唯一允许的 `app\ChatGPT.exe` 路径；发现与控制不接受仅名称相同、包外、错误架构或旧包路径的进程。

每个 `Profile` 由受路径包含校验保护的 `codex-home`、`electron-user-data` 和 `runtime-user-home` 派生；`PrimaryRuntimeInstallRoot` 位于运行时主目录的 `.cache/codex-runtimes`。`WorkspaceCatalog` 将受管工作区、确认的默认原始工作区和未解析外部工作区统一为一个模型，并仅向受管工作区传递专属运行时主目录；路径所有权跨 Electron 与 `CODEX_HOME` 去重，受管精确匹配优先。`config.toml` 首次创建采用 CreateNew，已有配置只由显式沙箱及运行时市场迁移契约更新；管理路径在访问前拒绝任一祖先的重解析点。代码不读取、复制、解析或迁移 `auth.json`，也不会删除历史用户数据。

## 启动和发现

`PackagedCodexLauncher` 仍使用包激活 API 和有界跨进程互斥锁，在成功或失败后清理临时 PackageDebugSettings 环境。启动器不直接执行 WindowsApps 文件，也不改变官方包内容或 ACL。

`PackageEnvironmentBlock.CreateValues` 是真实包激活环境的生成边界，保留三个绝对的 Codex 数据路径覆盖；仅在 `WorkspaceDataPaths.RuntimeUserHome` 非空且经过同账号路径验证时追加 `USERPROFILE`。相对目录、另一个账号或相邻伪前缀目录以及运行时根的重解析点会被拒绝。原始工作区不追加这一覆盖。`Create` 从相同值生成排序且双 NUL 终止的 UTF-16 PZZ，环境仅经 `PackageDebugSettings` 传给目标进程，不使用全局 `Environment.SetEnvironmentVariable`。

`CodexInstanceMonitor` 由两秒 DispatcherTimer 请求后台发现；同一发现未完成时复用该任务。进程快照、命令行和资源采样在后台完成，不可变测量结果回到 UI 线程后才更新集合。进程快照的文件名只用于减少查询；精确官方路径仍是最终身份依据。它使用 Toolhelp32Snapshot 读取 PID/父 PID，再以精确路径验证官方根进程。官方根进程是官方 `ChatGPT.exe`，且不存在同一官方文件、启动时间不晚于它的父进程；拒绝已重用父 PID 的时间倒置；实例树只包含连续的官方可执行文件子孙，遇到非官方子进程立即停止，绝不跨越该边界。`ProcessCommandLineReader` 使用有界的 `NtQueryInformationProcess` 读取命令行，绝不通过 WMI 或 shell。每个树里所有可发现的 `--user-data-dir` 值必须规范化为同一目录，否则工作区身份为空且旧 PID 关联被删除。

启动请求携带 UTC 请求时刻。只有根启动时间不早于请求、并在 `PendingLaunchAssociationWindow`（30 秒）内被发现时，才会建立归属；这拒绝包激活返回的请求前既有 PID。`ManagedInstanceStore` 持久化启动器归属的 `ProfileId`、根 PID 和根启动时间，但只在用户数据路径暂不可读时用作恢复线索。读入损坏或不可访问的文件时降级为空关联，并通过 `LastWarning` 让 UI 呈现警告；保存失败同样保留运行实例、设置可观察警告，不伪称归属已持久化。经验证的当前用户原始路径对是唯一可持久化的发现工作区；自定义外部路径没有猜测的 `CODEX_HOME`，不能启动或读用量。

## 指标与控制

`TrackedCodexInstance` 是实例树记录，不是单个 `Process` 缓存。树成员保存 PID 与启动时间；每次采样先复核每个成员的当前启动时间，才聚合 `TotalProcessorTime` 和工作集。CPU 只在两个连续、有效的聚合样本之间报告，并按逻辑处理器数归一化；树变化造成的样本不连续会显示不可用。窗口可用性只来自已验证官方根进程自己的主窗口。退出或不可访问状态保留在表中，定时刷新不会把进程访问异常抛入 UI。

所有控制操作都会再次验证根身份。定位、平铺、最小化和正常关闭都只作用于已验证根的 `MainWindowHandle`，绝不遍历后代 PID 的窗口；正常关闭发送 `WM_CLOSE`；强制结束需 UI 二次确认后调用 `Kill(entireProcessTree: true)`。监控器 `Dispose` 和启动器关闭只释放自身资源，绝不关闭官方客户端。

## UI 和验证

主窗口显示统一工作区侧栏、启动/切换、数据目录、只读用量和官方客户端控制表。选中工作区后，表格和所有运行时汇总严格筛选到该工作区。只读用量客户端仅在当前用户的 `%LOCALAPPDATA%\OpenAI\Codex\bin` 下选择不含重解析点的 `codex.exe`，设置该工作区的 `CODEX_HOME` / `CODEX_SQLITE_HOME`，并仅调用初始化及三个 `account/*/read` 方法；超时、取消或释放只终止它启动的进程树。

离线合约测试覆盖官方精确路径的接受和拒绝、包选择、带成员身份的进程树归组、请求时间窗口的启动关联及 PID 重用拒绝、以及监控器释放不终止进程。真实安装包、窗口权限和实际桌面布局属于 Windows 集成验收。

## 2026-10-05：桌面运行时缓存与用户主目录边界

现场应用日志确认，常驻实例分别要求运行时版本 `26.904.11930` 和 `26.930.11008`，成功安装及验证都指向真实用户的同一个 `.cache/codex-runtimes/codex-primary-runtime`。多次安装在相邻几分钟内交替完成，定时安装开始通常相隔约一小时。这支持共享目录的版本争用解释；历史 DNS 重连失败不能单独解释大量下载，安装耗时也包含解压和验证，不能用来计算网络速度。

当前生产客户端的归档运行时根由 `os.homedir()` 派生。开发模式的运行时环境开关不能作为生产路径契约，另一套 MSIX 运行时机制还依赖未验证的远程分发配置。本次通过新受管桌面进程的专属 `USERPROFILE` 改变这一派生路径，保持既有 Codex、SQLite 和 Electron 数据路径，且不启用这些未验证开关。`ProfileService` 幂等创建专属主目录及缓存根，不读取或搬运原用户文件，不改写已有账号配置。`ProfileStorageInspector` 分别计量 Codex、Electron、运行时三个区域，合计不包含其他账号或相邻文件，扫描不会跟随联接点。

该隔离具有明确的兼容影响：继承桌面环境的工具可能改变 `~`、SSH、Git 等用户配置的查找位置；显式的 `APPDATA`、`LOCALAPPDATA`、`TEMP`、`HOME` 环境变量保持原值，但部分 Windows 已知文件夹 API 会按新 `USERPROFILE` 展开路径模板，因此不同工具的主目录来源可能不同。现场 .NET 的 LocalApplicationData 查询已得到隔离目录中的 AppData，而 `LOCALAPPDATA` 环境仍为真实用户路径；测试和维修工具从隔离客户端启动时要在其自身进程树恢复真实用户环境，以复现正常启动器的运行方式。用户需要时可以为工具指定明确的路径，启动器不会复制 `.ssh`、`.gitconfig` 或凭据来补偿。后台 `CodexAppServerSession` 继续继承启动器的真实用户环境，仅覆盖选定账号的 `CODEX_HOME` / `CODEX_SQLITE_HOME`。原始工作区保持默认环境。已有客户端须在任务结束后重新启动才能读取新的进程环境；升级不结束现有任务，也不删除共享旧缓存。

每个账号首次仍可能独立下载运行时，正常升级也产生流量。离线合约使用临时双账号及中文、表情路径验证实际 UTF-16 PZZ、父进程与后台查询环境不变、原配置字节保留、路径越界和临时联接点拒绝、各账号存储独立。联接测试只在明确验证过的临时夹具内创建和移除联接，不访问真实用户数据。这些检查证明启动器的路径与环境边界，实际客户端采纳路径及下载量需要单独实机验证。

若提供 `CODEX_MULTI_LAUNCHER_TEST_NODE`，合约还会使用明确指定的既有 Node 可执行文件与 `CreateValues` 返回的同一组环境值，运行一个有二十秒预算的 `os.homedir()` / 运行时派生路径探测。未提供时明确跳过，不新增下载或外部依赖；探测不会安装运行时或执行模型任务。

现场回归还发现旧 `config.toml` 注册的 `marketplaces.openai-primary-runtime.source` 仍指向全局缓存，官方桌面在新目录安装完成后调用 `marketplace/add` 因同名不同来源而失败。`WorkspaceRuntimeMarketplaceService` 仅在冷启动受管账号时迁移准确匹配 Windows 已知用户目录下旧全局运行时路径的 `source_type=local` 注册；Windows `\\?\` 前缀先规范化，再做完整目录相等比较。未注册、已迁移及自定义来源不会被改写。已安装的新市场清单须有正确名称，目标及祖先重解析点拒绝；首次目录不存在时允许写入固定的未来安装路径。

迁移使用现有关闭插件启动任务的 app-server 会话，以精确用户层版本执行 `config/value/write`，只写 `marketplaces.openai-primary-runtime.source`。官方注册比较规范路径的完整字符串，因此用目录元数据句柄和 `GetFinalPathNameByHandle` 取得现有目标或最近祖先的真实大小写、展开短路径别名，保留 Windows verbatim 前缀，再拼接尚不存在的固定目录。先保存原配置字节，随后验证用户层与有效来源一致。没有 `marketplace/remove`、插件卸载或缓存删除操作，插件启用和其他市场保持原值。层覆盖、并发版本冲突、超时或验证失败均停止该次新启动；定位已存在实例时不执行这项迁移。真实临时账号回归使用不同大小写的账号路径验证目标不存在仍可迁移、配置仅有此值变化、备份完整、账号隔离及幂等性，随后仅在临时目录创建空市场清单，验证官方 `marketplace/add` 返回 `alreadyAdded=true`。

## 2026-09-06：沙箱生命周期与异步更新契约

现场证据：默认 home 与受管账号 1 的日志在 00:03–00:05 交替报告 sandbox users missing or incompatible with marker version；两份 marker 都指向 CodexSandboxOffline / CodexSandboxOnline。两个 home 独立保存状态，但系统用户不是独立资源。

`WorkspaceSandboxService` 在受管配置边界选择官方 restricted-token (`unelevated`) 实现。新账号从该配置开始；已有账号用官方 `config/read` 取得精确文件的 user 层及版本，先读 `configRequirements/read` 拒绝不允许的模式，再保存原配置备份，通过 `config/value/write` 定点写一个键，检查 write status 和再次读取的有效值。其网络隔离强度低于 elevated，UI 与 README 明示该语义。不会降级任务审批、设置完整访问、关闭私有桌面、重写官方程序或共享沙箱凭据。运行中的客户端须自行重启读取新配置；后台检查不关闭任何官方实例。

`CodexAppServerSession` 统一拥有启动、握手、顺序请求、协议错误和进程树回收。工作目录绑定所操作的 CODEX_HOME，避免继承启动器项目设置。配置迁移最长 20 秒；账号查询最长 12 秒；退出确认最长 2 秒，这些分别是用户交互等待预算，不是正确性容差。超时不重试。配置版本冲突直接拒绝，不覆盖他人的新编辑。账号缓存按规范化 home 区分，保鲜 30 秒；同一客户端请求经过一个异步信号量，取消不进入缓存。可选用量端点的 -32601 是明确的旧协议兼容契约，其他错误不能伪装成完整成功。

`WorkspaceRefreshGate` 在 UI 线程维护当前请求与各工作区完成时间。相同工作区的时钟滴答不能取消进行中的请求；切换工作区或退出才取消。旧请求成功、异常和 finally 均不能清除或更新新请求。存储 20 秒与用量 60 秒是显示数据的保鲜时间，以完成时刻开始；手动刷新和重新选择账号可要求刷新。退出等待被取消的后台操作完成回收，再结束启动器。

指标仅在整个采样树有效时标为可用；中断后清除 CPU 基线、工作集和窗口可用性。数值变化必须同时通知对应格式化属性。发现失败产生可见错误并禁止把“无法发现”当作“没有实例”启动第二份客户端。原始工作区持久化失败只失去持久化能力，不丢弃已验证运行实例。

定位仅在最小化时恢复，保留最大化窗口；平铺使用目标显示器原生工作区坐标，并禁止抢前台或改 Z 序。包恢复助手同时验证线程的归属 PID。每个启动器数据目录的命名互斥锁禁止两个新版本窗口并发写配置清单；包激活助手不持有该锁。包激活未完成时拒绝关闭启动器，以保证临时调试环境清理完成。

## 2026-09-07：JSONL 的字节编码契约

故障在进程通信边界：app-server 输出 UTF-8，而未指定编码的 Windows 重定向流可能采用 CP936。UTF-8 中文字节在该代码页下会与其后的反斜杠组合，破坏 JSON 字符串边界，导致 `Expected ':'`，因此失败位置随配置内容变化。业务配置本身可以是合法的 TOML 与 JSON。

`CodexAppServerSession.CreateStartInfo` 统一设置 stdin/stdout 为无 BOM、严格 UTF-8；stdout 非法字节明确作为协议错误拒绝，不尝试修补 JSON 或切换代码页重试。stderr 仅排空诊断文本，明确采用 UTF-8，坏字节仅影响未参与协议判断的诊断字符。回归检查复现 CP936 对中文 Windows 路径的破坏，同时确认 UTF-8、转义引号、表情及非法字节的接受/拒绝边界；真实 app-server 在带中文、空格与表情的临时 home 迁移包含中文项目路径的配置，并逐项验证路径值和精确备份。

验证与发布可按变更需要重复执行，不再施加全机构建互斥或仅一次验收限制。每次发布使用独立候选目录，只有完整验证成功后才用于替换便携版。

## 2026-09-07：stdio 查询进程的正常退出

后续现场报告：刚打开启动器、尚未激活官方客户端时出现 git.exe 0xc0000142。单独验证系统 Git 和附带 Git 的版本查询及仓库探测均正常，当前可用内存充足。不能仅凭错误码断定 Git 安装损坏。检查发现启动器在每次成功查询后也立即 Kill 整个 app-server 进程树，这是可确认的生命周期缺陷，会打断服务的后台子进程；与此弹窗的直接因果关系尚无进程跟踪证据。

官方 stdio 服务把输入 EOF 作为连接结束并执行清理。实机只读配置查询关闭 stdin 后约 0.17 秒正常返回退出码 0。Session 因此先发送 EOF，同步排空后续 stdout/stderr，以免输出反压阻止退出；只有已有两秒退出交互预算耗尽才终止自己创建的进程树，再用两秒确认退出和管道排空。stderr 错误与进程退出不可用情况仍明确传播，实际正常退出码与是否强制结束可供合约验证。检查覆盖真实 app-server 自然退出、故意不响应 EOF 的子进程、已退出子进程、超出管道缓冲区的尾部输出及非法 UTF-8 后清理。

继续集成验证确认后台额外启动了 `git ls-remote https://github.com/openai/plugins.git HEAD`，正常结束根进程后仍会占用管道和临时 home。官方 `core-plugins/src/manager.rs` 中 `maybe_start_plugin_startup_tasks_for_config` 在 `plugins_enabled` 为 true 时启动市场同步线程，因此仅调整退出顺序不足以满足短暂查询的生命周期契约。最终在查询进程的启动边界加官方 `--disable plugins` 参数，阻止与配置/用量查询无关的插件启动任务。它不属于账号配置迁移，不持久化，不传给包激活的客户端。真实集成在 user 层保留 `features.plugins = true`，确认查询进程有效值为 false、原插件配置仍为 true、退出无强杀且退出码为 0。
