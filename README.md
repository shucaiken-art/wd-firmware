# Build190FromTracks

从 WD 新款 8TB 等服务区备份中，用 **区域 2 / 3** 磁道文件（`Tracks\XXXX_02.trk`、`XXXX_03.trk`）拼接生成 **模块 190**（`190.rpm`）。

## 背景

部分 WD 盘（如 WD80EAAZ）的模块 `0x0190` 不在 SA 区域 0/1，目录属性指向 **区域 2、3**。PC3000 等工具直接导出可能得到错误的 `190.ERR`；正确数据应对应 `_02` / `_03` 磁道链。

## 要求

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（Windows）
- 备份目录中 `Data\Tracks\` 下含 `0006_02.trk` … `0346_02.trk`（及可选 `_03` 副本）

## 构建

```powershell
cd d:\WD8TB\Build190FromTracks
dotnet build -c Release
```

运行：

```powershell
dotnet run --project Build190FromTracks -c Release
```

或执行 `Build190FromTracks\bin\Release\net8.0-windows\Build190FromTracks.exe`。

## 使用步骤

1. **Data 目录**：选择 HDD Profile 下的 `Data` 文件夹（自动填充 `Tracks`、`01.rpm`、默认输出路径）。
2. 勾选 **从 01.rpm 读取模块 190 分配长度**（解析 20 字节目录项、区域掩码 `0x0C`）。
3. 默认 **起始磁道 346**，**区域扇区偏移 0**（与 WD80EAAZ-03.22T0 样本一致）。
4. 选择主用磁道头 **2** 或 **3**；建议开启 **校验 02 与 03 一致**。
5. 点击 **预览 / 解析** 或 **生成 190.rpm**。

生成后会校验 ROYL 头、模块 ID `0x0190`，并显示 SHA-256。

## 磁道布局（本工具假设）

与已验证样本一致：

- 每个 `.trk` = **3010 × 512** = 1,541,120 字节  
- 区域内从 **0346** 递减至 **0006** 拼接；模块 190 长度由 `01.rpm` 决定，最后一道可能只取前若干字节  
- 若模块在区域内非零偏移，可设置 **区域扇区偏移**

其他家族盘若磁道编号不同，请调整起始/最小磁道后再预览。

## 参考

工作区分析报告：`Data\firmware_report\01_directory_190_analysis.md`、`WD80EAAZ_firmware_report.md`。
