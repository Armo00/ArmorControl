# 2026-11-27 · CZ-10E 返航与着陆交接实测证据

[下载遥测 ZIP](CZ10E-telemetry.zip) · [实测报告](../../../docs/landing/2026-11-27-cz10e-d8bb6ec.md)

两趟从同一一级分离快照开始，`flight` 对应 6 km／8 s，`repeat` 对应 6 km／4 s。整箭、逐发动机和 Final 日志保留原始字节。以 `ut` 对齐。两趟求解提交 UT 一致，`planning-inputs` 的同名快照保留了补跑输入与返回结果。KSP 飞行事件摘录保留两次求解的返回。带 `old-inputs` 的文件是上轮输入的复算对照。

ZIP SHA-256 `f0fe912992d9392d7cc944985506abe2a329ac14ed6106b97010935689a6ed73`

| 文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| `native/flight-20261127-101414560.csv` | 2242269 | `37da035baf998fbeac9a9ff587ff8aa4a82ffeef9f74a7107c72922c3d2fc4fb` |
| `native/flight-20261127-102024013.csv` | 2211413 | `3dd7cf10f406234bb513dfdc78381cb04b56cad6d67d8a990a26b7726d138bf9` |
| `native/flight-engines-20261127-101414560.csv` | 3304947 | `ca55705c333233c5fb16e7f0e8071739656089d6ef5032217bc8420d6eb185d3` |
| `native/flight-engines-20261127-102024013.csv` | 3230271 | `482e3d897a063964151f4bf679aca29b229537621dc057a604ade1aeddbd6079` |
| `native/flight-final-20261127-101816513-1eeb4de495034da098e10c525dfd621d.jsonl` | 1122628 | `6e4bf89d99ec378b1ad48d6e0388660aa63a1a92067f4680bb283eac9b1b1564` |
| `native/flight-final-20261127-102415603-bc5d3cba088e4bac99a2f4bbc7e8f2e5.jsonl` | 2241041 | `d1d0938ffd150970d4f1756ac4f31a6f5f76763fbb276f07ad3aa447c214989d` |
| `planning-inputs/20261127-101111276-782607808.235.gate.txt` | 255 | `70deeeab8a0343551a786fef337c5f8ca13d5e62b89fa09a7039a05c80ff27c5` |
| `planning-inputs/20261127-101111276-782607808.235.input.bin` | 10756 | `a11f02dd724ae4e34fe6f46e1709ac2358064902ad0a010da9be6ceedef62d60` |
| `planning-inputs/20261127-101111276-782607808.235.input.json` | 15201 | `6a2b16c267039d28b18efcad2f3982ebdd9c9f683c8ad5677ad32482c4f88de8` |
| `exact-replay-live-return.jsonl` | 903 | `089e50e95a355b6aa4d38c122134f0edd6ffb0ed0684adcc57761c918d9ef6ea` |
| `flight-events.jsonl` | 133174 | `e9c3978aa043a59325f5dc74e29b87c30d2127796845007a5a186f7cf7d3b044` |
| `flight-samples.jsonl` | 16221810 | `22b9849883a55d531b222a209577e58b030e1b4664c82a68f41894897079bf63` |
| `recovery-settings-used.cfg` | 827 | `b591515f59a4812be2e826d289501f72f4c2b864da4cd6ff273748b4ee669bfc` |
| `native-summary.json` | 44558 | `8b4174b1fd5690bcace63d6a7c5b29b97d9a56b15e1169b3ca4e73d625f2eefe` |
| `stack-parts.json` | 17867 | `43cd53471a70dc92996c543a3e4b710d9527068477844647c86abd498a13d754` |
| `booster-parts.json` | 13247 | `ace7cf4e04383c4efbf4a5c64cc9dcdfac605f3842881c7a24574b2a9457be4e` |
| `waypoint-source.json` | 244 | `23f3c9ee2683b6196195e30e7249b150a7a4773407d257510860da1752f5f068` |
| `deployment-receipt.json` | 264 | `6edd67bf54eb3b44432d50314eaf14d3af9d186628e4879c4bcd637f4452d56e` |
| `repeat-flight-events.jsonl` | 88444 | `d602c801f57a8f79e828428678821060fc234b4f89b57f16e57c5f2fffb740bc` |
| `repeat-flight-samples.jsonl` | 15052521 | `78cb06f5ce7985dded62910c977edec54b087a94dc757688ade4ca93e3260dc5` |
| `comparison.json` | 66918 | `b4d43c31968a132a2d0ba67827c797fbdd9484df943120e8383c3cb38ce6344d` |
| `repeat-comparison.json` | 66125 | `e234fc8c7ae1059340a64c852d74ac64bfc0ef071f1e8a29d75a8fe3f0f93003` |
| `ksp-flight-excerpt.log` | 1485 | `c04714970ca787386857ef0e49f8d74406420ef149ccd0836ce6edc2a2a90679` |
| `test-dll-sha256.txt` | 66 | `bd9de5a94a4417d90868669c3ebf6fdfd88dddf8b2a365457d80b232d7cadf67` |
| `report.md` | 6339 | `782e830987d4a19038fdbff725d7d3716795b9f3294c5f8a27ed961961d1695f` |
| `old-inputs-new-plan-4000.jsonl` | 2710 | `992f64816f0e56c29e22d95f9213bf78b6314a8a5cff7441eec296388dad68fe` |
| `old-inputs-new-plan-6000.jsonl` | 2710 | `992f64816f0e56c29e22d95f9213bf78b6314a8a5cff7441eec296388dad68fe` |
| `review-summary.json` | 33178 | `7b3a2f4b5872aa2e2e0cab42d5b7875bb3748cffdc2e5b1de2b4620d51b87abb` |
| `settle-readback.json` | 2235 | `40fabbd438f1d0586d3d96bb2d271e94383fcec3eb490e0108a73fe87e053a50` |
| `recovery-settings-4s.cfg` | 827 | `84f50eee630e96e4681957288ccad5806b1c9558b72e45baeb1d3b9c9efa250e` |
