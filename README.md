# 📖 PDFReader X

> OneNote 风格的 PDF 阅读与手写批注工具 · WPF / .NET 8

![版本](https://img.shields.io/badge/版本-v1.0.2-2D6CDF?style=flat-square)
![框架](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square)
![平台](https://img.shields.io/badge/平台-Windows%2010%2F11%20x64-0078D6?style=flat-square)

在 PDF 页面上像 OneNote 一样书写：钢笔压感、荧光笔标注、橡皮擦、图片、富文本框，全部批注可保存为单文件 `.pdfrx` 包，随时重新打开继续编辑。

---

## ✨ 特性

**书写与标注**

- ✍️ **钢笔手写**：压感笔迹，跨页面边界连续书写不断裂
- 🖍️ **荧光笔**：半透明矩形笔尖，重点内容一目了然
- 🧽 **橡皮擦**：跨画布与页面连续擦除，逐笔可撤销
- 🎨 **独立设置**：钢笔 / 荧光笔 / 橡皮擦各自可调颜色与粗细

**OneNote 式元素**

- 📝 **富文本批注**：局部字符可加粗、斜体、下划线、改字号、改颜色（选中即改，OneNote 同款交互）
- 🖼️ **图片批注**：工具栏插入 / `Ctrl+V` 粘贴 / 直接拖入，可拖动与缩放
- 🔲 **边框样式**：文本选区与编辑框的边框样式（虚线 / 实线 / 颜色）可自定义

**阅读体验**

- 📄 **无限画布**：按需渲染 + LRU 缓存，大 PDF 滚动流畅不卡 UI
- 🔍 **缩放平移**：`Ctrl+滚轮` 以光标为中心缩放，中键拖拽平移，触摸双指捏合
- 🗂️ **缩略图导航**：侧边栏缩略图快速定位，当前页高亮并自动跟随
- 🔖 **书签目录**：读取 PDF 自带目录，分层树状展示，点击跳转

**AI 书签（可选 LLM 配置）**

- 🧠 **自动识别目录**：逐批轻量探测，找到目录后读取完整目录，核对章节偏移推算书签
- 📖 **扫描版支持**：无文字层时用视觉模型识别目录与页码
- ✂️ **超长目录续传**：输出截断自动续传，JSON 容错解析

**数据与导出**

- 💾 **`.pdfrx` 批注包**：墨迹、富文本、图片、书签一键保存 / 完整恢复
  - 「保存」与退出自动保存采用**引用式**：不内嵌 PDF，体积小、保存快——请**保留原 PDF 文件在原位置**，打开批注时需要它
  - 「另存为」采用**内嵌式**：PDF 一并打包进 `.pdfrx`，可单独分发，无需原 PDF
- 📤 **导出 PDF**：全部批注合并渲染导出为新 PDF
- 🔄 **退出自动保存**：带进度提示，另存为可内嵌原始 PDF

---

## 🚀 快速开始

### 下载

从 [GitHub Releases](https://github.com/yeguoyy/PDFReader-X/releases) 获取：

- `PDFReaderX-win-x64.zip` —— 免安装，解压即用
- `PDFReaderX-Setup-1.0.2.exe` —— 安装包

> 自包含单文件，无需额外安装 .NET 运行时。

### 从源码构建

```powershell
git clone https://github.com/yeguoyy/PDFReader-X.git
cd PDFReader-X
dotnet build PDFReaderX.sln
dotnet run --project src/PDFReaderX.App
```

### 命令行打开

```powershell
PDFReaderX.App.exe "文档.pdf"
```

也可在系统设置中将其设为 PDF 默认打开方式。

---

## 🖊️ 操作速查

| 操作 | 方式 |
| --- | --- |
| 缩放 | `Ctrl + 滚轮`（以光标为中心） |
| 平移 | 中键拖拽 / 选择工具左键拖拽 |
| 滚动 | 滚轮 / 右侧滚动条 |
| 横向滚动 | `Shift + 滚轮` |
| 撤销 / 重做 | `Ctrl+Z` / `Ctrl+Y` |
| 文本提交 | `Enter`；`Shift+Enter` 换行；`Esc` 取消 |
| 局部加粗 / 斜体 / 下划线 | 选中字符后点工具栏，或 `Ctrl+B` / `Ctrl+I` / `Ctrl+U` |
| 自定义字号 | 字号框直接输入 6~144 |
| 删除元素 / 笔划 | 选中后 `Delete` |
| 插入图片 | 工具栏按钮 / `Ctrl+V` / 拖入画布 |

---

## 🧠 AI 书签

在「设置 → LLM 设置」中配置（OpenAI 兼容格式，默认阿里云百炼 DashScope）：

- API 地址 / Key / 文本模型 / 视觉模型，保存在本机 `%AppData%\PDFReaderX\llm-settings.json`

**工作策略**：10 页一批轻量探测目录 → 找到目录后读取完整目录 → 再读章节起始页确认「PDF 页数与书本页数」的偏移 → 推算全部书签；全书无目录则停止并提示，不浪费 token。

---

## 🛠️ 技术栈

| 组件 | 选型 |
| --- | --- |
| 框架 | WPF / .NET 8（net8.0-windows） |
| PDF 引擎 | PDFium（PdfiumViewer.Updated + bblanchon.PDFium.Win32） |
| MVVM | CommunityToolkit.Mvvm |
| LLM API | OpenAI 兼容格式（默认阿里云百炼 DashScope） |
| 批注存储 | `.pdfrx` ZIP 包 |

## 📦 打包

```powershell
# 免安装单文件包：dist/PDFReaderX-win-x64.zip
.\scripts\publish.ps1 pack

# 安装包：dist/installer/PDFReaderX-Setup-1.0.2.exe（需先安装 Inno Setup 6）
.\scripts\publish.ps1 dist
```

## 📁 项目结构

```text
PDFReaderX.sln
├── src/
│   ├── PDFReaderX.App/          # WPF 主程序（窗口、画布、工具栏、资源）
│   ├── PDFReaderX.Core/         # 核心库（PDFium 封装、渲染、文本提取）
│   └── PDFReaderX.LLM/          # LLM 集成（OpenAI 兼容）
├── tests/PDFReaderX.Core.Tests/ # 单元测试
├── scripts/                     # publish.ps1 打包、installer.iss 安装包脚本
├── tools/                       # 样例 PDF、图标生成脚本
└── samples/                     # 样例 PDF（gitignore）
```

## 📝 更新日志

### v1.0.2

- 新增 Paint 风格颜色面板：笔触预览、粗细调节、最近颜色、屏幕取色器
- 新增快捷笔列表：新建 / 删除 / 长按拖动排序，配置自动保存
- 工具栏图标化：撤销 / 重做置左，选择、文本、套索图标，钢笔与荧光笔视觉区分
- 套索图标更新为虚线圆环样式，按钮顺序调整为 撤销 → 重做 → 选择 → 套索 → 快捷笔
- 修复跨图层墨迹连接：PDF 页面边框移至墨迹之下，跨边界自动求交无缝衔接
- 修复非 100% 缩放下第二页跨图层笔迹坐标偏移
- 修复新建快捷笔对话框崩溃

### v1.0.1

- 新增 OneNote 风格工具栏，钢笔 / 荧光笔 / 橡皮设置分组收纳
- 文本批注升级为富文本：选中字符可加粗 / 斜体 / 下划线 / 改字号 / 改颜色，字号支持自定义输入
- 修复跨页面边界书写的 1px 墨迹间隙（页面边框坐标补偿）
- 橡皮擦支持跨画布与页面连续擦除
- 修复 exe 应用图标未生效的问题
- 富文本分段格式随 `.pdfrx` 包持久化保存与恢复

### v1.0.0

- 画布批注、侧边栏导航、AI 书签、自由元素、`.pdfrx` 批注包、套索选择与 PDF 导出

---

## 🗺️ 路线图

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| Phase 1 | 骨架 + PDF 加载 | ✅ 完成 |
| Phase 2 | 画布交互 + 手写墨迹（缩放 / 平移 / 压感 / 手势隔离） | ✅ 完成 |
| Phase 3 | 侧边栏：缩略图 + 书签跳转 | ✅ 完成 |
| Phase 4 | LLM 自动生成书签 | ✅ 完成 |
| Phase 5 | OneNote 元素：图片 / 文本框 / 撤销重做 | ✅ 完成 |
| Phase 6 | `.pdfrx` 持久化 + 导出 | ✅ 完成 |
| 下一步 | 墨迹与元素导出为 PDF 原生注释 | ⏳ 规划中 |

## 环境要求

- Windows 10/11 x64
- 构建：.NET 8 SDK 或更高（本机 9.0 SDK 可构建 net8.0 目标）
- 运行免安装包：无需额外运行时

## 测试

```powershell
dotnet test
```

覆盖 PDF 加载、按 DPI 渲染、文本提取、书签目录、缩略图渲染、LLM 书签 JSON 解析、坐标变换等。