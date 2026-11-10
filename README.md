# 卫戍协议 · 实时观察器

用于《明日方舟》PC 客户端「卫戍协议·规程·序列 I」的独立 Windows 工具。它从正在运行的游戏进程读取战斗数据，无需在游戏里打开敌方情报或强化面板。官服和 B 服可以尝试使用同一份程序。

**[直接下载 StrongholdObserver.exe（Windows x64）](https://github.com/WinRNGS/arknight-StrongholdObserver/releases/latest/download/StrongholdObserver.exe)** · [发布页和校验和](https://github.com/WinRNGS/arknight-StrongholdObserver/releases/latest)

1. 下载后双击 `StrongholdObserver.exe`。这是单文件版，已内置 .NET 运行库和名称目录，无需安装、解压或登录。
2. 启动兼容版本的游戏，进入「卫戍协议·规程·序列 I」对局，在观察器中点 **连接游戏**。
3. 如果显示 Windows 错误 5，且你愿意授予管理员权限，可在观察器内点 **以管理员权限重开**。

请把程序放在有写入权限的文件夹中，例如「下载」，以便保存历史。程序未作代码签名，Windows 可能显示提示。请从本仓库的发布页下载，可用同页的 `SHA256SUMS.txt` 校验文件。发布页的 `Source code` 压缩包是源码。

### 让 Agent 下载

把下面这条指令交给能下载文件的 Agent 即可，或自行把链接粘贴到浏览器。无需提供账号、密码或 API Key。

> 请下载 https://github.com/WinRNGS/arknight-StrongholdObserver/releases/latest/download/StrongholdObserver.exe ，保存到「下载」文件夹，完成后告诉我文件位置。

### 能看到什么

| 页面 | 内容 |
| --- | --- |
| 本波敌人 | 显示当前或最后观测到的生命，以及最大生命、攻击、防御、法抗、攻速。可点表头排序，选中敌人可与赛季基准对比。若未采到归零就离场，会保留最后实测值并标记「已离场」。 |
| 回合强化 | 显示强化效果、末次份数、本波同时读到的峰值和已见波数。选中敌人后可按名单查看适用倍率和排除项；模拟强度和认证倍率是难度信息。 |
| 我方战报 | 按干员 ID 展示客户端记账的累计伤害、治疗和阶段平均 DPS。战斗计时重置时分段；治疗可能包含溢出，采样也可能错过退场前的最后一帧。 |
| 历史与导出 | 首次使用没有预置对局；之后在程序旁的 `记录/history.json` 保存最近 12 局。可以回看历史，同时继续记录当前战斗，也可以把所选波次导出为 JSON。 |

三张表默认按表头和内容调整列宽，再留出三个字符的空间。拖动表头分隔线可手动调整，列宽会保存在程序旁的 `记录/列宽.json`；窗口较窄时可横向滚动查看右侧列。1.2.3 也调整了主标题、工具栏、下拉框和标签页的间距，避免在高缩放下挤压或遮挡。

表格下方的详情会自动换行并按文字增高；文字较多时，可在详情区向下滚动查看完整说明。

### 兼容性与数据

- 从 1.2.1 起，观察器不再核对游戏文件的固定哈希。程序仍需识别游戏的战斗对象、活动和回合数据，因此客户端的战斗数据布局须兼容。此前的 B 服版本已有实测，当前版通过了离线样本测试，尚未在官服和 B 服分别复测。发布页的 SHA-256 只用于校验下载的观察器。
- 观察器本身不联网，不需要登录或配置密钥。它只读游戏进程，不截屏、不注入、不修改游戏文件或内存。历史和导出均保存在本地；对外分享导出时请留意其中的对局时间和战斗数据。
- 这是非官方工具。读取进程仍可能受游戏规则或反作弊机制限制。

### 从源码构建

在 Windows x64 上安装 [.NET 8 SDK 8.0.427](https://dotnet.microsoft.com/download/dotnet/8.0)，然后在仓库根目录执行：

```powershell
dotnet publish .\src\Observer.csproj -c Release -o .\publish
```

生成的 `publish/StrongholdObserver.exe` 可独立运行。随程序分发的 .NET 运行库来自 [Microsoft .NET](https://dotnet.microsoft.com/)；[组件许可与声明](THIRD-PARTY-NOTICES.txt)见仓库。
