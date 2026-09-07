# ArmorControl Localization

默认语言为简体中文。网页右上角可切换简体中文 / English，无需重载页面。
选择保存在当前浏览器的 localStorage（armorControlLanguage），不会改变其他设备。
游戏内服务面板独立使用 settings.cfg 中的 language，缺省 zh-CN。

## 修改翻译

- zh-CN.json：简体中文；en-US.json：英文。
- languages.json：语言清单、显示名称及默认语言。
- JSON 使用 UTF-8，messages 下的值必须是字符串。
- 当前采用原始界面文案作为键。修改右侧译文，不要随意改键。
- 完整句子优先匹配；旧的动态拼接文案按最长词条匹配，数值保持不变。
- 新增动态文案优先使用完整句子和 {0}、{name} 占位符；两份词典保持键一致。
- 翻译只作为文本显示，不支持 HTML。占位符、单位和专有名词应保留。
- 不翻译控件 value、data-*、协议命令或枚举。代码判断使用 ArmorI18n.sourceText，
  不得依据已经翻译的 textContent 判断控制模式。
- 载具名、宇航员名、零件名称等用户/游戏数据不自动翻译。第三方模组提供的
  零件菜单名称、异常详情等可能保留游戏或模组语言；需要针对其原文补充词条。

新增网页语言时复制一份词典，修改 locale，在 languages.json 注册 code/name。
游戏内面板当前只提供 zh-CN/en-US；新增语言还需扩展 C# 中的语言校验和选择按钮。
语言文件加载失败保留当前语言；缺少词条则显示原文。

运行离线检查：

    node BuildTools/ArmorControl.LocalizationTests.cjs
    node BuildTools/ArmorControl.ReviewRegression.cjs

网页刷新后会重新读取词典；游戏内词典在切换语言或下次启动时读取。

## Contributing (English)

Edit the string values in en-US.json, keeping source keys identical to zh-CN.json.
Preserve placeholders and units. Add complete sentences for new UI strings; legacy
dynamic text also supports longest-first phrase matching. Translations are plain
text, never HTML. Do not translate protocol values, commands, or user-generated names.
Register new web locales in languages.json. Language preference is per browser.
Run both offline tests above before submitting changes.
