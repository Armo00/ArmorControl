# 2026-11-27 · CZ-10E 定点回收实飞证据

[下载完整遥测 ZIP](CZ10E-telemetry.zip) · [实测报告](../../../docs/landing/2026-11-27-cz10e-cbc6357.md)

该 ZIP 是实飞后生成的原始归档。`native/` 包含整箭、逐发动机和 Final 原始记录。`flight-samples.jsonl` 覆盖从发射前到首触水，`flight-events.jsonl` 保留启动、分级、FAR 就绪、航点和失事事件。配置摘录仅包含本次测试采用的回收参数。ZIP 内 `report.md` 是测试完成时的报告，仓库中链接的版本补充了角度字段和构建来源说明。

对齐整箭和逐发动机时用 `ut`。T+0 的游戏 UT 为 `782607807.355`。返航预测按 `boostback_prediction_ut` 去重，帧开销按 `render_frame` 去重。

ZIP SHA-256

```text
1efc2feb18477992b3284ff3a8d817c3fc9fb8111371edcead1fd1ca461c18fe  CZ10E-telemetry.zip
```

| 文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| `native/flight-20261127-044526398.csv` | 2091287 | `29b7ee0fa1e0609de6760369df6d7e7207c3011f6566ddf16ce8eb4f65d47418` |
| `native/flight-engines-20261127-044526398.csv` | 3439924 | `6f2e0c87447d19c2ef730fa0d7138bad0c5b0b3879f0235996c4e3d3d0d2e21b` |
| `native/flight-final-20261127-044943786-98ae6eb28cb040228d8485a7466e88cb.jsonl` | 387719 | `ae99c26265850593f9a65afe4fb64bace64010bdd5b5bf9995d1ccbfe5fc82fb` |
| `flight-events.jsonl` | 132216 | `721174f6a9fe2788268f46891be8f152ed9b497c5286e2c6c5f07cc4d3758ec7` |
| `flight-samples.jsonl` | 14239180 | `5653d07ddc48837c9502de89a88d3d1433533566729807f3a52ddf7e72a30d6c` |
| `recovery-settings-used.cfg` | 827 | `f856baf249d9c12c1028bfd555de1a2f39fdb1c86f1bb257434d872977e0e6c6` |
| `native-summary.json` | 22370 | `fbf86b94eb17f108ff2b6c559683563a49f9ffe11826cd7b3c171feb4ef6b0a6` |
| `stack-parts.json` | 17863 | `43f9bc6d8d8eb10ae686f8f625bf61c04ca64b75e8ab7386b0cdefe52f4c4770` |
| `booster-parts.json` | 13245 | `c676cb54e890088a8e15154aab8d57a33e19dadd6b541da1fb10088ba64e7b7b` |
| `waypoint-source.json` | 244 | `23f3c9ee2683b6196195e30e7249b150a7a4773407d257510860da1752f5f068` |
| `deployment-receipt.json` | 500 | `ea0454d73b22dcad29fe7af634586e460c0b22e8ee141b7730c150448fe47b7e` |
| `ksp-flight-excerpt.log` | 4373 | `a8b4bc02394a71943489f7a1add0b8738b11c940500167849a6282ae5c9f508b` |
| `report.md` | 5009 | `59341a611696596e9269d9569bce9e5a7f0e906426ff146bd6a8aae3af9efc2d` |
