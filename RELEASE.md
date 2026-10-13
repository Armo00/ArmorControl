# ArmorControl 发布约定

- 固定 Release 存放目录：`D:\KSP\KSP_Git\ArmorControl_Release`
- 当前开发版本：`0.1.2`（未发布）。本轮仅提交并推送源码，不创建标签、不生成安装包、不上传 Release。
- 当前导出版本：`0.1.1`（导出不代表已上传发布）
- 安装包：`ArmorControl-0.1.1.zip`
- 校验文件：`ArmorControl-0.1.1.zip.sha256`（SHA-256）
- 包内根目录为 `GameData/ArmorControl`，可解压合并到 KSP 安装目录。
- 仅发布运行时 DLL、网页（包含 Localization）、默认配置、中英 README 和 CHANGELOG。
- 不发布 Git 元数据、源码、审核/编译中间产物或个人 settings.cfg。
- 本地专用打包入口：KSP 根目录下 `BuildTools/ArmorControl.PackageRelease.ps1 -Version 0.1.1`。
  脚本执行编译及离线回归验证；同名发布包已存在时拒绝覆盖。
- 当前开发程序集的数字版本为 `0.1.2.0`；上一次导出的 `0.1.1` 安装包对应程序集版本 `0.1.1.0`。
