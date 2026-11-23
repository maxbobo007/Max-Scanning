<div align="center">

# Max Scanning

**极速、准确的 Windows 磁盘空间可视化工具**
*A blazing-fast, accurate disk space treemap for Windows*

[![Release](https://img.shields.io/github/v/release/maxbobo007/max-scanning?label=%E4%B8%8B%E8%BD%BD%20Download)](https://github.com/maxbobo007/max-scanning/releases/latest)
![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)

![Max Scanning](docs/screenshot.png)

</div>

Max Scanning 用方块图 (treemap) 告诉你磁盘空间都被什么占了。它的功能与经典工具 SpaceSniffer 1.1.2 一一对齐，扫描引擎、数据结构和渲染全部用现代技术重写：

- **以管理员身份运行时直接读 NTFS 的 MFT**：158 万个文件约 2 秒扫完，不逐个打开目录。
- **GPU 渲染**（Direct2D），每帧约 1 毫秒，高分屏下清晰锐利。
- **64 位、列式数据结构**：几百万个文件也不会卡，没有 2 GB 内存上限。

## 下载 / Download

到 [**Releases**](https://github.com/maxbobo007/max-scanning/releases/latest) 下载：

| 文件 | 说明 |
|---|---|
| `MaxScanning-1.0.0-win-x64-portable.exe` | **推荐**。单文件，自带运行时，下载就能用，无需安装（约 52 MB） |
| `MaxScanning-1.0.0-win-x64.exe` | 体积小（约 3 MB），需要先安装 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |

> 💡 右键选"以管理员身份运行"会启用 **MFT 极速扫描**。普通权限下自动改用多线程扫描，界面"选项"菜单里也可以一键以管理员身份重启。

## 性能 / Performance

测试机器：Ryzen 7 5800H，NVMe SSD，管理员权限。

| 卷 | 条目数 | 扫描方式 | 耗时 |
|---|---:|---|---:|
| C:（400 GB 系统盘） | 1,581,220 | NTFS MFT | **1.9 s** |
| D:（349 GB） | 252,849 | NTFS MFT | **0.58 s** |
| E:（1.09 TB） | 276,542 | NTFS MFT | **0.73 s** |
| F:（1.82 TB） | 98,460 | 多线程枚举 | 0.9 s |

- **准确**：扫描得到的"占用空间"与 Windows 报告的已用空间相差不到 0.1%。
- **轻量**：150 万个条目约占 330 MB 内存；快照保存和加载在 0.1 秒以内。

## 功能 / Features

**扫描**
- 支持驱动器、任意文件夹和网络路径（`\\server\share`）
- 可暂停、继续、停止、重新扫描；可选扫描交替数据流（ADS）
- **实时监听**：文件变化时只增量重扫发生变化的文件夹，视图自动更新

**方块图**
- 嵌套盒子带标题栏，详细级别可调（`+` / `-` / Ctrl+滚轮）
- 可调最小单元尺寸、横纵比例、按大小或名称排序、立体阴影
- 悬停高亮（高亮层数、离开淡出效果都可调），缩放带动画
- 根目录下可显示**空闲空间**和**未知空间**

**导航**
- 双击进入文件夹；Backspace 或鼠标中键返回上层；鼠标后退键后退；Home 回到根目录
- 顶部路径栏可点击跳转；可以同时开多个标签页窗口（Ctrl+T）

**过滤器**（兼容 SpaceSniffer 语法并做了扩展）
```
*.mp4;*.mkv;>500mb        大于 500 MB 的视频
>1y;>100mb                一年没改过、且大于 100 MB 的文件
|*.dll;|*.exe             排除 dll 和 exe
:red                      红色标签（文件夹的标签会被其中内容继承）
@h  @s  @c                隐藏 / 系统 / 压缩文件
modified>2024-01-01       按日期
folder:node_modules       位于某类文件夹中的文件
regex:^IMG_\d+\.jpe?g$    正则表达式
```
同类条件之间为"或"，不同类条件之间为"且"，`|` 表示排除。完整说明见程序内的"帮助 → 过滤器帮助"。

**其他**
- **标签**：红、绿、蓝、黄四色（Ctrl+1~4），可按标签过滤
- **导出**：模板式导出（头部、详细、尾部三段 + 占位符），带实时预览；内置 CSV、TXT、树状、HTML、JSON，可保存自己的预设
- **快照**：把整次扫描结果（含标签）存成 `.mscan`，之后随时打开
- **右键菜单**：打开、在资源管理器中显示、在此处打开命令提示符、复制路径、属性、删除到回收站或永久删除、重新扫描此文件夹
- **配置**：图形、效果、动作、颜色全部可调，配置可导入导出
- **日志控制台**、扫描完成后闪动窗口、拖放文件夹或快照即可打开
- **中文 / English** 界面可切换

## 与 SpaceSniffer 1.1.2 的对比

| | SpaceSniffer 1.1.2 | Max Scanning |
|---|---|---|
| 扫描方式 | 单线程递归 `FindFirstFileW` | MFT 直读，或 48 线程批量枚举 |
| 程序位数与内存 | 32 位，最多 2 GB | 64 位，列式存储 |
| 渲染 | GDI（CPU） | Direct2D（GPU）+ DirectWrite |
| 高 DPI | 不支持，会发糊 | Per-Monitor V2 |
| 硬链接 | 每个路径都计一次，总量可能超过磁盘容量 | 只计一次，总量准确 |
| 云文件（OneDrive / Dropbox） | 按文件大小计算 | 按实际占用计算，未下载的文件为 0 |

## 命令行 / Command line

```
MaxScanning.exe D:\                          打开并扫描
MaxScanning.exe --load scan.mscan            打开快照
MaxScanning.exe D:\ --bench --out r.txt      无界面扫描并输出统计
MaxScanning.exe D:\ --verify                 对比 MFT 扫描和目录枚举的结果（准确性检查）
MaxScanning.exe D:\ --export a.csv --preset CSV --filter ">100mb"
MaxScanning.exe D:\ --save-snapshot d.mscan
MaxScanning.exe --selftest D:\Some\Folder --out report.txt   自动化自检（41 项）
```

## 从源码构建 / Build

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```bash
dotnet build -c Release
# 小体积版（需 .NET 运行时）
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=true -o publish/MaxScanning
# 便携版（自带运行时）
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish/MaxScanning-portable
```

### 代码结构
```
src/Core      FileTree：列式存储 + CSR 子节点表；Util
src/Scan      MftScanner：MFT 直读，并行解析
              DirScanner：并行目录枚举
              ScanEngine：自动选择扫描方式
src/Filter    FilterSet：过滤语言
src/View      TreemapLayout：squarified 布局
              D2DRenderer：GPU 渲染
              ScanView：单个视图（导航 / 过滤 / 标签 / 监听）
src/UI        MainForm、StartDialog、ConfigForm、ExportForm、ConsoleForm、HelpForm
src/IO        Exporter：模板导出；Snapshot：快照
src/Settings  Config：JSON 配置；L：中英双语
```

## 说明

本项目与 SpaceSniffer 及其作者没有任何关联。代码全部是独立编写的，"SpaceSniffer"一名仅用于说明功能对标。
