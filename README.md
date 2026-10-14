# ScopedImport：Jellyfin 定向入库插件

按指定路径追加缺失的电影、剧目、季和集，通过 Jellyfin 服务保存条目。插件不主动调用全库扫描、父级刷新或完整元数据刷新。它在 Jellyfin 内运行，无需 Python 助手。

当前版本 **0.2.0.0**。仅支持 **Windows + 原版 Jellyfin 10.11.11**；其他版本或仓储替换插件可能使追加保护无法启用。

## 安装与使用

本项目采用 **GPL-3.0-only**，见 [LICENSE](LICENSE)。第三方声明及历史来源不确定性见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。最终工程版 [v0.2.0.0 Release](https://github.com/gok2023/jellyfin-scoped-import/releases/tag/v0.2.0.0) 提供安装 ZIP 和 SHA256 校验文件。仓库仍为 Private，仅授权协作者可访问；已通过35项安全离线检查，真实集成/重启验证尚未完成。

1. 完全停止 Jellyfin，并保留自己的数据与原插件备份。
2. 从上述 Release 下载 `ScopedImport-0.2.0.0-jf10.11.11.zip` 和同名 `.sha256`，用 PowerShell 的 `Get-FileHash -Algorithm SHA256` 核对摘要，再解压发布包，将 `ScopedImport_0.2.0.0` 放到该实例数据目录的 `plugins` 下。升级时把其他 ScopedImport 版本移出插件目录，避免同时加载。不要复制测试插件或参考程序集。
3. 启动 Jellyfin，在管理菜单打开“定向入库”，或访问服务器地址下的 `/ScopedImport/App`。用 Jellyfin 管理员账户登录；不要直接打开本地 HTML 文件。
4. 输入服务器可访问的完整目录或视频路径，选择“按媒体库设置获取元数据”或“仅本地 NFO”，提交一次并等待结果。
5. HTTP 202 表示排队，只有操作结果能说明完成、拒绝或部分完成。不要在状态未知时重复提交。

支持库根下的独立单片目录（一个主视频），以及电视剧库中的剧目/季/单集；支持 SxxEyy、NFO 和 E01 单集子目录。已有条目跳过；元数据只处理新条目，按库启用的远程提供器及顺序补缺。

限制：不支持 ISO、STRM、光盘目录、复杂多版本和季集歧义；库物理根必须已经初始化。拒绝库根、UNC、越界和重解析路径。部分完成不会自动回滚；重试也不会修复已有条目缺失的元数据。回退插件代码不会撤销已创建条目。

仓储代理不是第三方插件的系统沙箱；合法后台扫描仍可能改变条目。超时是软限制，尚无所有进程 I/O 的完整证明。

## 从源码构建

需要 Windows、PowerShell 7、Node.js（HTML mock）、Python 3.10+（离线隔离门禁测试，无第三方包）。.NET SDK **9.0.318** 由 bootstrap 下载到忽略目录，无需全局安装。

在项目根执行：

```powershell
pwsh -NoProfile -File scripts/bootstrap.ps1
pwsh -NoProfile -File scripts/build.ps1
```

bootstrap 校验固定官方 SDK 和 Jellyfin 分发包，只提取本工程需要的参考 DLL，不启动服务。首次下载约 445 MiB；无需复制任何旧工程内容。固定来源、摘要和 ABI 在 `scripts/dependencies.lock.json`。

build 使用本轮独立输出目录，校验程序集版本、Jellyfin ABI、GUID 与嵌入页面，执行离线测试后打包到 `artifacts/build-*/`。ZIP 只含正式 DLL、meta 和文档/校验文件，不含 SDK、服务器、FFmpeg、凭据、数据库或测试插件。不要从旧 bin/dist 手动选 DLL。

单独执行离线测试：

```powershell
pwsh -NoProfile -File scripts/test.ps1
```

`build.ps1 -SkipTests` 仅用于显式诊断，构建清单会记录跳过，不能作为完整验收。集成测试需要另外证明隔离的一次性宿主；默认禁止网络请求，不会连接生产服务。运行条件和副作用见 [测试说明](tests/README.md)。

## 项目导航

- [当前技术状态与验收](docs/PROJECT.md)：架构、决策、证据、风险和下一步。
- [开发规则](AGENTS.md)：后续 Codex 的读取、修改、验证和收尾要求。
- [版本变化](CHANGELOG.md)。
- [第三方依赖与许可待办](THIRD_PARTY_NOTICES.md)。

本项目不提供自动生产部署器，不维护旧独立助手、历史实验或生产备份。
