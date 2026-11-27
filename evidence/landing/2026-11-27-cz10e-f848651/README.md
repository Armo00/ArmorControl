# 2026-11-27 · CZ-10E 两趟实机复测证据

[下载两趟飞行遥测](CZ10E-telemetry.zip) · [实测报告](../../../docs/landing/2026-11-27-cz10e-f848651.md)

归档保留两趟的整箭 CSV、逐发动机 CSV、Final JSONL、飞行采样和事件 JSONL 原始字节，以及比较结果、配置与离线检查。KSP 日志保留与本次发射和触水有关的摘录。含环境和联系信息的游戏总日志保存在测试机本地。公开部署回执保留版本与哈希。

第一趟遥测 T+0 为 UT `782607809.515`，对齐复测为 UT `782607807.475`。用 `ut` 对齐整箭和逐发动机，用文件 `repeat-flight-samples.jsonl` 与 `repeat-flight-events.jsonl` 查看对齐复测的自动化状态和接触后倾斜。两趟各有三份 `native/` 原始日志，文件按启动时刻排列。

ZIP SHA-256

```text
0a1278a72572050d0da524d82b94621a257ecde941e8a61fc868b542b35a9052  CZ10E-telemetry.zip
```

| 文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| `native/flight-20261127-070134798.csv` | 1814234 | `5423ca89076296b8388e811632dadb6d52463e265201107d676835616ff8f5c2` |
| `native/flight-20261127-070718200.csv` | 1787694 | `39e6f34360ae295ffa76f7334823a5e599130cefb3a34f6f9bb3d1994fdea831` |
| `native/flight-engines-20261127-070134798.csv` | 3611080 | `c62530cb7c5ba4b85d3eea56229e0f0ac622804567abe259fa40ab9b6a4fd1ca` |
| `native/flight-engines-20261127-070718200.csv` | 3585539 | `d3eb39aecacb6d830d456f0b0b3fd31a3c04af9e031b36364c28b5b45692158a` |
| `native/flight-final-20261127-070610643-9d0295067a0547848e931078df4adc84.jsonl` | 597999 | `eb3f3d8445309593c364ec337ffeb99e10c633bf6bfd19e8bb43ea5bed4bc5f9` |
| `native/flight-final-20261127-071151414-35dfca568e8046c28156c122c32c170c.jsonl` | 707503 | `dab90bd867df1d6591be8f0f4899c4b45029d4aae3d220c2d5b2838186d8b202` |
| `flight-events.jsonl` | 166145 | `016a5e03a0df9d89b4b3d61cc2e35d3a3a3aee4c6df94dd6bdfccf1754545886` |
| `flight-samples.jsonl` | 16889308 | `e8037b6d91f5ea8409adc61d4122a7d526c031b9b826e6a39c41ec919bcc1f8c` |
| `recovery-settings-used.cfg` | 827 | `f856baf249d9c12c1028bfd555de1a2f39fdb1c86f1bb257434d872977e0e6c6` |
| `native-summary.json` | 22057 | `a4abeedbeca6c74d4c1be90d87f9e0b7c2e300ea5d1367c40a26436e2d9d4b68` |
| `stack-parts.json` | 17863 | `b6fbc9e4a2f43d3662ee34ff737443e1f160b883cc657fe14af0fc40e62b3e48` |
| `booster-parts.json` | 13245 | `31b2cdac1383add1a722fc0773d215b2591dcf61d21e484148a696857c0a9a88` |
| `waypoint-source.json` | 244 | `23f3c9ee2683b6196195e30e7249b150a7a4773407d257510860da1752f5f068` |
| `deployment-receipt.json` | 258 | `ce66d2ce99425e5ca95924bb88c42733d668d1c51cf16ec1f392f8d22c08dbf2` |
| `repeat-flight-events.jsonl` | 53667 | `0b0c95580510ba0dc34f679bfd42898aa44af01582cab69276826d3163ed5251` |
| `repeat-flight-samples.jsonl` | 16043276 | `feb5feb833f217fa8bf7de4d13ff09f34453d704130a0411825fea658893d063` |
| `comparison.json` | 50346 | `a388b6582bcbb0f7ae4661aa661e2a39ee50d3590bd60718556097caf7b3b835` |
| `repeat-comparison.json` | 45736 | `777667108a6a5f5dd28aad7ac92fb3b3c75c7109b6ab70878a61315ace07a6a2` |
| `ksp-flight-excerpt.log` | 4274 | `9c67bac9c662bfb11596fda7884ddd0c9a9aeaf079e64f654284c2c6afb33d1b` |
| `compiled-logic-results.txt` | 402 | `95e0acdb3504b53f9c4f0ffae49f7e4b177d26847cca172d311cfebab8a612bc` |
| `report.md` | 6369 | `76e68b3e9c3e1b4981a7e4f3b245430eb4b58c54f0664a63f849c8733a9b702a` |
