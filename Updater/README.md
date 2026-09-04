# NexusUpdater

> 本说明主要供 Codex 在本项目中执行 Nexus Mods 发布任务时阅读和遵循。

这是一个独立发布工具，不是 REFramework mod。它把仓库根目录中由
`PackageMods.ps1` 生成的 `.7z` 上传到已有的 Nexus Mods 模组页面，并同步：

- 文件内容与文件版本；
- 模组页面版本号；
- 对应版本的 changelog。

它不会创建 Nexus 模组页面。对于还未公开、文件列表完全为空且 API key 所属用户有权
管理的模组页面，工具可以创建首个 Main File；后续则自动选择该模组唯一的有效文件并
上传新版本。只需先在 Nexus 网站上创建模组页面，再把全局 `modId` 配给本工具。

## 配置

复制 `Updater/updater.example.json` 为 `Updater/updater.json`，然后按模组填写：

```json
{
  "NEXUSMODS_API_KEY": "",
  "mods": {
    "ShowHP": {
      "modId": "Nexus-v3-global-mod-id",
      "description": "本版本的文件说明"
    }
  }
}
```

`NEXUSMODS_API_KEY` 用于填写 API key；示例文件中始终留空。工具只从本地
`updater.json` 读取密钥，不使用环境变量、交互输入或命令行参数。
`updater.json` 已被 `.gitignore` 排除，但其中的密钥仍是明文，不能分享、提交或
复制到日志中。

Codex 执行某个 mod 的发布任务时，如果 `mods` 中没有该 mod 对应的对象，应自行在
本地 `updater.json` 中添加。若对象中的 `modId` 缺失、为空或仍是占位符，应根据用户
提供的 Nexus Mods 页面地址、游戏内编号或其它足以确定目标的信息查询并填写正确的
全局 `modId`；信息不足以唯一确定目标时必须先询问用户。更新配置时只能修改对应的
`mods` 条目，必须保留且不得显示、记录或覆盖现有的 `NEXUSMODS_API_KEY`。

这里的 `modId` 是 v3 API 的全局 ID，不一定等于页面 URL 中的游戏内编号；可按
Nexus 官方说明通过 `GET /v3/games/{game_domain}/mods/{game_scoped_id}` 查询响应中的
`id`。

每个模组只有 `modId` 是必填项。上传开始前，工具会调用
`GET /mods/{modId}/files`。若返回的文件列表完全为空，工具会通过 `POST /mod-files`
创建首个 Main File；这也适用于尚未公开但当前 API key 有权管理的模组。只要列表中
存在任何文件记录，即使全部已失效或是历史文件，也不会按初次上传处理。

对于已有文件的模组，工具会筛选 `is_active == true` 的文件，并只在结果恰好为一个时
继续；随后查询该文件的版本，确认本次版本号从未使用过，并要求恰好存在一个
`is_primary == true` 的当前主要版本。没有唯一有效文件、存在多个有效文件、版本号
重复或主要版本不唯一都会在上传前终止。

已有文件的新版本强制沿用当前主要版本的显示名称和文件分类。初次上传时，文件显示
名称取自本地 `modinfo.ini` 的 `name`，分类固定为 `main`；这两项都不能在配置中修改。
唯一的可选配置字段是 `description`，用于设置本次文件说明；留空或不填写时不发送
文件说明。Changelog 由命令行的 `--changelog-file` 提供。工具不会发送归档旧版本、
Mod Manager 下载许可或依赖弹窗等控制字段。初次文件及后续新版本都固定设置
`primary_mod_manager_download = true`，使其成为主要版本；该行为不可配置。
`update_mod_version` 始终启用。

## 使用

先打包并准备 UTF-8 changelog：

```powershell
.\PackageMods.ps1
dotnet run --project .\Updater\NexusUpdater.csproj -- `
  --mod ShowHP `
  --changelog-file .\changelog.txt `
  --dry-run
```

检查通过后去掉 `--dry-run`。工具会从本地 `updater.json` 读取 API key，先显示
目标信息并要求输入 `yes`：

```powershell
dotnet run --project .\Updater\NexusUpdater.csproj -- `
  --mod ShowHP `
  --changelog-file .\changelog.txt
```

自动化运行时可以传入 `--yes`：

```powershell
dotnet run --project .\Updater\NexusUpdater.csproj -- `
  --mod ShowHP `
  --changelog-file .\changelog.txt `
  --yes
```

工具不会打印 API key 或预签名上传 URL，并使用一个完全不携带 API key 的独立
HTTP 客户端访问预签名存储地址。上传前还会拒绝缺失、空白或比模组源文件更旧的
压缩包，以防误发旧构建。

Nexus 当前的 changelog 接口是追加式的：对同一版本重复调用会追加重复内容，而不是
覆盖旧内容。因此同一个版本不要重复运行；如果工具提示“文件版本已创建，但 changelog
更新失败”，应直接在 Nexus 后台补写 changelog，不要重新上传。
