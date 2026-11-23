// ============================================================================
// AI Multi Launcher · Portal Links & Comprehensive Bilingual (ZH / EN) Engine
// ============================================================================
// 【独立站引流配置入口】：当你搭建好独立站/文档站后，只需修改下方两个链接即可将软件内自然流量导入你的站点：
// 1. websiteUrl: 左侧导航栏「官网 / Docs」按钮点击后打开的官方主页或文档站地址
// 2. apiGuideUrl: 添加智能体弹窗内「获取 API Key 与算力配置指南 ↗」点击后打开的页面地址

export const PORTAL_CONFIG = {
  websiteUrl: "https://thalesnottychus.github.io/Hyperion/",
  apiGuideUrl: "https://thalesnottychus.github.io/Hyperion/#playbooks",
  showDocsNavButton: true,
  showApiGuideLink: true,
};

let currentLang = (() => {
  try {
    return localStorage.getItem("ade_ui_lang") === "en" ? "en" : "zh";
  } catch {
    return "zh";
  }
})();
let observerInstalled = false;
let isApplying = false;

const CJK_RE = /[\u4e00-\u9fff]/;

// Tier 1: Exact UI Labels, Headings, Buttons, Menu Items, Preset Titles & Descriptions
const EXACT_MAP = new Map([
  // Topbar & Window Chrome
  ["全托管指挥舱", "Project Workspace"],
  ["项目工作台", "Project Workspace"],
  ["👻 窗口隐身", "👻 Stealth On"],
  ["👻 窗口已隐身", "👻 Stealth On"],
  ["👁 窗口已显示", "👁 Windows Shown"],
  ["显现窗口中…", "Showing…"],
  ["思考过程", "Thinking"],
  ["正在思考…", "Thinking…"],
  ["已读取", "Analyzed"],
  ["已修改", "Edited"],
  ["已创建", "Created"],
  ["已检索", "Searched"],
  ["正在读取", "Analyzing"],
  ["正在修改", "Editing"],
  ["正在创建", "Creating"],
  ["正在检索", "Searching"],
  ["👻 零弹窗", "👻 Stealth"],
  ["👻 已隐身", "👻 Hidden"],
  ["隐身处理…", "Hiding…"],
  ["⚡ 自动换号", "⚡ Auto-Failover"],
  ["⚡ 换号关", "⚡ Failover Off"],
  ["⚡ 已开启", "⚡ Enabled"],
  ["已关闭", "Disabled"],
  ["切换中…", "Switching…"],
  ["🌐 预览", "🌐 Preview"],
  ["🌐 预览开启", "🌐 Preview On"],
  ["加载预览…", "Loading…"],
  ["访问预览", "Go"],
  ["打开中…", "Opening…"],
  ["✓ 已加载", "✓ Loaded"],
  ["🎯 点选页面元素抓取 DOM+截图", "🎯 Inspect Element (DOM + Screenshot)"],
  ["🎯 正在点选右侧元素… (点击取消)", "🎯 Click any element in preview… (Cancel)"],
  ["🎯 请点选", "🎯 Pick Element"],
  ["已取消", "Cancelled"],
  ["📸 截取整页附加至对话", "📸 Attach Full Screenshot"],
  ["截图中…", "Capturing…"],
  ["✓ 已附加", "✓ Attached"],
  ["点击右侧实时网页任意组件，自动提取 HTML/CSS 与截图喂给 Agent", "Click any component in live preview to attach its HTML, computed CSS, and screenshot"],
  ["🎨 深空极客", "🎨 Obsidian"],
  ["🎨 赛博矩阵", "🎨 Matrix"],
  ["🎨 晨雾亮色", "🎨 Daylight"],
  ["🎨 极光毛玻璃", "🎨 Aurora"],
  ["深空极客", "Obsidian"],
  ["赛博矩阵", "Matrix"],
  ["晨雾亮色", "Daylight"],
  ["极光毛玻璃", "Aurora"],

  // Left Navigation Rail
  ["指挥舱", "Projects"],
  ["项目", "Projects"],
  ["专舱", "Studio"],
  ["竞技场", "Arena"],
  ["账号池", "Accounts"],
  ["官网", "Docs"],
  ["同步", "Sync"],

  // Project Sidebar & Threads
  ["＋ 新建项目", "＋ New Project"],
  ["📂 打开", "📂 Open"],
  ["打开…", "Opening…"],
  ["＋ 新建任务", "＋ New Task"],
  ["主对话", "Main Chat"],
  ["我的群聊", "Main Chat"],
  ["重构算力池自动接力与 Worktree 隔离", "Quota Failover & Worktree Isolation"],
  ["未命名项目", "Untitled Project"],
  ["＋ 点击在此项目开启新对话", "＋ Start a new chat in this project"],
  ["暂无匹配项目，点击上方「＋ 新建项目」或「📂 打开」开始", "No matching projects. Click '＋ New Project' or '📂 Open' above to start."],
  ["请点击左侧「＋ 新建项目」或「📂 打开」开始工作", "Click '＋ New Project' or '📂 Open' on the left to get started"],
  ["从侧栏移除项目", "Remove Project from Sidebar"],
  ["移除列表项", "Remove from List"],

  // Room Header & Roster Popover
  ["＋ 新对话", "＋ New Chat"],
  ["⑂ 独立分支", "⑂ Worktree"],
  ["⑂ 派生分支", "⑂ Worktree"],
  ["⑂ 合并回主库", "⑂ Merge"],
  ["⑂ 合并", "⑂ Merge"],
  ["⑂ 分支", "⑂ Branch"],
  ["创建分支…", "Branching…"],
  ["✓ 已派生", "✓ Branched"],
  ["正在合并…", "Merging…"],
  ["✓ 已合入", "✓ Merged"],
  ["⌨ 终端", "⌨ Terminal"],
  ["🖥 终端", "🖥 Terminal"],
  ["💬 对话", "💬 Chat"],
  ["📊 状态", "📊 Status"],
  ["↻ 同步全员", "↻ Sync All"],
  ["＋ 新接入", "＋ Add Agent"],
  ["⚙ 详细编排", "⚙ Configure"],
  ["删除项目对话", "Delete Thread"],
  ["删除任务会话", "Delete Thread"],
  ["🗑 确认删除", "🗑 Delete"],

  // Composer & Modes
  ["输入指令，或按 @ 点名任意智能体协同处理（Ctrl+Enter 发送）…", "Type a message, or press @ to mention specific agents (Enter to send)…"],
  ["🛠 Agent 读写", "🛠 Agent"],
  ["Agent 读写", "Agent"],
  ["🔍 Ask 只读", "🔍 Ask"],
  ["Ask 只读", "Ask"],
  ["对话", "Chat"],
  ["合并", "Merge"],
  ["分支", "Branch"],
  ["启动后台并行赛马", "Start Arena Run"],
  ["桌面零弹窗", "Zero-Popup Stealth"],
  ["获取 API 密钥与算力通道指南 ↗", "Get API Keys & Quota Channel Guide ↗"],
  ["全功能读写", "Agent Write"],
  ["只读分析", "Read-Only"],
  ["读写", "Write"],
  ["只读", "Read-Only"],
  ["主力：", "Agent:"],
  ["未接入智能体", "No Agent Connected"],
  ["@ 多人协作", "@ Mention"],
  ["@ 协作", "@ Mention"],
  ["⚡ 全员并行", "⚡ Ask All"],
  ["＋ 接入", "＋ Add"],
  ["■ 停止", "■ Stop"],
  ["发送 (Enter)", "Send ↵"],
  ["发送 (Ctrl+Enter)", "Send (Ctrl+Enter)"],
  ["正在派单…", "Sending…"],
  ["✓ 已发送", "✓ Sent"],

  // Empty-State Project Hero Cards
  ["🚀 初始化项目 / 梳理代码架构", "🚀 Initialize Project / Map Architecture"],
  ["🔧 排查修复并运行验证", "🔧 Debug, Fix & Run Verification"],
  ["在当前项目目录全自动改代码、跑测试并汇报 Diff", "Automatically edit files, run build/test commands, and report diffs in this project"],
  ["⌨ 打开当前项目内置终端", "⌨ Open Integrated Project Terminal"],
  ["在项目目录下打开隔离环境 PowerShell / Codex TUI / Claude TUI", "Launch PowerShell, Codex TUI, or Claude TUI with isolated account credentials in this folder"],
  ["🤝 @ 多智能体同屏协作评审", "🤝 @ Multi-Agent Cross Review"],
  ["在同一项目内同时调度多个账号或跨平台模型交叉把关", "Mention multiple accounts or cross-vendor models to collaborate in the same project"],

  // Chat Stream & Messages
  ["你", "You"],
  ["复制", "Copy"],
  ["复制代码", "Copy"],
  ["✓ 已复制", "✓ Copied"],
  ["展开/收起", "Toggle"],
  ["已完成", "Done"],
  ["正在回复", "Streaming"],
  ["等待回复", "Queued"],
  ["已取消", "Cancelled"],
  ["已中断", "Interrupted"],
  ["失败", "Failed"],
  ["工作中", "Working"],
  ["独立账号", "Isolated Account"],

  // Terminal & Diff Review Drawer
  ["本机默认环境", "System Default Env"],
  ["点击右上角 ＋ PowerShell / Codex TUI / Claude TUI 启动绑定独立账号的内置伪终端", "Click ＋ PowerShell / Codex TUI / Claude TUI above to start an integrated ConPTY session"],
  ["Git Diff 审查与逐行批注", "Git Diff & Inline Code Review"],
  ["Git Diff 审查", "Git Diff Review"],
  ["并排 Split", "Split"],
  ["↻ 刷新", "↻ Refresh"],
  ["点击任意代码行左侧 ＋ 添加行内批注", "Click ＋ on any diff line to add an inline review comment"],
  ["悬停代码行点击 ＋ 添加行内批注", "Hover any diff line and click ＋ to attach inline review"],
  ["打包发回修改", "Send Review"],

  // Arena View
  ["⚡ 多智能体后台并行赛马竞技场 (Headless Worktree Arena)", "⚡ Parallel Worktree Arena · Multi-Agent Benchmark"],
  ["全程后台无窗运行：为每个选定账号（Codex / Claude / 反重力）自动创建隔离 Git Worktree 并行编码，对比 Diff 与测试结果后一键合入主分支", "Headless parallel execution: creates an isolated Git Worktree per selected agent, compares diffs & verification results, and merges the winner in one click."],
  ["选择仓库", "Browse…"],
  ["参赛后台账号：", "Contestants:"],
  ["🚀 启动后台并行赛马", "🚀 Start Arena Run"],
  ["创建分支并派单…", "Spawning Worktrees…"],
  ["🚀 已发车", "🚀 Started"],
  ["尚未发起后台赛马竞赛", "No Active Arena Runs"],
  ["在上方勾选 2~6 个不同账号（支持 Codex、Claude、反重力同台竞技），全部选手将在后台独立 Git Worktree 无窗并行开发。", "Select 2–6 accounts above (Codex, Claude, Antigravity, Direct API, or CLI) to solve your task concurrently in isolated Git Worktrees."],
  ["正在后台独立 Worktree 中编码…", "Coding in isolated Git Worktree…"],
  ["等待输出", "Waiting for output…"],
  ["测试 PASS", "Tests PASS"],
  ["± 审查完整 Diff", "± View Full Diff"],
  ["✓ 已合入主分支", "✓ Merged to Main"],
  ["🏆 采纳并合并", "🏆 Accept & Merge"],
  ["■ 停止本场赛马", "■ Stop Arena"],
  ["停止中…", "Stopping…"],
  ["已停止", "Stopped"],
  ["读取 Diff…", "Loading Diff…"],
  ["✓ 已打开", "✓ Opened"],
  ["合并胜出方案回主仓库", "Merge Winning Branch to Main"],
  ["✓ 确认合并", "✓ Confirm Merge"],
  ["关闭", "Close"],

  // Accounts / Quota Pool View
  ["↻ 刷新额度", "↻ Refresh Quotas"],
  ["并行查询中…", "Refreshing…"],
  ["✓ 额度已更新", "✓ Updated"],
  ["＋ 添加账号 / Agent", "＋ Add Account / Agent"],
  ["全部", "All"],
  ["免客户端 API/CLI", "Direct API / CLI"],
  ["反重力", "Antigravity"],
  ["🛡 反重力双开防串号提示", "🛡 Antigravity Multi-Account Guide"],
  ["当前分类暂无匹配账号", "No matching accounts in this filter"],
  ["＋ 添加免下载 API / CLI 或隔离账号", "＋ Add Direct API, CLI, or Isolated Account"],
  ["● 待命", "● Ready"],
  ["● 在线就绪", "● Ready"],
  ["● 就绪", "● Ready"],
  ["● 已限流", "● Rate Limited"],
  ["○ 待登录", "○ Sign In"],
  ["○ 待装 CLI", "○ Install CLI"],
  ["5h 额度", "5h Quota"],
  ["7d 周度", "7d Weekly"],
  ["待同步", "Syncing"],
  ["直连通道", "Direct API"],
  ["预算余量", "Budget Left"],
  ["不限额", "Unlimited"],
  ["运行状态", "Status"],
  ["无头就绪", "Ready"],
  ["待授权", "Sign In Required"],
  ["免客户端 · 独立隔离环境", "Clientless · Isolated Home"],
  ["⚡ 测速", "⚡ Ping"],
  ["测速…", "Pinging…"],
  ["✓ 正常", "✓ OK"],
  ["⬇ 安装", "⬇ Install"],
  ["🔑 登录", "🔑 Sign In"],
  ["👻 收回窗", "👻 Hide Win"],
  ["💬 设为当前主力", "💬 Use in Project"],
  ["🖥 专舱", "💬 Use in Project"],
  ["@ 群聊", "@ Mention"],
  ["打开账号池 →", "Open Accounts →"],
  ["↻ 刷新状态", "↻ Refresh"],
  ["💬 在指挥舱 @Ta", "💬 @Mention in Chat"],
  ["👻 后台静默守护", "👻 Start Headless"],
  ["🔑 临时弹窗登录", "🔑 Sign-In Window"],
  ["👻 收回登录窗", "👻 Hide Window"],

  // Card Context Menu & Advanced Process Drawer
  ["↻ 刷新状态与配额", "↻ Refresh Status & Quota"],
  ["🔄 转为免客户端 CLI（保留原目录）", "🔄 Convert to Clientless CLI (Keep Home)"],
  ["🔄 立即转为免客户端", "🔄 Convert to Clientless"],
  ["👻 后台静默启动守护", "👻 Start Headless Daemon"],
  ["👻 收回登录窗口", "👻 Hide Sign-In Window"],
  ["🔑 临时弹窗登录授权", "🔑 Open Sign-In Window"],
  ["⚙ 编辑模型 / 目录与密钥", "⚙ Edit Model, Home & API Key"],
  ["⌨ 打开专属隔离终端", "⌨ Open Isolated Terminal"],
  ["📂 打开本地数据目录", "📂 Open Profile Folder"],
  ["✎ 重命名账号", "✎ Rename Account"],
  ["🗑 从列表移除（隐藏）", "🗑 Hide from List"],
  ["🗑 移除此账号", "🗑 Remove Account"],
  ["▸ 高级运维与底层守护进程", "▸ Process & Sandbox Diagnostics"],
  ["低频操作已收纳 · 点击展开", "Click to expand"],
  ["默认全部无头运行（0 弹窗）；若外部客户端意外显示窗口或沙箱异常，可在此一键收纳修复。", "All agents run headless by default. Manage external client processes or repair multi-account sandboxes below."],
  ["👻 隐藏全部外部窗口", "👻 Hide External Windows"],
  ["隐身收纳中…", "Hiding…"],
  ["👻 全部已隐身", "👻 All Hidden"],
  ["🛡️ 修复多开沙箱", "🛡️ Repair Sandboxes"],
  ["检查沙箱…", "Checking…"],
  ["🛡️ 沙箱正常", "🛡️ Sandboxes OK"],
  ["↻ 刷新进程快照", "↻ Refresh Processes"],
  ["扫描进程…", "Scanning…"],
  ["✓ 已刷新", "✓ Refreshed"],
  ["👁 恢复隐藏窗口", "👁 Reveal Windows"],
  ["恢复中…", "Restoring…"],
  ["👁 已显现", "👁 Revealed"],
  ["✓ 已恢复", "✓ Restored"],
  ["归属账号", "Account"],
  ["类型", "Source"],
  ["根 PID", "Root PID"],
  ["状态", "State"],
  ["进程数", "Procs"],
  ["时长", "Uptime"],
  ["内存", "Memory"],
  ["窗口", "Window"],
  ["操作", "Actions"],
  ["当前无驻留客户端进程（直连 API 与无头 CLI 均为按需执行、零后台常驻）", "No resident desktop processes (Direct API & Headless CLI run on demand with zero idle footprint)"],
  ["👁 显现", "👁 Show"],
  ["👻 隐身", "👻 Hide"],
  ["退出", "Quit"],
  ["强杀", "Kill"],
  ["强制结束进程树", "Force Kill Process Tree"],
  ["⚡ 强制结束", "⚡ Force Kill"],

  // New Project Modal & Thread Settings Modal
  ["新建项目工作空间", "Create New Project"],
  ["新项目文件夹名称", "Project Folder Name"],
  ["存放父级目录", "Parent Directory"],
  ["浏览…", "Browse…"],
  ["稍后在底部选择智能体", "Select agent later in composer"],
  ["默认主力智能体（进入项目后可直接回车对话，也可随时输入 @ 多账号协作）", "Default Primary Agent (press Enter to chat directly, or type @ anytime for multi-agent)"],
  ["方便右侧实时查看代码改动 Diff，并随时派生独立分支或竞技场赛马", "Enables live Git Diff review, isolated worktree branches, and parallel Arena runs"],
  ["取消", "Cancel"],
  ["确认", "Confirm"],
  ["保存", "Save"],
  ["创建并进入项目", "Create & Open Project"],
  ["创建项目中…", "Creating…"],
  ["✓ 已创建", "✓ Created"],
  ["新建全托管任务会话 / Git Worktree", "New Project Thread / Git Worktree"],
  ["任务标题", "Thread Title"],
  ["新功能开发任务", "Feature Development"],
  ["项目或 Git 仓库目录", "Project or Git Repository Folder"],
  ["自动派生独立 Git Worktree 分支（推荐）", "Create isolated Git Worktree branch (Recommended)"],
  ["新任务自动接入全部可用后台 Agent，并在独立分支全功能读写", "Enrolls all available agents in an isolated branch without touching your main working tree"],
  ["初始执行权限", "Execution Mode"],
  ["🛠 Agent 全功能读写（直接修改代码并运行命令）", "🛠 Agent Full Write (Edit files & run commands)"],
  ["🔍 Ask 只读分析（仅分析方案，不改文件）", "🔍 Ask Read-Only (Analyze & plan without modifying files)"],
  ["创建任务", "Create Thread"],
  ["正在创建…", "Creating…"],
  ["任务会话编排与 @ 身份绑定（支持 Codex + Claude + 反重力混编）", "Thread Settings & @ Agent Roster (Codex + Claude + Antigravity + API)"],
  ["会话名称", "Thread Title"],
  ["工作目录", "Working Directory"],
  ["执行模式", "Sandbox Mode"],
  ["🛠 Agent 全功能读写（允许读写文件与执行命令）", "🛠 Agent Full Write (Read/write files & run commands)"],
  ["🔍 Ask 只读分析（仅分析代码）", "🔍 Ask Read-Only (Analyze code only)"],
  ["参与身份（最多 8 个，支持 Codex / Claude / 反重力）", "Enrolled @ Agents (up to 8 cross-platform accounts)"],
  ["＋ 添加身份", "＋ Add Slot"],
  ["保存配置", "Save Changes"],
  ["保存中…", "Saving…"],
  ["✓ 已保存", "✓ Saved"],
  ["重命名隔离账号", "Rename Account"],
  ["新名称", "New Display Name"],

  // Universal Agent Hub (Add Account / Agent Modal)
  ["全市场智能体接入中心（免下载直连 API · 轻量级 CLI · 桌面后台托管）", "Add Account / Universal Agent Hub (Direct API · Lightweight CLI · Isolated Desktop)"],
  ["全市场智能体接入中心 (免下载直连 API · 轻量级 CLI · 桌面后台托管)", "Add Account / Universal Agent Hub (Direct API · Lightweight CLI · Isolated Desktop)"],
  ["🚀 免下载直连 API 智能体（推荐）", "🚀 Direct API (Recommended)"],
  ["免下载直连 API 智能体（推荐）", "Direct API (Recommended)"],
  ["⌨ 轻量级全市场 CLI 引擎", "⌨ Lightweight CLI Engine"],
  ["轻量级全市场 CLI 引擎", "Lightweight CLI Engine"],
  ["🖥 官方订阅桌面客户端托管", "🖥 Isolated Desktop Client"],
  ["官方订阅桌面客户端托管", "Isolated Desktop Client"],
  ["智能体名称（指挥舱 @ 称呼）", "Agent Name (Display & @Mention Alias)"],
  ["通讯协议族", "API Protocol"],
  ["OpenAI 兼容协议 (/chat/completions)", "OpenAI Compatible (/chat/completions)"],
  ["Google Gemini 原生协议 (:generateContent)", "Google Gemini Native (:generateContent)"],
  ["Anthropic Messages 协议 (/v1/messages)", "Anthropic Messages (/v1/messages)"],
  ["模型标识 (Model ID)", "Model ID"],
  ["推荐模型快选：", "Quick Pick:"],
  ["API Key（Windows DPAPI 本机硬件级加密存储）", "API Key (Encrypted locally via Windows DPAPI)"],
  ["🔑 获取 API 密钥与算力通道指南 ↗", "🔑 Get API Keys & Quota Channel Guide ↗"],
  ["Token 预算上限（可选，用于电量条与自动换号）", "Token Budget Limit (Optional, for quota bar & auto-failover)"],
  ["⚡ 测试连接与延迟", "⚡ Test Connection"],
  ["测速验证中…", "Testing…"],
  ["✓ 连通正常", "✓ Connected"],
  ["✓ 立即接入并加入 @ 名单", "✓ Connect & Add Agent"],
  ["保存智能体修改", "Save Agent Changes"],
  ["加密保存中…", "Encrypting & Saving…"],
  ["✓ 已接入", "✓ Connected"],
  ["✓ 本机已就绪", "✓ Installed"],
  ["⬇ 可一键安装", "⬇ 1-Click Install"],
  ["⬇ 在内置分屏终端一键安装 / 更新", "⬇ Install / Update in Integrated Terminal"],
  ["启动终端安装…", "Starting Installer…"],
  ["✓ 已在终端运行", "✓ Running in Terminal"],
  ["智能体名称（每个实例分配独立隔离 HOME 目录）", "Agent Name (Dedicated Isolated HOME Directory)"],
  ["指定模型（可选）", "Model Override (Optional)"],
  ["自定义 CLI 可执行路径 / 命令", "Custom CLI Executable / Command"],
  ["参数模板（支持 {prompt} {model} {cwd} {sandbox}）", "Args Template ({prompt} {model} {cwd} {sandbox})"],
  ["专属 API Key 覆盖（可选，DPAPI 加密注入该 CLI 进程）", "API Key Override (Optional, DPAPI-encrypted)"],
  ["自定义网关 Base URL（可选）", "Custom Base URL Override (Optional)"],
  ["保留或指定账号主目录（可选 · 用于迁移原客户端目录）", "Custom / Preserved Account Home Directory (Optional)"],
  ["✓ 创建独立隔离 CLI 智能体", "✓ Create Isolated CLI Agent"],
  ["保存 CLI 智能体配置", "Save CLI Agent"],
  ["创建隔离环境中…", "Creating Isolated Home…"],
  ["官方订阅桌面客户端（可选托管 · 支持后台无窗隐身）", "Official Desktop Client (Isolated Profile · Headless Stealth)"],
  ["Codex 桌面版（无头 CLI 执行 · 实时 5h/7d 剩余电量监控 · 自动换号接力）", "OpenAI Codex Desktop (Headless CLI · Live 5h/7d Quota · Auto-Failover)"],
  ["Claude 桌面版（内置 Claude Code 无头引擎 · 独立 CLAUDE_CONFIG_DIR）", "Claude Desktop (Headless Claude Code · Isolated CLAUDE_CONFIG_DIR)"],
  ["Antigravity 反重力桌面版（独立 APPDATA + Gemini 目录 · Headless 后台守护）", "Google Antigravity Desktop (Isolated APPDATA & Gemini Home · Headless)"],
  ["隔离账号名称（同时作为默认 @ 名字）", "Isolated Profile Name (Default @ Alias)"],
  ["🚀 免下载 Gemini 直连 API", "🚀 Direct Gemini API"],
  ["⌨ 隔离目录 Gemini CLI", "⌨ Isolated Gemini CLI"],
  ["创建桌面隔离账号", "Create Isolated Profile"],

  // Built-in API & CLI Preset Names & Descriptions (from UniversalAgentCatalog)
  ["Google Gemini 直连（防串号推荐）", "Google Gemini Direct (Zero Token Clash)"],
  ["Google Gemini 直连", "Google Gemini Direct"],
  ["免装 Antigravity 客户端，每账号独立 API Key 物理隔离，彻底杜绝多开 Google 串号，内置完整读写与终端工具链。", "No Antigravity desktop client required. Per-account encrypted API key with built-in file read/write and PowerShell tools."],
  ["DeepSeek 深度求索", "DeepSeek V3 / R1"],
  ["零下载直连 DeepSeek V3 / R1，自动赋予本地文件编辑、搜索与 PowerShell 终端实操能力。", "Direct connection to DeepSeek V3 / R1 with full local file editing, search, and headless PowerShell execution."],
  ["OpenAI 官方直连", "OpenAI Direct API"],
  ["免装 Microsoft Store 版 Codex 客户端，纯内置 C# 工具循环直连 OpenAI 模型。", "Direct connection to OpenAI models via built-in C# agent tool loop without the Microsoft Store client."],
  ["Anthropic Claude 直连", "Anthropic Claude Direct"],
  ["免装 Claude MSIX 桌面端，原生支持 Anthropic Messages 工具调用协议与多账号并发。", "Native Anthropic Messages tool-use protocol with multi-account concurrency, no MSIX client required."],
  ["阿里云百炼 · 通义千问 Qwen", "Alibaba DashScope · Qwen Coder"],
  ["直连阿里云 DashScope Qwen3-Coder 编程大模型，免装任何外部插件。", "Direct connection to Qwen3-Coder models with zero external plugins required."],
  ["Moonshot Kimi 编程智能体", "Moonshot Kimi K2"],
  ["直连月之暗面 Kimi K2 模型，内置本地仓库读写与命令行工具链。", "Direct connection to Moonshot Kimi K2 with built-in repository file and shell toolchains."],
  ["智谱清言 · GLM Coding", "Zhipu GLM Coding"],
  ["直连智谱 BigModel 开放平台，免客户端即刻接入多智能体协同。", "Direct connection to GLM-4.5 coding models for multi-agent collaboration."],
  ["OpenRouter 全市场模型聚合", "OpenRouter (200+ Models)"],
  ["一把 Key 畅联全球 200+ 主流编程模型，支持在赛马竞技场同台跨模型竞技。", "Access 200+ frontier coding models with one API key and benchmark them side by side in Arena."],
  ["Ollama / LM Studio 本地离线模型", "Ollama / LM Studio (Local Offline)"],
  ["零 API 费用、100% 内网离线隐私安全，将本地 Ollama / LM Studio 秒变为全功能代码 Agent。", "100% local & private offline coding agent powered by your local Ollama or LM Studio server."],
  ["自定义 OpenAI 兼容网关 / 企业中转", "Custom OpenAI-Compatible Gateway"],
  ["适配任意 NewAPI / OneAPI / LiteLLM / vLLM / Azure / 企业内网大模型网关。", "Connect any LiteLLM, vLLM, OneAPI, NewAPI, Azure OpenAI, or enterprise LLM gateway."],
  ["OpenAI Codex CLI（免装商店客户端）", "OpenAI Codex CLI (No Store App)"],
  ["仅 ~15MB 轻量命令行引擎，无需安装臃肿 Windows Store 桌面客户端，独立 CODEX_HOME 多开。", "Lightweight ~15MB CLI engine with isolated CODEX_HOME per account—no Windows Store app needed."],
  ["官方原版 Claude Code 引擎，独立 CLAUDE_CONFIG_DIR 隔离多账号，无需安装 Claude 桌面端。", "Official Claude Code CLI with isolated CLAUDE_CONFIG_DIR per account—no desktop app needed."],
  ["Google Gemini CLI（独立目录防串号）", "Google Gemini CLI (Isolated Home)"],
  ["Google 官方轻量级编程 CLI，每个实例分配独立 HOME 与 .gemini 目录，不依赖反重力桌面端。", "Official Gemini CLI with dedicated HOME and .gemini directory per instance—zero credential sharing."],
  ["直接以无头协议驱动官方轻量 agy CLI，无需打开反重力桌面客户端。", "Drives Google Antigravity headless protocol directly without opening the desktop IDE window."],
  ["开源高人气多模型终端编码智能体（`opencode run --format json`），免桌面客户端。", "Popular open-source multi-model terminal coding agent (`opencode run --format json`)."],
  ["经典 Git 感知型 AI 结对编程工具，自动精细打补丁。", "Git-aware AI pair programmer with automatic diff patching."],
  ["阿里开源 Qwen Code 命令行智能体，轻量高速。", "Lightweight open-source Qwen Code CLI agent."],
  ["Block 开源自主工程 Agent CLI。", "Open-source autonomous engineering CLI agent by Block."],
  ["自定义任意市场 CLI 智能体", "Custom CLI Agent"],
  ["通用模板", "Custom Template"],
  ["echo 请在配置中填写自定义 CLI 可执行路径与参数模板", "echo Configure your custom CLI executable path & argument template below"],
  ["支持任意命令行 Agent（用 {prompt}、{model}、{cwd} 占位符或标准输入）", "Supports any CLI agent via {prompt}, {model}, {cwd} placeholders or stdin"],
  ["万能 CLI 适配器：接入 Cursor Agent、Cline CLI、内部自研 CLI 等任何命令行工具。", "Universal CLI adapter: connect Cursor Agent, Cline CLI, or any custom command-line agent."],

  // Antigravity Guard & Remove Account Modals
  ["反重力（Antigravity）双开防串号隔离机制说明", "Antigravity Multi-Account Isolation Guide"],
  ["为什么两个反重力桌面客户端退登时会互相变账号？", "Why does signing out of one Antigravity desktop window affect another?"],
  ["我知道了", "Got It"],
  ["🚀 添加免客户端 Gemini 独立号", "🚀 Add Clientless Gemini Agent"],
  ["🗑 确认隐藏", "🗑 Hide Account"],
  ["🗑 确认移除", "🗑 Remove Account"],
  ["正在移除…", "Removing…"],
  ["✓ 已移除", "✓ Removed"],

  // Studio Legacy / Inspector Labels
  ["独立智能体列表", "Dedicated Agents"],
  ["专属任务对话", "Dedicated Threads"],
  ["专属隔离环境原生交互终端", "Isolated Interactive Terminal"],
  ["▶ 启动智能体原生 TUI", "▶ Launch Native TUI"],
  ["← 返回对话面板", "← Back to Chat"],
  ["实时工作状态", "Live Telemetry"],
  ["空闲待命 · 随时响应", "Idle · Ready"],
  ["尚未登录授权", "Sign-In Required"],
  ["🔑 在隔离终端一键登录", "🔑 Sign In via Terminal"],
  ["实时执行与思考轨迹", "Live Execution & Tool Trace"],
  ["暂无", "None"],
  ["发送指令后，此处将实时滚动展示该智能体的思考、文件读写与命令执行步骤", "Reasoning, file edits, and shell commands will stream here in real time."],
  ["工作目录文件变更", "Working Tree Changes"],
  ["↻ 扫描", "↻ Scan"],
  ["工作区干净，暂无未提交文件改动", "Working tree clean — no uncommitted changes"],

  // Demo / Default Account Names (for Screenshots & Defaults)
  ["账号 1", "Account 1"],
  ["账号 2", "Account 2"],
  ["反重力主力号", "Antigravity Primary"],
  ["反重力二号", "Antigravity Backup"],
  ["GeminiPro直连", "GeminiPro Direct"],
  ["DeepSeek架构师", "DeepSeek Architect"],
  ["OpenCode终端", "OpenCode CLI"],

  // Native Model / Effort / Fast-Mode Controls
  ["模型：", "Model:"],
  ["思考：", "Effort:"],
  ["正在读取模型…", "Loading models…"],
  ["默认模型", "Default Model"],
  ["✎ 自定义模型 ID…", "✎ Custom Model ID…"],
  ["输入自定义模型标识 (Model ID)", "Custom Model ID"],
  ["确认使用", "Apply Model"],
  ["⚡ 极速", "⚡ Fast"],
  ["⚡极速", "⚡ Fast"],
  ["⚡⚡ 超极速", "⚡⚡ Ultrafast"],
  ["⚡⚡超极速", "⚡⚡ Ultrafast"],
  ["标准速度", "Standard"],
  ["标准", "Std"],
  ["默认强度", "Default"],
  ["关闭 None", "None"],
  ["极简 Minimal", "Minimal"],
  ["低 Low", "Low"],
  ["中 Medium", "Medium"],
  ["高 High", "High"],
  ["超高 XHigh", "XHigh"],
  ["极限 Max", "Max"],
  ["超旗舰 Ultra", "Ultra"],
  ["⚡ 全员", "⚡ All"],
  ["↻ 实时拉取模型", "↻ Fetch Models"],
  ["✓ 已拉取", "✓ Fetched"],
  ["↻ 刷新", "↻ Refresh"],
  ["＋ 接入新账号 / API / CLI", "＋ Add Account / API / CLI"],
  ["尚未接入可用账号", "No accounts connected yet"],
  ["无匹配模型，可点下方输入自定义 ID", "No matching models — click below for custom ID"],
  ["默认标准响应速度", "Standard inference speed"],
  ["就绪", "Ready"],
  ["待登录", "Sign In"],
  ["Claude 官方通道", "Anthropic Claude"],
  ["本机 Ollama 模型", "Local Ollama"],
  ["当前配置", "Configured"],
  ["推荐", "Default"],
  ["旗舰", "Flagship"],

  // v0.0.2 Flagship UI Additions
  ["窗口隐身", "Stealth On"],
  ["隐身", "Stealth"],
  ["搜索项目、对话或命令…", "Search projects, chats, or commands…"],
  ["窗口已隐身", "Stealth On"],
  ["窗口已显示", "Windows Shown"],
  ["自动换号", "Auto-Failover"],
  ["换号关", "Failover Off"],
  ["已开启", "Enabled"],
  ["预览", "Preview"],
  ["预览开启", "Preview On"],
  ["新建项目", "New Project"],
  ["打开", "Open"],
  ["新对话", "New Chat"],
  ["独立分支", "Worktree"],
  ["合并回主库", "Merge"],
  ["合并", "Merge"],
  ["分支", "Branch"],
  ["终端", "Terminal"],
  ["状态", "Status"],
  ["Agent 读写", "Agent"],
  ["Ask 只读", "Ask"],
  ["协作", "Mention"],
  ["全员", "All"],
  ["接入", "Add"],
  ["停止", "Stop"],
  ["发送", "Send"],
  ["↓ 回到底部", "↓ Latest"],
  ["回到底部", "Latest"],
  ["↻ 重试", "↻ Retry"],
  ["重试", "Retry"],
  ["✎ 编辑", "✎ Edit"],
  ["编辑", "Edit"],
  ["Diff 审查", "Diff Review"],
  ["实时轨迹", "Live Trace"],
  ["命令面板", "Command Palette"],
  ["搜索命令…", "Commands…"],
  ["没有匹配的命令或项目", "No matching commands or projects"],
  ["选择服务商 / 引擎", "Select Provider / Engine"],
  ["高级选填（协议 / Base URL / 预算上限）", "Advanced Options (Protocol / Base URL / Budget)"],
  ["＋ 同号跨模槽位", "＋ Cross-Model Slot"],
  ["同号跨模槽位", "Cross-Model Slot"],
  ["已选参赛槽位：", "Active Slots:"],
  ["🧹 清理分支", "🧹 Clean Worktrees"],
  ["清理分支", "Clean Worktrees"],
  ["↻ 复用配置", "↻ Load Config"],
  ["复用配置", "Load Config"],
  ["🗑 移除记录", "🗑 Remove"],
  ["正在等待响应…", "Waiting for response…"],
  ["○ 待配置密钥", "○ Set API Key"],
  ["待配置密钥", "Set API Key"],
]);

// Tier 2: Full-String Regex Rules for Structured Dynamic Strings
const REGEX_RULES = [
  [/^(\d+\/\d+)\s*就绪$/, (_, ratio) => `${ratio} Ready`],
  [/^(\d+)\s*运行中$/, (_, n) => `${n} Running`],
  [/^窗口(?:\s*\((\d+)\))?$/, (_, n) => (n ? `Windows (${n})` : "Windows")],
  [/^向\s+(.+?)\s+发送消息，输入 @ 多智能体协作…$/, (_, name) => `Message ${name}, or type @ to collaborate…`],
  [/^(\d+\/\d+)\s*智能体就绪(?:\s*·\s*均余\s*(\d+%))?$/, (_, ratio, avg) => (avg ? `${ratio} Ready · Avg ${avg}` : `${ratio} Agents Ready`)],
  [/^(?:⚡\s*)?(\d+)\s*个任务执行中$/, (_, n) => `${n} Task${n === "1" ? "" : "s"} Running`],
  [/^(?:👁\s*)?(\d+)\s*窗\s*·\s*隐身$/, (_, n) => `${n} Visible · Hide`],
  [/^(?:👁\s*)?窗口显示(?:\s*\((\d+)\))?$/, (_, n) => (n ? `Windows (${n})` : "Windows On")],
  [/^(\d+)\s*个文件已更改$/, (_, n) => `${n} file${n === "1" ? "" : "s"} changed`],
  [/^(?:👁\s*)?(\d+)\s*窗可见$/, (_, n) => `${n} Window${n === "1" ? "" : "s"} Visible`],
  [/^(?:👻\s*)?桌面零弹窗$/, () => "Zero-Popup Stealth"],
  [/^就绪\s*(\d+\/\d+)?$/, (_, r) => (r ? `Ready ${r}` : "Ready ")],
  [/^平均余量\s*(\d+%)?$/, (_, p) => (p ? `Avg Quota ${p}` : "Avg Quota ")],
  [/^累计流量\s*(.+)?$/, (_, t) => (t ? `Traffic ${t}` : "Traffic ")],
  [/^全部\s*\((\d+)\)$/, (_, n) => `All (${n})`],
  [/^全部\s*(\d+)$/, (_, n) => `All ${n}`],
  [/^免客户端 API\/CLI\s*(\d+)$/, (_, n) => `Direct API/CLI ${n}`],
  [/^反重力\s*(\d+)$/, (_, n) => `Antigravity ${n}`],
  [/^👁\s*恢复隐藏账号\s*\((\d+)\)$/, (_, n) => `👁 Restore Hidden (${n})`],
  [/^●\s*剩\s*(\d+%)$/, (_, p) => `● ${p} Left`],
  [/^●\s*余量\s*(\d+%)$/, (_, p) => `● ${p} Left`],
  [/^⚡\s*执行中\s*\((\d+)\)$/, (_, n) => `⚡ Running (${n})`],
  [/^⚡\s*执行中$/, () => "⚡ Running"],
  [/^剩余\s*(\d+%)$/, (_, p) => `${p} left`],
  [/^(\d+)\s*轮\s*·\s*(.+)$/, (_, r, t) => `${r} turn${r === "1" ? "" : "s"} · ${t}`],
  [/^完成轮次：(\d+)\s*轮$/, (_, r) => `Completed: ${r} turn${r === "1" ? "" : "s"}`],
  [/^累计流量：(.+)$/, (_, t) => `Traffic: ${t}`],
  [/^本机完成：(\d+)\s*轮$/, (_, r) => `Turns: ${r}`],
  [/^会话 Token：(.+)$/, (_, t) => `Session Tokens: ${t}`],
  [/^5h 窗口剩余$/, () => "5h Window Left"],
  [/^7d 周剩余$/, () => "7d Weekly Left"],
  [/^满额$/, () => "Full"],
  [/^(?:·\s*)?(\d+天\d+h|\d+h\d+m|\d+m|\d+h|\d+d)后(?:重置|恢复)?$/, (full, t) => {
    const clean = t.replace("天", "d");
    return full.trim().startsWith("·") ? ` · in ${clean}` : `in ${clean}`;
  }],
  [/^Diff 审查\s*·\s*(\d+)\s*文件\s*\((.+)\)$/, (_, f, s) => `Diff Review · ${f} file${f === "1" ? "" : "s"} (${s})`],
  [/^已添加\s*(\d+)\s*条行内代码批注$/, (_, n) => `${n} inline review comment${n === "1" ? "" : "s"} queued`],
  [/^派回给\s*(.+)$/, (_, a) => `Send to ${a}`],
  [/^\((\d+)\s*文件\)$/, (_, n) => `(${n} files)`],
  [/^测试 EXIT\s*(.+)$/, (_, c) => `Tests EXIT ${c}`],
  [/^方案\s*([A-Z0-9]+)$/, (_, c) => `Plan ${c}`],
  [/^参赛方案代码变更\s*\((\d+)\s*文件,\s*(.+)\)$/, (_, f, s) => `Contestant Diff (${f} files, ${s})`],
  [/^(\d+)\s*个\s*Agent$/, (_, n) => `${n} Agents`],
  [/^👥\s*(\d+)\s*个智能体\s*▾$/, (_, n) => `👥 ${n} Agents ▾`],
  [/^👥\s*(\d+)\s*▾$/, (_, n) => `👥 ${n} ▾`],
  [/^本会话参与智能体\s*\((\d+)\)$/, (_, n) => `Enrolled Agents (${n})`],
  [/^在线智能体与算力概览\s*\((\d+)\)$/, (_, n) => `Active Agents & Quotas (${n})`],
  [/^@\s*选择智能体\s*\((\d+)\)$/, (_, n) => `@ Mention (${n})`],
  [/^@\s*协作\s*\((\d+)\)$/, (_, n) => `@ Mention (${n})`],
  [/^对话\s*(\d+)$/, (_, n) => `Chat ${n}`],
  [/^群聊\s*(\d+)$/, (_, n) => `Chat ${n}`],
  [/^(.+)\s*·\s*专属工作台$/, (_, n) => `${n} · Workspace`],
  [/^(.+)\s*·\s*任务\s*(\d+)$/, (_, n, k) => `${n} · Task ${k}`],
  [/^思考与工具轨迹\s*·\s*(\d+)\s*步$/, (_, n) => `Reasoning & Tool Trace · ${n} step${n === "1" ? "" : "s"}`],
  [/^执行轨迹\s*·\s*(\d+)\s*步$/, (_, n) => `Execution Trace · ${n} step${n === "1" ? "" : "s"}`],
  [/^(\d+)\s*条$/, (_, n) => `${n} msgs`],
  [/^(\d+)\s*步$/, (_, n) => `${n} steps`],
  [/^耗时\s*(.+)$/, (_, t) => `Elapsed ${t}`],
  [/^账号目录：(.+)$/, (_, p) => `Profile Home: ${p}`],
  [/^将创建目录：(.+)$/, (_, p) => `Target path: ${p}`],
  [/^隔离环境：(.+)$/, (_, n) => `Isolated: ${n}`],
  [/^免客户端\s*·\s*保留目录\s*(.+)$/, (_, p) => `Clientless · Home ${p}`],
  [/^编辑智能体配置\s*·\s*(.+)$/, (_, n) => `Edit Agent · ${n}`],
  [/^转为免客户端模式\s*·\s*(.+)$/, (_, n) => `Convert to Clientless · ${n}`],
  [/^隐藏系统原始账号\s*·\s*(.+)$/, (_, n) => `Hide System Account · ${n}`],
  [/^移除账号\s*·\s*(.+)$/, (_, n) => `Remove Account · ${n}`],
  [/^新建专属任务\s*·\s*(.+)$/, (_, n) => `New Task · ${n}`],
  [/^让\s*(.+)\s*直接读取并在当前项目目录内开始构建$/, (_, n) => `Let ${n} inspect and build directly inside this project folder`],
  [/^在「(.+)」直接向「(.+)」发送指令（Enter 发送，Shift\+Enter 换行，输入 @ 可多账号协同）…$/, (_, proj, ag) => `Message ${ag} in "${proj}" (Enter to send, Shift+Enter for newline, @ for multi-agent)…`],
  [/^在「(.+)」向「(.+)」发送指令（Enter 发送，Shift\+Enter 换行，输入 @ 可多账号协同）…$/, (_, proj, ag) => `Message ${ag} in "${proj}" (Enter to send, Shift+Enter for newline, @ for multi-agent)…`],
  [/^远端可用\s*\((\d+)\)：$/, (_, n) => `Live Remote (${n}):`],
];

// Tier 3: Substring / Phrase Rules for Composite Text, Backend Statuses, Placeholders & Tooltips
const PHRASE_RULES = [
  // Backend AccountPoolService Status Strings
  [/⚡\s*正在直连执行\s*\((\d+)\)/g, "⚡ Direct API running ($1)"],
  [/⚡\s*正在无头执行\s*\((\d+)\)/g, "⚡ Headless CLI running ($1)"],
  [/⚡\s*正在后台执行任务\s*\((\d+)\)/g, "⚡ Running in background ($1)"],
  [/⚡\s*反重力守护在线\s*\(PID\s*(\d+)\)\s*·\s*(\d+)\s*个脑图会话/g, "⚡ Antigravity daemon online (PID $1) · $2 sessions"],
  [/反重力已登录就绪\s*·\s*(\d+)\s*个历史会话/g, "Antigravity ready · $1 sessions"],
  [/反重力新目录\s*·\s*请先完成一次 Google 登录授权/g, "New Antigravity home · Sign in to Google once"],
  [/新账号\s*·\s*首次请点临时弹窗完成 Google 登录/g, "New profile · Click Sign In once for Google OAuth"],
  [/免客户端就绪\s*·\s*内置工具链\s*\(([^)]+)\)/g, "Clientless ready · Built-in tools ($1)"],
  [/预算余量\s*(\d+%)\s*·\s*免客户端直连/g, "Budget $1 left · Direct API"],
  [/待安装\s*`?([^`·]+)`?\s*·\s*支持一键终端安装/g, "Install `$1` · 1-click terminal setup"],
  [/已就绪 CLI\s*·\s*点击「🔑 登录」在隔离目录授权一次 Google 号/g, "CLI installed · Click Sign In to authorize Google"],
  [/无头 CLI 就绪\s*\(([^)]+)\)/g, "Headless CLI ready ($1)"],
  [/已初始化\s*·\s*后台无头就绪/g, "Initialized · Headless ready"],
  [/待登录\s*·\s*可在终端执行 claude auth login/g, "Sign in required · Run claude auth login"],
  [/未登录\s*·\s*点击临时弹窗登录一次/g, "Not signed in · Click Sign In once"],
  [/满血待命\s*·\s*剩余\s*(\d+%)/g, "Healthy · $1 remaining"],
  [/余量偏低\s*\((\d+%)\)\s*·\s*自动接力保护中/g, "Low quota ($1) · Auto-failover active"],
  [/额度耗尽\s*·\s*([^\s]+)\s*重置/g, "Quota exhausted · Resets in $1"],
  [/额度即将耗尽/g, "Quota nearly exhausted"],
  [/限流冷却\s*·\s*([^\s]+?)后恢复/g, "Cooldown · Recovers in $1"],
  [/正在同步官方配额…/g, "Syncing quota…"],
  [/配额暂不可用/g, "Quota unavailable"],
  [/外部工作区\s*·\s*未验证配额/g, "External workspace · Unverified"],
  [/外部未验证路径/g, "External unverified path"],

  // Failover Notices & Advanced Diagnostics
  [/触发限流保护：(.+?)\s*→\s*已自动接力至\s*(.+?)，上下文与 Worktree 保持不变/g, "Auto-Failover: $1 → seamlessly switched to $2 (context & worktree preserved)"],
  [/▸\s*高级运维与底层守护进程（当前驻留进程\s*(\d+)\s*个\s*·\s*(.+)）/g, "▸ Process & Sandbox Diagnostics ($1 active processes · $2)"],
  [/全部受管 Codex 账号多开沙箱就绪（unelevated 隔离令牌）。?/g, "All managed Codex sandboxes ready (isolated token)"],
  [/正在检查受管账号多开沙箱[….]*/g, "Checking multi-account sandboxes…"],
  [/已修复\s*(\d+)\s*个受管账号的多开沙箱配置并保留备份。?/g, "Repaired $1 managed sandbox(es) with backup"],
  [/沙箱就绪/g, "Sandboxes ready"],

  // Subtitles, Option Suffixes & Mixed Fragments
  [/Antigravity（反重力）\s*·\s*隔离数据目录/g, "Antigravity · Isolated Profile"],
  [/Antigravity（反重力）\s*·\s*系统默认目录/g, "Antigravity · Default Profile"],
  [/Antigravity（反重力）\s*·\s*原始官方数据目录/g, "Antigravity · Default Official Profile"],
  [/Claude\s*·\s*隔离数据目录/g, "Claude · Isolated Profile"],
  [/Claude\s*·\s*系统默认目录/g, "Claude · Default Profile"],
  [/Claude\s*·\s*原始官方数据目录/g, "Claude · Default Official Profile"],
  [/Codex\s*·\s*隔离数据目录/g, "Codex · Isolated Profile"],
  [/Codex\s*·\s*系统默认目录/g, "Codex · Default Profile"],
  [/Codex\s*·\s*原始官方数据目录/g, "Codex · Default Official Profile"],
  [/原始官方数据目录/g, "Default Official Profile"],
  [/原始官方目录/g, "Default Official Folder"],
  [/独立 CLI 沙箱目录/g, "Isolated CLI Sandbox"],
  [/DPAPI 加密直连\s*·\s*零客户端体积/g, "DPAPI Direct API · Zero Client Footprint"],
  [/待配置密钥\s*·\s*点击编辑填入 API Key/g, "API key required · Click Edit to configure"],
  [/隔离数据目录/g, "Isolated Profile"],
  [/系统默认目录/g, "Default Profile"],
  [/·\s*待登录/g, "· Sign In"],
  [/·\s*(\d+)天(\d+)h后/g, "· in $1d$2h"],
  [/·\s*(\d+h\d+m|\d+m|\d+h)后/g, "· in $1"],
  [/(\d+)天(\d+)h重置/g, "in $1d$2h"],
  [/(\d+h\d+m|\d+m|\d+h)重置/g, "in $1"],
  [/·\s*当前主力：/g, "· Active Agent: "],
  [/（直接输入即可像原版客户端一样在项目内读写操作，输入\s*/g, " (Press Enter to work directly in this folder, or type "],
  [/\s*可随时多智能体协作）/g, " anytime for multi-agent collaboration)"],
  [/自动初始化 Git 仓库\s*\(/g, "Initialize Git repository ("],
  [/✨\s*零客户端全功能赋能：/g, "✨ Full Agent Capabilities without Desktop Apps: "],
  [/此账号无需安装任何外部软件，指挥舱内置 C# Agent 引擎自动赋予其\s*/g, "Built-in C# agent engine natively equips this API model with "],
  [/\s*与无窗\s*/g, " and headless "],
  [/\s*全套实操能力！/g, " tools!"],
  [/✅\s*已检测到本机引擎：/g, "✅ Detected local CLI binary: "],
  [/📦\s*尚未检测到\s*/g, "📦 Not installed yet: "],
  [/，无需下载桌面客户端，可一键轻量安装：/g, " — click to install in 1 click:"],

  // Placeholders & Common Input Hints
  [/搜索具体模型名称或标识…/g, "Filter models by name or ID…"],
  [/点击选择具体模型（支持实时搜索与自定义模型 ID）/g, "Select model (live search & custom model ID)"],
  [/配额剩余\s*(\d+%)/g, "$1 quota left"],
  [/搜索项目或对话…/g, "Search projects or chats…"],
  [/搜索会话或分支…/g, "Search threads or branches…"],
  [/筛选名称、平台或模型…/g, "Filter by name, provider, or model…"],
  [/补充总体修改要求（可选）…/g, "Optional overall review summary…"],
  [/选择 Git 仓库根目录…/g, "Select Git repository root folder…"],
  [/可选自动验收命令（如 dotnet test 或 npm test）/g, "Optional test command (e.g. dotnet test or npm test)"],
  [/输入赛马开发任务：各参赛 Agent 将在后台各自独立的 Git Worktree 分支中同步实现…/g, "Describe the task: selected agents will implement concurrently in isolated Git Worktrees…"],
  [/例如：my-saas-app 或 demo-agent-tool/g, "e.g. my-saas-app or demo-agent-tool"],
  [/如：GeminiPro2 \/ DeepSeek架构师/g, "e.g. GeminiPro-2 / DeepSeek-Coder"],
  [/粘贴 sk-\.\.\. \/ AIza\.\.\. 密钥（绝不明文落盘）/g, "Paste sk-... / AIza... API key (DPAPI encrypted, never stored in plaintext)"],
  [/已加密保存 \(([^)]+)\)，留空(?:保持)?不变/g, "Encrypted ($1) — leave blank to keep"],
  [/本地 Ollama 模型可留空/g, "Leave blank for local Ollama"],
  [/留空为不限额，如填 2000000/g, "Blank = unlimited (e.g. 2000000)"],
  [/留空使用该 CLI 默认模型/g, "Leave blank to use CLI default model"],
  [/如 cursor-agent\.cmd 或 C:\\tools\\my-agent\.exe/g, "e.g. cursor-agent.cmd or C:\\tools\\my-agent.exe"],
  [/留空则使用独立目录 OAuth 登录态/g, "Leave blank to use isolated OAuth login"],
  [/可选覆盖 API 端点/g, "Optional custom API base URL"],
  [/留空则自动创建独立隔离目录 data\/universal-agents\/\.\.\./g, "Leave blank to auto-create isolated folder under data/universal-agents/..."],
  [/例如：Codex3 \/ Claude架构号 \/ 反重力2号/g, "e.g. Codex-Pro-2 / Claude-Lead / Antigravity-2"],
  [/@身份名/g, "@Alias"],
  [/模型\(可选\)/g, "Model (optional)"],

  // Tooltips (`title` attributes) & Toast Messages
  [/选择当前账号使用的原生具体模型，点击右侧 ↻ 实时同步最新模型目录/g, "Select native model for the active account; click ↻ to refresh live catalog"],
  [/从官方守护进程或远端 API 实时拉取最新模型列表/g, "Fetch live model catalog from local daemon or remote API"],
  [/原生思考强度 \/ 推理深度控制/g, "Native reasoning effort / thinking depth"],
  [/切换极速高优推理通道/g, "Toggle high-speed priority inference (Fast Mode)"],
  [/原生推理速度档位（Standard \/ Fast \/ Ultrafast）/g, "Native inference speed tier (Standard / Fast / Ultrafast)"],
  [/思考强度/g, "Reasoning Effort"],
  [/推理速度档位/g, "Speed Tier"],
  [/Pro \(主力旗舰\)/g, "Pro (Flagship)"],
  [/Flash \(极速响应\)/g, "Flash (Fast)"],
  [/Flash Lite \(超轻量\)/g, "Flash Lite (Ultra-Light)"],
  [/Antigravity CLI \(官方 agy acp\)/g, "Antigravity CLI (Official agy acp)"],
  [/官方订阅桌面客户端（标准隔离目录 · 零侵入合规调用）/g, "Official Subscription Clients (Isolated Profile · Compliant Integration)"],
  [/Codex 桌面版（官方 CLI 执行 · 实时 5h\/7d 剩余电量监控 · 自动换号接力）/g, "OpenAI Codex Desktop (Official CLI · Live 5h/7d Quota & Failover)"],
  [/Claude 桌面版（官方 Claude Code 引擎 · 独立 CLAUDE_CONFIG_DIR）/g, "Anthropic Claude Desktop (Official Claude Code · Isolated CLAUDE_CONFIG_DIR)"],
  [/Antigravity 反重力（官方标准 agy acp 协议 · 零封号风险）/g, "Google Antigravity (Official agy acp Protocol · Account-Safe)"],
  [/为每个选定智能体自动创建隔离 Git Worktree 并行编码，对比 Diff 与测试结果后一键合入主分支/g, "Spawns isolated Git Worktrees per selected agent to code in parallel, compare diffs & test results, and merge winners"],
  [/账号风控安全保护：/g, "Account Safety Protection: "],
  [/高并发并行赛马推荐勾选「直连 API（API Key）」通道；个人 OAuth 订阅账号默认不自动勾选，避免高频自动化并发触发平台风控。/g, "High-concurrency parallel runs recommend Direct API (API Key) channels; personal OAuth subscriptions are left unchecked by default to prevent automated rate-limit flags."],
  [/在内置终端执行 `agy acp --login` 即可通过官方标准 ACP 协议驱动（零封号风险）/g, "Run `agy acp --login` in built-in terminal to connect via official ACP protocol"],
  [/采用 Google 官方公开 Agent Client Protocol \(`agy acp` JSON-RPC 2\.0\) 通信，与 Zed \/ JetBrains 官方集成同源。/g, "Communicates strictly via Google's official public Agent Client Protocol (`agy acp` JSON-RPC 2.0), matching Zed & JetBrains integrations."],
  [/待安装官方 `agy` CLI · 支持一键终端安装/g, "Official `agy` CLI not installed · 1-click install available"],
  [/待安装官方 `agy` CLI · 按订阅周期限额/g, "Official `agy` CLI not installed · Plan-limited"],
  [/官方 `agy acp` 标准协议就绪/g, "Official `agy acp` ready"],
  [/待独立登录 · 点击「🔑 登录」授权此号（文件级独立凭据，不顶原号）/g, "Sign in required · Click 🔑 Sign In (file-isolated credentials, never overwrites default account)"],
  [/待登录 · 点击「🔑 登录」或在终端执行 `agy acp --login` 授权/g, "Sign in required · Click 🔑 Sign In or run `agy acp --login`"],
  [/待登录 · 请在终端执行 `agy acp --login` 授权/g, "Sign in required · run `agy acp --login` in terminal"],
  [/官方 `agy` 5h 余量 (\d+)% · 周余量 (\d+)%/g, "Official `agy` 5h: $1% · 7d: $2%"],
  [/官方 `agy` 5h 余量 (\d+)%/g, "Official `agy` 5h: $1%"],
  [/官方 `agy` 额度 (\d+)%/g, "Official `agy` quota: $1%"],
  [/官方 `agy acp` (?:就绪|Ready) · 独立凭据隔离/g, "Official `agy acp` ready · File-isolated"],
  [/正在执行 ACP 任务/g, "Running ACP task"],
  [/远端可用\s*\((\d+)\)：/g, "Available ($1):"],
  [/尚未接入可用账号/g, "No active accounts configured yet"],
  [/＋ 接入新账号 \/ API \/ CLI/g, "＋ Add Account / API / CLI"],
  [/无匹配模型，可点下方输入自定义 ID/g, "No matching models — click below for custom ID"],
  [/正在读取模型…/g, "Loading models…"],
  [/✎ 自定义模型 ID…/g, "✎ Custom Model ID…"],
  [/确认使用/g, "Apply"],
  [/标准速度/g, "Standard Speed"],
  [/默认标准响应速度/g, "Default standard inference speed"],
  [/⚡⚡ 超极速/g, "⚡⚡ Ultrafast"],
  [/⚡ 极速/g, "⚡ Fast"],
  [/最低延迟极简思考/g, "Minimal latency & light thinking"],
  [/快速轻量思考/g, "Fast lightweight reasoning"],
  [/均衡深度与速度/g, "Balanced reasoning depth & speed"],
  [/标准均衡推理/g, "Standard balanced reasoning"],
  [/深度复杂逻辑推理/g, "Deep complex reasoning"],
  [/超高强度深度推理/g, "Extra-high reasoning depth"],
  [/最高深度推理/g, "Maximum reasoning depth"],
  [/OpenAI Priority 高优加速通道/g, "OpenAI Priority high-speed channel"],
  [/当前已选模型/g, "Currently selected model"],
  [/当前配置模型/g, "Configured model"],
  [/官方 `agy acp` 默认旗舰推理模型/g, "Official `agy acp` flagship reasoning model"],
  [/官方 `agy acp` 高速低延迟推理通道/g, "Official `agy acp` fast low-latency model"],
  [/官方 `agy acp` 极轻量响应通道/g, "Official `agy acp` ultra-lightweight model"],
  [/官方 `agy acp` (?:就绪|Ready) · 按订阅周期限额/g, "Official `agy acp` ready · Plan-limited"],
  [/Flash \(极速响应\)/g, "Flash (Fast)"],
  [/Flash Lite \(超轻量\)/g, "Flash Lite"],
  [/Claude Sonnet \(自动最新主力\)/g, "Claude Sonnet (Latest)"],
  [/Claude Opus \(自动最新旗舰\)/g, "Claude Opus (Flagship)"],
  [/Claude Haiku \(自动极速版\)/g, "Claude Haiku (Fast)"],
  [/·\s*推荐/g, "· Default"],
  [/·\s*旗舰/g, "· Flagship"],
  [/·\s*⚡\s*极速/g, "· ⚡ Fast"],
  [/将所有外部客户端窗口隐藏至后台运行，桌面与任务栏零弹窗/g, "Hide all external desktop client windows into background stealth mode"],
  [/当前：窗口隐身开启（后台无窗运行）· 点击切换为显示外部客户端窗口/g, "Stealth Mode ON (headless background) · Click to show external client windows"],
  [/当前：显示客户端窗口 · 点击切换为全部后台隐身/g, "Windows Visible · Click to hide all external client windows into stealth mode"],
  [/点击打开右侧 Git Diff 逐行审查代码改动/g, "Open Git Diff drawer to review line-by-line changes"],
  [/某个账号额度耗尽或触发 429 时，自动无感切换池内健康账号/g, "Automatically switch to a healthy account when quota is exhausted or 429 occurs"],
  [/打开内置浏览器实时预览与点选 DOM\/截图/g, "Open live web preview & element inspector"],
  [/切换界面风格/g, "Switch UI theme"],
  [/最小化/g, "Minimize"],
  [/最大化 \/ 还原/g, "Maximize / Restore"],
  [/关闭/g, "Close"],
  [/项目工作台 · 新建或打开项目并在项目目录内操作（支持单 Agent 直发与 @ 多智能体协同）/g, "Project Workspace · Create or open projects, chat 1-on-1 or @mention multiple agents"],
  [/赛马竞技场 · 多账号\/跨模型独立 Worktree 并行实现与合并/g, "Parallel Arena · Benchmark multiple agents in isolated Git Worktrees"],
  [/账号与额度池 · 极简卡片监控与接入/g, "Accounts & Quotas · Manage API keys, CLIs, and isolated profiles"],
  [/打开官方网站与使用指南/g, "Open documentation & website"],
  [/立即刷新全平台额度与守护状态/g, "Sync all account quotas & daemon statuses"],
  [/打开电脑上已有的项目文件夹/g, "Open an existing project folder from disk"],
  [/在此项目下新建对话/g, "New chat in this project"],
  [/在资源管理器中打开目录/g, "Reveal in File Explorer"],
  [/从侧栏移除此项目（本地文件保留）/g, "Remove from sidebar (files on disk are kept)"],
  [/点击在资源管理器中打开目录：/g, "Reveal in Explorer: "],
  [/切换当前对话绑定的项目文件夹/g, "Change project folder for this chat"],
  [/在当前项目下再开一个新对话/g, "Start another chat in this project"],
  [/为此对话派生独立 Git Worktree 分支/g, "Create an isolated Git Worktree branch for this chat"],
  [/查看或编排本对话可协作智能体名单/g, "View or configure enrolled @ agents"],
  [/对话标题与智能体编排设置/g, "Thread title & agent settings"],
  [/删除此对话/g, "Delete this chat"],
  [/默认主力智能体：不输入 @ 时，回车将直接发给此账号处理/g, "Primary Agent: pressing Enter without @ sends directly to this agent"],
  [/输入 @ 点名单个或多个智能体协同工作/g, "Mention one or more agents with @ to collaborate"],
  [/同时点名全部已接入后台 Agent 并行回复/g, "Ask all enrolled agents in parallel"],
  [/接入新的免客户端 API \/ CLI 或隔离账号/g, "Connect a new Direct API, CLI, or isolated profile"],
  [/全功能 Agent 模式：允许直接在当前项目目录读写文件与执行验证命令/g, "Agent Write Mode: read/write files & run commands in this project"],
  [/只读规划模式：仅读取分析当前项目，不改动文件/g, "Ask Read-Only Mode: analyze code without modifying files"],
  [/放大 \/ 还原终端高度/g, "Maximize / restore terminal height"],
  [/收起终端面板/g, "Hide terminal panel"],
  [/更多操作/g, "More actions"],
  [/在当前项目工作台中将此账号设为默认主力智能体/g, "Set as active Primary Agent in Project Workspace"],
  [/在当前项目中将此账号设为默认主力智能体/g, "Set as active Primary Agent in Project Workspace"],
  [/在内置隔离终端一键登录授权此账号/g, "Sign in via integrated isolated terminal"],
  [/为保障零封号风险，Hyperion 绝不抓取桌面端内部私有接口；额度由官方按 5h\/7d 订阅周期管控/g, "Zero-ban-risk compliance: Hyperion never scrapes internal desktop RPCs; quotas are enforced by the provider's 5h/7d plan windows"],
  [/待安装官方 `agy` CLI · 按订阅周期限额/g, "Install official `agy` CLI · Plan-limited"],
  [/待安装官方 `agy` CLI · 支持一键终端安装/g, "Install official `agy` CLI · 1-click install"],
  [/官方 `agy acp` 就绪 · 按订阅周期限额/g, "Official `agy acp` ready · Plan-limited"],
  [/官方 `agy acp` 标准协议就绪/g, "Official `agy acp` protocol ready"],
  [/官方 `agy` 额度\s*(\d+)%/g, "Official `agy` quota $1%"],
  [/待登录 · 请在终端执行 `agy acp --login` 授权/g, "Sign in via `agy acp --login` in terminal"],
  [/待装 CLI · 订阅限额/g, "Install CLI · Plan-limited"],
  [/待登录 · 订阅限额/g, "Sign in · Plan-limited"],
  [/按官方周期限额/g, "Provider-limited"],
  [/订阅配额/g, "Plan Quota"],
  [/本地算力/g, "Local Compute"],
  [/离线免额/g, "Offline Free"],
  [/API 通道/g, "API Channel"],
  [/按量计费/g, "Pay-as-you-go"],
  [/留空按服务商官方计费\/限额，如填 2000000/g, "Leave blank for provider billing/quota, e.g. 2000000"],
  [/Orchestrator 多模编排/g, "Orchestrator Routing"],
  [/旗舰深度架构推理与复杂多文件排障模型/g, "Flagship deep architectural reasoning & multi-file debugging"],
  [/高判断力生产级研发与多子智能体编排主力/g, "High-judgment production coding & subagent orchestration"],
  [/长程系统规划与跨模块工程推理模型/g, "Long-horizon system design & cross-module reasoning"],
  [/高速低耗能日常编码与并行执行引擎/g, "High-efficiency rapid coding & parallel worker engine"],
  [/自动协同 Neon 规划与 Hydrogen 高速执行以节省配额/g, "Pairs Neon planning with Hydrogen execution to save quota"],
  [/自动协同 Carbon 深度架构与并行执行子智能体/g, "Pairs Carbon architecture with parallel execution subagents"],
  [/自动协同 Argon 系统设计与高效验证链路/g, "Pairs Argon system design with fast verification workers"],
  [/高吞吐低延迟日常工程主力模型/g, "High-throughput low-latency workhorse engineering model"],
  [/Antigravity 内置 Claude Opus 深度推理通道/g, "Built-in Claude Opus deep reasoning channel"],
  [/Antigravity 内置 Claude Sonnet 高效编码通道/g, "Built-in Claude Sonnet coding channel"],
  [/Antigravity 托管 120B 开源推理模型/g, "Antigravity-hosted 120B open reasoning model"],
  [/Antigravity CLI \(官方 agy acp\)/g, "Antigravity CLI (Official agy acp)"],
  [/在内置终端执行 `agy acp --login` 即可通过官方标准 ACP 协议驱动（零封号风险）/g, "Run `agy acp --login` in terminal for official standard ACP integration (zero ban risk)"],
  [/采用 Google 官方公开 Agent Client Protocol \(`agy acp` JSON-RPC 2\.0\) 通信，与 Zed \/ JetBrains 官方集成同源。/g, "Uses Google's official public Agent Client Protocol (`agy acp` JSON-RPC 2.0), identical to Zed / JetBrains integrations."],
  [/✓ 已创建项目「(.+)」并打开工作台/g, '✓ Created project "$1"'],
  [/✓ 已接入免客户端智能体[“"](.+?)[”"]并自动加入指挥舱 @ 名单！?/g, '✓ Added agent "$1" and enrolled in @mention roster!'],
  [/✓ 已保存智能体[“"](.+?)[”"]并加入 @ 名单\s*·\s*(.+)/g, '✓ Saved agent "$1" · $2'],
  [/✓ 默认主力已切换为「(.+?)」/g, '✓ Active Primary Agent switched to "$1"'],
  [/✓ 当前对话主力已切换为「(.+?)」，直接回车即可对话/g, '✓ Active Primary Agent set to "$1"'],
  [/✓ 已打开项目「(.+)」/g, '✓ Opened project "$1"'],
  [/✓ 默认主力已切换为「(.+)」/g, '✓ Primary agent switched to "$1"'],
  [/✓ 当前对话主力已切换为「(.+)」，直接回车即可对话/g, '✓ Primary agent set to "$1" — press Enter to chat'],
  [/✓ 已同步全部账号额度与后台进程状态/g, "✓ Synced all account quotas and background statuses"],
  [/✓ 已刷新“(.+)”状态/g, '✓ Refreshed "$1"'],
  [/已切换界面风格：/g, "Theme switched to: "],

  // Short Demo / Default Account Names (evaluated last so full placeholder phrases match first!)
  [/快速跳转项目、会话、视图或执行命令…/g, "Jump to project, thread, view, or run command…"],
  [/切换侧边栏 \(Ctrl\+B\)/g, "Toggle Sidebar (Ctrl+B)"],
  [/打开快速命令面板 \(Ctrl\+K\)/g, "Open Command Palette (Ctrl+K)"],
  [/加宽 \/ 还原面板/g, "Widen / Restore Drawer"],
  [/显示 \/ 隐藏密钥/g, "Show / Hide API Key"],
  [/重试此轮指令/g, "Retry this turn"],
  [/编辑并重新发送/g, "Edit and resend"],
  [/模型覆盖\(可选\)/g, "Model override (optional)"],
  [/可为同一账号添加多个不同模型并发竞技/g, "Add multiple model slots on the same account to benchmark models head-to-head"],
  [/已发送指令，正在连接智能体并开始思考…/g, "Connecting to agent and starting reasoning…"],
  [/账号\s*(\d+)/g, "Account $1"],
  [/反重力主力号/g, "Antigravity Primary"],
  [/反重力二号/g, "Antigravity Backup"],
  [/原始反重力/g, "Antigravity Default"],
  [/原始\s*Antigravity/g, "Antigravity Default"],
  [/原始\s*Codex/g, "Codex Default"],
  [/原始\s*Claude/g, "Claude Default"],
  [/新账号/g, "New Account"],
  [/Pro订阅号/g, "Pro Subscription"],
  [/([A-Za-z0-9_-]+)主力号/g, "$1 Primary"],
  [/([A-Za-z0-9_-]+)独立号/g, "$1 Isolated"],
  [/GeminiPro直连/g, "GeminiPro Direct"],
  [/DeepSeek架构师/g, "DeepSeek Architect"],
  [/OpenCode终端/g, "OpenCode CLI"],
  [/就绪/g, "Ready"],
];

function applyPhraseRules(str) {
  if (!str || !CJK_RE.test(str)) return str;
  let out = str;
  for (const [re, rep] of PHRASE_RULES) {
    out = out.replace(re, rep);
    if (!CJK_RE.test(out)) break;
  }
  return out;
}

export function translateTextToEn(raw) {
  if (!raw || !CJK_RE.test(raw)) return raw;
  const trimmed = raw.trim();
  if (!trimmed) return raw;

  // 1. Exact match (preserving leading/trailing whitespace!)
  if (EXACT_MAP.has(trimmed)) {
    const idx = raw.indexOf(trimmed);
    return raw.slice(0, idx) + EXACT_MAP.get(trimmed) + raw.slice(idx + trimmed.length);
  }

  // 2. Structured full-string regex match (+ phrase pass on captured groups if needed)
  for (const [re, fn] of REGEX_RULES) {
    const m = trimmed.match(re);
    if (m) {
      const idx = raw.indexOf(trimmed);
      const replaced = applyPhraseRules(fn(...m));
      return raw.slice(0, idx) + replaced + raw.slice(idx + trimmed.length);
    }
  }

  // 3. Phrase / substring replacement for mixed text nodes & dynamic statuses
  return applyPhraseRules(raw);
}

export function getUiLanguage() {
  return currentLang;
}

export function setUiLanguage(lang) {
  currentLang = lang === "en" ? "en" : "zh";
  try {
    localStorage.setItem("ade_ui_lang", currentLang);
  } catch {}
  document.documentElement.lang = currentLang === "en" ? "en" : "zh-CN";
  applyLanguageToRoot(document.body);
  const btn = document.getElementById("btn-toggle-lang");
  if (btn) {
    btn.textContent = currentLang === "en" ? "中文" : "EN";
    btn.title = currentLang === "en" ? "切换为简体中文界面" : "Switch UI to English";
  }
}

export function applyLanguageToRoot(rootEl) {
  if (!rootEl) return;
  isApplying = true;
  try {
    const walker = document.createTreeWalker(rootEl, NodeFilter.SHOW_TEXT);
    let node;
    while ((node = walker.nextNode())) {
      const parent = node.parentElement;
      if (!parent) continue;
      const parentTag = parent.tagName;
      if (parentTag === "SCRIPT" || parentTag === "STYLE" || parentTag === "CODE" || parentTag === "PRE" || parentTag === "SVG") continue;
      if (parent.id === "btn-toggle-lang") continue;
      if (parent.closest(".diff-code, .xterm, svg")) continue;
      if (parent.closest(".md-body") && !parent.hasAttribute("data-copy-code")) continue;

      if (currentLang === "en") {
        // Detect fresh Chinese updates from JS even on reused DOM nodes
        if (node.__origZh === undefined || node.nodeValue !== node.__lastEn) {
          node.__origZh = node.nodeValue;
        }
        const nextVal = translateTextToEn(node.__origZh);
        node.__lastEn = nextVal;
        if (node.nodeValue !== nextVal) node.nodeValue = nextVal;
      } else if (node.__origZh !== undefined) {
        if (node.nodeValue === node.__lastEn && node.nodeValue !== node.__origZh) {
          node.nodeValue = node.__origZh;
        }
        delete node.__origZh;
        delete node.__lastEn;
      }
    }

    const withSelf = (selector) => {
      const list = rootEl.querySelectorAll ? Array.from(rootEl.querySelectorAll(selector)) : [];
      if (rootEl.matches?.(selector)) list.unshift(rootEl);
      return list;
    };

    // Translate placeholders (including dynamically updated composer placeholder!)
    withSelf("input[placeholder], textarea[placeholder]").forEach((el) => {
      if (currentLang === "en") {
        if (el.__origPh === undefined || el.placeholder !== el.__lastEnPh) {
          el.__origPh = el.placeholder;
        }
        const nextPh = translateTextToEn(el.__origPh);
        el.__lastEnPh = nextPh;
        if (el.placeholder !== nextPh) el.placeholder = nextPh;
      } else if (el.__origPh !== undefined) {
        if (el.placeholder === el.__lastEnPh) el.placeholder = el.__origPh;
        delete el.__origPh;
        delete el.__lastEnPh;
      }
    });

    // Translate hover tooltips (`title` attributes)
    withSelf("[title]").forEach((el) => {
      if (el.id === "btn-toggle-lang") return;
      if (currentLang === "en") {
        if (el.__origTitle === undefined || el.title !== el.__lastEnTitle) {
          el.__origTitle = el.title;
        }
        const nextTitle = translateTextToEn(el.__origTitle);
        el.__lastEnTitle = nextTitle;
        if (el.title !== nextTitle) el.title = nextTitle;
      } else if (el.__origTitle !== undefined) {
        if (el.title === el.__lastEnTitle) el.title = el.__origTitle;
        delete el.__origTitle;
        delete el.__lastEnTitle;
      }
    });
    // Translate <optgroup label="...">
    withSelf("optgroup[label]").forEach((el) => {
      if (currentLang === "en") {
        if (el.__origLbl === undefined || el.label !== el.__lastEnLbl) {
          el.__origLbl = el.label;
        }
        const nextLbl = translateTextToEn(el.__origLbl);
        el.__lastEnLbl = nextLbl;
        if (el.label !== nextLbl) el.label = nextLbl;
      } else if (el.__origLbl !== undefined) {
        if (el.label === el.__lastEnLbl) el.label = el.__origLbl;
        delete el.__origLbl;
        delete el.__lastEnLbl;
      }
    });
  } finally {
    isApplying = false;
  }
}

export function installI18nObserver() {
  if (observerInstalled) return;
  observerInstalled = true;
  const pendingRoots = new Set();
  let flushScheduled = false;

  const flushPending = () => {
    flushScheduled = false;
    if (isApplying || currentLang !== "en" || pendingRoots.size === 0) {
      pendingRoots.clear();
      return;
    }
    const roots = Array.from(pendingRoots);
    pendingRoots.clear();
    for (const r of roots) {
      if (!r.isConnected) continue;
      // Skip if an ancestor of r is already in this batch
      if (roots.some((other) => other !== r && other.contains?.(r))) continue;
      applyLanguageToRoot(r);
    }
  };

  const obs = new MutationObserver((mutations) => {
    if (isApplying || currentLang !== "en") return;
    for (const m of mutations) {
      if (m.type === "attributes" && m.target?.nodeType === 1) {
        pendingRoots.add(m.target);
      } else if (m.type === "characterData" && m.target?.parentElement) {
        pendingRoots.add(m.target.parentElement);
      } else if (m.type === "childList") {
        let anyElement = false;
        for (const n of m.addedNodes) {
          if (n.nodeType === 1) {
            pendingRoots.add(n);
            anyElement = true;
          }
        }
        if (!anyElement && m.target?.nodeType === 1) {
          pendingRoots.add(m.target);
        }
      }
    }
    if (pendingRoots.size > 0 && !flushScheduled) {
      flushScheduled = true;
      queueMicrotask(flushPending);
    }
  });
  obs.observe(document.body, {
    childList: true,
    subtree: true,
    characterData: true,
    attributes: true,
    attributeFilter: ["placeholder", "title"],
  });
}
