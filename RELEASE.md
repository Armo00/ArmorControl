# ArmorControl 发布约定

- 固定 Release 存放目录：`D:\KSP\KSP_Git\ArmorControl_Release`
- 当前发布版本：`0.1`
- 安装包：`ArmorControl-0.1.zip`
- 校验文件：`ArmorControl-0.1.zip.sha256`（SHA-256）
- 包内根目录为 `GameData/ArmorControl`，可解压合并到 KSP 安装目录。
- 仅发布运行时 DLL、网页（包含 Localization）、默认配置和 README。
- 不发布 Git 元数据、源码、审核/编译中间产物或个人 settings.cfg。
- 本地专用打包入口：KSP 根目录下 `BuildTools/ArmorControl.PackageRelease.ps1 -Version 0.1`。
  脚本执行编译及离线回归验证；同名发布包已存在时拒绝覆盖。
- .NET 程序集的数字版本可能规范化为 `0.1.0.0`；对外发布版本为 `0.1`。
