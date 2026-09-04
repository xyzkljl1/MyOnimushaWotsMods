# NexusUpdater

> 本说明主要供 Codex 在本项目中执行 Nexus Mods 发布任务时阅读和遵循。

这是一个独立发布工具，不是 REFramework mod。它把仓库根目录中由
`PackageMods.ps1` 生成的 `.7z` 上传为 Nexus Mods 上**已有文件**的新版本，并同步：

- 文件内容与文件版本；
- 模组页面版本号；
- 对应版本的 changelog。

它不会创建 Nexus 模组页面或新文件条目。第一次发布仍需在 Nexus 网站上手工完成，
之后把已有的全局 `modId` 配给本工具即可。工具会在上传前自动选择该模组唯一的
有效文件。

## 配置

复制 `Updater/updater.example.json` 为 `Updater/updater.json`，然后按模组填写：

```json
{
  "NEXUSMODS_API_KEY": "",
  "mods": {
    "ShowHP": {
      "modId": "Nexus-v3-global-mod-id"
    }
  }
}
```

`NEXUSMODS_API_KEY` 用于填写 API key；示例文件中始终留空。工具只从本地
`updater.json` 读取密钥，不使用环境变量、交互输入或命令行参数。
`updater.json` 已被 `.gitignore` 排除，但其中的密钥仍是明文，不能分享、提交或
复制到日志中。

这里的 `modId` 是 v3 API 的全局 ID，不一定等于页面 URL 中的游戏内编号；可按
Nexus 官方说明通过 `GET /v3/games/{game_domain}/mods/{game_scoped_id}` 查询响应中的
`id`。

每个模组只有 `modId` 是必填项。上传开始前，工具会调用
`GET /mods/{modId}/files`，筛选 `is_active == true` 的文件，并只在结果恰好为一个时
继续。没有有效文件或存在多个有效文件都会直接终止，以免更新错误文件。历史版本
属于同一个持久化文件，不影响该选择。

可选配置字段包括 `displayName`、`description`、`fileCategory`、`archiveExistingVersion`、
`primaryModManagerDownload`、`allowModManagerDownload` 和
`showRequirementsPopUp`。不填写时分别使用自动名称、`main` 分类和 Nexus 默认行为。
`update_mod_version` 始终启用，因此不提供关闭选项。

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
