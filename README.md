# PDFReader X — OneNote 风格的 PDF 阅读与手写批注

基于 WPF (.NET 8) + PDFium 的 Windows PDF 阅读器，目标体验对齐 OneNote：在 PDF 页面上直接手写、荧光笔标注、插入图片与文本框，并以单文件 `.pdfrx` 包保存全部批注。

> 当前处于 **Phase 4（LLM 自动生成书签）** 完成阶段：画布批注、侧边栏导航与 AI 书签均已可用（见[路线图](#路线图)）。

## 功能

### 已实现

- 打开任意 PDF，无限画布垂直排列渲染：页面按视口按需渲染 + LRU 缓存（后台线程，UI 不卡）
- 手写墨迹：钢笔（压感）/ 荧光笔（半透明矩形笔尖）/ 橡皮擦（笔画级），颜色与粗细可调
- 缩放/平移：`Ctrl+滚轮` 缩放（以光标为中心）、滚轮滚动、`Shift+滚轮` 横向、中键拖拽平移
- 触摸/笔手势隔离：手写笔书写墨迹，单指平移、双指捏合缩放（带惯性）
- 每页墨迹独立存储为 StrokeCollection，为 `.pdfrx` 持久化铺路
- 支持命令行直接打开：`PDFReaderX.App.exe "文档.pdf"`（可配置为 PDF 默认打开方式）
- 打开/关闭文档、加载进度提示、错误弹窗提示
- 核心库提供页面文本提取 API（为 LLM 书签生成铺路）
- 应用图标（exe + 窗口标题栏）
- 侧边栏缩略图：后台线程批量生成 + 虚拟化列表，点击跳转、当前页高亮并自动跟随
- 书签目录树：读取 PDF Outline 分层展示，点击跳转（无目录时给出提示）
- 页面间距可调：工具栏「页距」滑块，实时生效
- LLM 自动生成书签：按批增量读页面识别目录，读完提示并自动核对章节偏移推算书签（不再渲染剩余页面）；无目录才回退全量分批读取 → 预览/编辑后应用到侧边栏
- 扫描版 PDF 支持：检测到无文字层时，渲染少量页面图片交给视觉模型找目录，读完目录后定位章节页码确认偏移；无法确认才回退全本看图识别（可勾选"不再提示"）
- 超长目录自动续传：AI 输出被截断时保留已解析条目，分批续传剩余部分再合并
- LLM 设置：API 地址 / Key / 文本模型 / 视觉模型可配置，保存在本机 `%AppData%\PDFReaderX\llm-settings.json`

### 规划中

- 书签持久化到 `.pdfrx` 包（Phase 6）
- 图片导入、自由文本框、撤销/重做（Phase 5）
- `.pdfrx` 包持久化与导出（Phase 6）

## 技术栈

| 组件 | 选型 |
| --- | --- |
| 框架 | WPF / .NET 8（net8.0-windows） |
| PDF 引擎 | PDFium（PdfiumViewer.Updated + bblanchon.PDFium.Win32） |
| MVVM | CommunityToolkit.Mvvm |
| LLM API | OpenAI 兼容格式（默认阿里云百炼 DashScope） |
| 文件存储 | `.pdfrx` ZIP 包（规划中） |

## 环境要求

- Windows 10/11 x64
- 构建：.NET 8 SDK 或更高（本机 9.0 SDK 可构建 net8.0 目标）
- 运行免安装包：无需额外运行时（自包含单文件）

## 构建与运行

```powershell
cd "D:\Code\PDFReader X"

# 构建
dotnet build PDFReaderX.sln

# 运行（开发模式）
dotnet run --project src/PDFReaderX.App

# 直接打开某个 PDF
dotnet run --project src/PDFReaderX.App -- "samples\sample.pdf"
```

生成样例 PDF（需要 Python 3）：

```powershell
python tools/make-sample-pdf.py
```

## 测试

```powershell
dotnet test
```

覆盖 PDF 加载、按 DPI 渲染、文本提取、书签目录、缩略图渲染、LLM 书签 JSON 解析（当前 15/15 通过）。

## 打包

参考打包产物对应两个命令（脚本在 `scripts\publish.ps1`）：

```powershell
# 免安装单文件包：dist/PDFReaderX-win-x64.zip（解压即用，含 .NET 运行时与 PDFium）
.scriptspublish.ps1 pack

# 安装包：dist/installer/PDFReaderX-Setup.exe（需要先安装 Inno Setup 6）
.scriptspublish.ps1 dist
```

发布配置：win-x64 自包含单文件（PublishSingleFile + 原生库自解压），已固化在 `src/PDFReaderX.App/PDFReaderX.App.csproj`。

> 国内网络拉取 NuGet 较慢时可配置镜像：`dotnet nuget add source https://nuget.cdn.azure.cn/v3/index.json -n nuget-cn`（可选）。

## 项目结构

```
PDFReaderX.sln
├── src/
│   ├── PDFReaderX.App/          # WPF 主程序（窗口、ViewModel、资源、图标）
│   ├── PDFReaderX.Core/         # 核心库（PDFium 封装、渲染、文本提取）
│   ├── PDFReaderX.LLM/          # LLM 集成（OpenAI 兼容，Phase 4）
│   └── ...
├── tests/PDFReaderX.Core.Tests/ # 单元测试
├── scripts/
│   ├── publish.ps1              # 打包脚本（免安装包 / 安装包）
│   └── installer.iss            # Inno Setup 安装包脚本
├── tools/
│   ├── make-sample-pdf.py       # 生成样例 PDF
│   └── make-app-icon.py         # 由图片生成多尺寸 ICO
└── samples/sample.pdf           # 样例 PDF（已 gitignore）
```

## 路线图

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| Phase 1 | 骨架 + PDF 加载 | ✅ 完成 |
| Phase 2 | 画布交互 + 手写墨迹（缩放/平移/压感/手势隔离） | ✅ 完成 |
| Phase 3 | 侧边栏：缩略图 + 书签跳转 | ✅ 完成 |
| Phase 4 | LLM 自动生成书签 | ✅ 完成 |
| Phase 5 | OneNote 元素：图片 / 文本框 / 撤销重做 | 待开始 |
| Phase 6 | `.pdfrx` 持久化 + 导出 | 待开始 |
