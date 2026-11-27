# 2026-11-27 · CZ-10E 返航启动实机复测证据

[下载完整飞行遥测](CZ10E-telemetry.zip) · [实测报告](../../../docs/landing/2026-11-27-cz10e-a4b6664.md)

归档保留本轮的整箭 CSV、逐发动机 CSV、Final JSONL、飞行采样和事件 JSONL 原始字节，以及比较结果、配置与离线检查。KSP 日志保留与本次发射和触水有关的摘录。含环境和联系信息的游戏总日志保存在测试机本地。公开部署回执保留版本与哈希。

用 `ut` 对齐整箭和逐发动机。`planning-inputs` 存放真实求解输入和结果返回帧的发动机快照。`exact-replay` 文件记录三版求解器使用同一快照的离线结果。绝对游戏时间和首帧相对时间见测试报告。

ZIP SHA-256

```text
07c6fab4acc0ebc55c1595186e67b35db38ef54fa0e91a0dded83356a46099cc  CZ10E-telemetry.zip
```

| 文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| `native/flight-20261127-075504381.csv` | 1824713 | `a8a451bbd4956469a256ff417ef60347ef912ccbf0598c011942ec4ef02be394` |
| `native/flight-20261127-080721981.csv` | 2189564 | `f7a01938610da84f0ccd452c8e0ed3d00b92a85c93185ba28ad996038b7230b8` |
| `native/flight-engines-20261127-075504381.csv` | 3633355 | `4b37e7c5b19cc6f5638fb37716e471cfff41514c4d162042ccb4f7013350e186` |
| `native/flight-engines-20261127-080721981.csv` | 3378102 | `4d0eb5f4e1ed17b6ee52fa61307f30c6ef5c4636731bfed3d497d7bb46361ff0` |
| `native/flight-final-20261127-080532110-e54c944851454f2c8f90bdf7a0b19b60.jsonl` | 765779 | `8925d3270af87414805c6589c2f7f8486adf5dff6ea64b71c08d88d4fb4cb296` |
| `native/flight-final-20261127-081437614-fc5b221903b046c4b9750adb9d86ca59.jsonl` | 1174239 | `9c58dda27695c6c2166456d0c1c580824c1cf6caeadba43ea6e4ddbbdb22021a` |
| `planning-inputs/20261127-074836328-782607810.415.gate.bin` | 8305 | `6a6e5c0d918871b00a3f2b48ef4281df834b5c6adbf650707e8c8a78f8ad96f4` |
| `planning-inputs/20261127-074836328-782607810.415.gate.json` | 9063 | `bd09a3d3069039b08dbe7b74b6630dfab29e9dfad6e97bcbba11f82f61b61b99` |
| `planning-inputs/20261127-074836328-782607810.415.gate.txt` | 352 | `ef59811a63203b4272045df2a610ab8f90cca348b2049f36ee7872621c168e91` |
| `planning-inputs/20261127-074836328-782607810.415.input.bin` | 10670 | `fc87b26d9ccf8ed37cc1b1f482dccee3e9e1822b2e9fc5f197e8dd810954e626` |
| `planning-inputs/20261127-074836328-782607810.415.input.json` | 15206 | `e6691cd3d34ef448f2cc5d37b4f672ea2ee1d733e97ddb699f81880723b25b92` |
| `planning-inputs/20261127-074836328-782607811.735.gate.bin` | 8305 | `a91f4d9b05911448e0f5ba8f60bad5cd10a026856b05776868b262b15b63ab45` |
| `planning-inputs/20261127-074836328-782607811.735.gate.json` | 9174 | `ab0da384ace66ad38f745ab33f0edbf0d3fd6e00141549a45f4bd8eea2b82ff5` |
| `planning-inputs/20261127-074836328-782607811.735.gate.txt` | 348 | `1e27b22d22c8b405beb7a2234325feef9313dd4d1c8fda440dedb4a086759636` |
| `planning-inputs/20261127-074836328-782607811.735.input.bin` | 9155 | `766bc4bc1cbf3b5d8f2cdd739c7fbebd4ca4bae799b9f7c2dc290278e8f862c0` |
| `planning-inputs/20261127-074836328-782607811.735.input.json` | 12255 | `032607cc69ad440828facd631f3f8dfd73a96f97fc645165c3b9ad90d90e1c73` |
| `planning-inputs/20261127-074836328-782607813.055.gate.bin` | 8305 | `58b110a9cfea436a563bf38b7c67766def52d28a452d86d6a0554a859ed43a38` |
| `planning-inputs/20261127-074836328-782607813.055.gate.json` | 9094 | `6882b8a0f5f1b496d74ae96fb290f8be6227095469a39972df553d89fc6bf9bf` |
| `planning-inputs/20261127-074836328-782607813.055.gate.txt` | 374 | `dfee6ce8839a381293f4bf84d372e06aa0c0f8d2bdcacff2b0db1e78b5863697` |
| `planning-inputs/20261127-074836328-782607813.055.input.bin` | 9155 | `a3ad5fe38c38fab823792668c8553e141cc122aaf368d73594189d0b7ea31ac7` |
| `planning-inputs/20261127-074836328-782607813.055.input.json` | 12173 | `b36a83f11c0e88cb89ce41f815ea66e74d1d01e80b03b250a853e7c83face048` |
| `exact-replay-air-sensitivity.jsonl` | 8441 | `618f8e026eaf6539d115171f543d7a632665d7c3909f685653aea3ec659440da` |
| `exact-replay-latest.jsonl` | 1571 | `095c763d8c3275ac20ef8ca397b127f73463b508fd83be9fbcb60320e9ffb846` |
| `exact-replay-old.jsonl` | 1401 | `ba8e69db5e140642255b5c7a3a179ee8101b93c3dd013682842f1ad00b428e9e` |
| `exact-replay-previous.jsonl` | 1401 | `ba8e69db5e140642255b5c7a3a179ee8101b93c3dd013682842f1ad00b428e9e` |
| `exact-replay-terminal-details.jsonl` | 8440 | `0dcd629b016734fabbee125abc390f6447dc926d773ae6da393a5d233acb9372` |
| `exact-replay-traced.jsonl` | 6504 | `e2e85f36361bdacfdd07b80011a17b07ddf3b6931f6101d95574aa3e71e12277` |
| `flight-events.jsonl` | 178137 | `a6b720a547a0729d7136bb4fe3974ce510f715f16f2c29c7229a87e9356036a4` |
| `flight-samples.jsonl` | 32842738 | `eaeed4eea3ae7bf7352d700e8abee00b72bb54e36b2260491e790869f0c7b446` |
| `recovery-settings-used.cfg` | 827 | `f856baf249d9c12c1028bfd555de1a2f39fdb1c86f1bb257434d872977e0e6c6` |
| `native-summary.json` | 34832 | `9cbac2e2a7222ca96338c3d00761b8b0669ffc25264570cc5b9943eff7426921` |
| `stack-parts.json` | 17860 | `1742e87d260e3102d9dc08b9c86550d8a4b908711fd28176045f232e25bf2a32` |
| `booster-parts.json` | 13243 | `97fb08f29f20b24d1b4d31f121ace6ac486c286fc5bfae6629bc497f31899500` |
| `waypoint-source.json` | 244 | `23f3c9ee2683b6196195e30e7249b150a7a4773407d257510860da1752f5f068` |
| `deployment-receipt.json` | 258 | `c8a5f279105d42a42a2786d18fdb098fea53ae01aa4b533c912f971dd19fafc5` |
| `repeat-flight-events.jsonl` | 108730 | `443537d7d16db30f023da08bfd620ff17edb141b74dace21d020cc30f03fac45` |
| `repeat-flight-samples.jsonl` | 22936472 | `dcb2bd90574054c9f93ac00a31e84ee78d526627e3df4f7aee83869b6c184041` |
| `comparison.json` | 49488 | `aa64ffd4bf48679cb075448e1eb21dfc96fd9163bad87c498f34b45e1a3ba118` |
| `repeat-comparison.json` | 55284 | `cb6c6741dc65e014d49f7db7cb6956dbe3def31022bab78b7c30cd1a0743e9a4` |
| `ksp-flight-excerpt.log` | 5500 | `70fcccb0c78022d9ccff2412d7c59a69b16fbd0d5fabf219e8637d95eb598311` |
| `far-ready-probes.jsonl` | 10688 | `1542128b5cae9ff42f3d992d58edd07958101cea6de3f969ee1735a15adc8c95` |
| `test-dll-sha256.txt` | 66 | `ce9a82eeb15f15349873bde7bef5330dd4cfeab1c40309d96e73f0910ad467ab` |
| `report.md` | 7599 | `88590bf2d0c5fa37bf6b96e4db82f1ff10fb768b0bebaaedc9a1b066cb04b427` |
