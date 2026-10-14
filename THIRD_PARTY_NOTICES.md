# 第三方依赖、版权与来源记录

项目作者于2026-10-14决定采用 **GPL-3.0-only**，正文见 LICENSE。该声明不替代第三方各自的许可，也不把来源未确认的材料声明为项目作者原创。

## Jellyfin 参考组件

版权/作者声明：**Jellyfin Contributors**。官方固定10.11.11 tag 的下列csproj均声明 GPL-3.0-only：

| 组件 | 固定来源 |
|---|---|
| Emby.Naming | https://github.com/jellyfin/jellyfin/blob/v10.11.11/Emby.Naming/Emby.Naming.csproj |
| Jellyfin.Data | https://github.com/jellyfin/jellyfin/blob/v10.11.11/Jellyfin.Data/Jellyfin.Data.csproj |
| MediaBrowser.Common | https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Common/MediaBrowser.Common.csproj |
| MediaBrowser.Controller | https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/MediaBrowser.Controller.csproj |
| MediaBrowser.Model | https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Model/MediaBrowser.Model.csproj |
| Jellyfin.Extensions（命名测试） | https://github.com/jellyfin/jellyfin/blob/v10.11.11/src/Jellyfin.Extensions/Jellyfin.Extensions.csproj |

获取自[官方固定版本分发](https://repo.jellyfin.org/?path=/server/windows/stable/v10.11.11/amd64)，版本/归档摘要/DLL摘要见 scripts/dependencies.lock.json。参考DLL不提交到源码仓库，也不随插件ZIP分发。

Jellyfin根LICENSE为GPLv2文本，不能用它概括上述组件自己的GPL-3.0-only声明。[官方插件模板](https://github.com/jellyfin/jellyfin-plugin-template/blob/master/README.md#licensing)说明GPLv3与插件链接的关系。本项目没有复制 targeted-scans/autoscan/autopulse 的研究源码、完整Jellyfin源码快照或已退役的历史实验工程。

上游分发只提供MD5。本项目的归档SHA256来自校验过的官方HTTPS下载，已交叉比对上游MD5；各参考DLL另核SHA256和程序集ABI。没有取得上游数字签名/SHA256认证，不能宣称已验证官方签名。

## .NET 构建工具及框架

SDK9.0.318来自Microsoft官方分发，使用官方release metadata的SHA512。已下载Windows归档的LICENSE.txt为 **Microsoft .NET Library License**，ThirdPartyNotices.txt另列Apache-2.0、BSD、Unicode等第三方声明。保留其原有版权和通知，不把WindowsSDK分发整体概括为MIT或重新许可为GPL。

SDK位于忽略目录.local，不随源码或插件分发。ASP.NET Core及.NET运行框架由宿主提供，不随插件复制。[官方.NET许可说明](https://github.com/dotnet/core/blob/main/license-information.md)。构建工具许可与本项目源码许可分别适用。

## 历史来源不确定性

正式C#/HTML来自原有效0.2版本迁移；有效测试和公共工具由必要功能提取而来，新构建工具与文档在工程整理中编写。缺少完整历史提交、贡献者和摘录记录，不能仅据迁移过程或无版权头认定全部为作者原创。

本轮未发现具体授权冲突，但来源确认仍为待办。应继续补充贡献者、引用来源及必要通知；如发现无法兼容的第三方材料或实质权属争议，应暂停相应分发并处理，不用本项目LICENSE覆盖它。

## 分发边界

历史阶段已在Private仓库发布v0.2.0.0 Release及对应源码标签；本轮用户明确授权在安全和权属门禁通过后公开现有源码仓库，未授权生产部署。本轮不创建新Release，也不替换既有安装包。插件ZIP包含LICENSE和本说明；历史附件中的Private表述是发布时快照，当前仓库可见性以docs/PROJECT.md及GitHub实际状态为准。

面向他人分发二进制时须满足GPL对应源码、构建说明和必要通知等义务；原v0.2.0.0二进制对应同名Git标签，不能以之后main的文档更新冒充其构建来源。历史记录不完备的事实继续保留，仓库可见性不消除未授权复制问题。
