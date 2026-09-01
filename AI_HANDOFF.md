# PDFReader-X 交接说明

> 更新时间：2026-09-01  
> 当前分支：`main`  
> 当前状态：3 个文件已修改、未提交。构建已通过；测试存在两处与本次修改无关的既有失败。

## 1. 本次任务

用户反馈了 5 个问题，当前目标是改善 PDF 阅读器的侧栏与墨迹体验：

1. 左侧栏要可以折叠。
2. 橡皮擦有时失效，尤其是擦荧光笔时。
3. 荧光笔撤销不了。
4. 荧光笔效果不佳，色块太浓、文字不清晰。
5. 放大 PDF 后文字模糊，希望放大到一定倍数后提高清晰度。
6. 左侧书签和预览要跟随右侧滚动的页面联动（例如滚到 2.1.1 时，预览定位到对应页，书签列表滚动并高亮 2.1.1）。
7. 左侧栏要可以拖拽调节大小。

## 2. 已完成的修改

### 2.1 左侧栏折叠——已初版完成

文件：

- `src/PDFReaderX.App/MainWindow.xaml`
- `src/PDFReaderX.App/MainWindow.xaml.cs`

改动：

- 把主内容区第一列的侧栏列命名为 `SidebarColumn`。
- 新增一个窄条形折叠按钮 `SidebarToggle`。
- 新增 `OnSidebarToggleClick`：
  - 展开时宽度为 `220`，最小宽度 `160`。
  - 折叠时宽度为 `0`，最小宽度 `0`。
  - 同时切换图标与 ToolTip。
- 主内容区由 `Grid.Column="1"` 移到 `Grid.Column="2"`。

### 2.2 橡皮擦失效——已做初版修复

文件：

- `src/PDFReaderX.App/Controls/InfiniteCanvas.xaml.cs`

改动：

- `EraseStrokesAt` 不再只依赖 `InkCanvas.Strokes.HitTest()`。
- 新增基于 `EllipseGeometry` 的圆盘命中判断：
  - 用 `Geometry.FillContainsWithDetail(circle)` 检查荧光笔这类填充型笔画。
  - 用 `Geometry.StrokeContains(Pen, Point)` 补充检查线框边缘。
- 目的：避免荧光笔这类大面积、低透明度笔迹被默认命中测试漏判。

### 2.3 荧光笔透明度——已调低

文件：

- `src/PDFReaderX.App/Controls/InfiniteCanvas.xaml.cs`

改动：

- `CreateDrawingAttributes()` 中荧光笔透明度由 `0x55` 改为 `0x2E`。
- 目的是让荧光笔更轻、更通透，减少“糊在字上”的感觉。

### 2.4 放大清晰度——已做缓存重渲染初版

文件：

- `src/PDFReaderX.App/Controls/InfiniteCanvas.xaml.cs`

改动：

- `MaxRenderDpi` 从 `240` 提升到 `384`。
- 原来的 `_bitmapCache` 只保存 `BitmapSource`。
  - 现在新增 `RenderedPage(BitmapSource Bitmap, int Dpi)`。
  - `_bitmapCache` 保存位图和渲染 DPI。
- `UpdateVisiblePages()` 中：
  - 如果当前缓存页的 DPI 与按当前缩放计算出的目标 DPI 差距大于 `12`，就触发异步重渲染。
- 目的：放大后不再长期停留在低分辨率缓存页，达到一定倍数后自动重渲染更清晰页面。

### 2.5 顺手清理

文件：

- `src/PDFReaderX.App/Controls/InfiniteCanvas.xaml.cs`

改动：

- 移除了 `RenderPageAsync` 里无实际作用的调试性坐标计算和延迟 Dispatcher 调用。

## 3. 当前验证状态

### 构建

已执行：

```powershell
dotnet build PDFReaderX.sln -c Debug --nologo
```

结果：成功，0 警告，0 错误。

### 测试

已执行：

```powershell
dotnet test tests\PDFReaderX.Core.Tests\PDFReaderX.Core.Tests.csproj --nologo
```

结果：25 通过，2 失败。

失败项：

- `LlmServiceTests.ParseBookmarks_SkipsInvalidPageAndEmptyTitle`
- `LlmServiceTests.ParseBookmarks_DetectsZeroBasedPages`

### 与本次修改的关系

我用 `git stash push/pop` 验证过：把本次 UI/画布修改暂存后，这 2 个测试仍然失败。  
结论：这些失败是既有问题，不是本次修改造成。

## 4. 尚未完成 / 下一步计划

### 4.1 荧光笔撤销——未完成，需要继续排查

现状：

- 当前已有的统一撤销逻辑在 `InfiniteCanvas.Undo.cs`。
- 普通笔画应该经由 `OnStrokeCollected` 调用 `SplitPageStroke` 或 `SplitFreeStroke` 后记录 undo。
- 目测理论上荧光笔也应记录 undo，但用户实际反馈不能撤销。

建议下一步：

1. 先复现问题，确认：
   - 是“荧光笔画上去后立即 Ctrl+Z 无反应”，还是“画了很久以后无法撤销到荧光笔”。
   - 是在页面内书写，还是跨页 / 跨边界书写。
   - 是新建荧光笔，还是从 `.pdfrx` 恢复的旧笔迹。
2. 在 `OnStrokeCollected` 加日志或断点，确认：
   - sender 是 `_liveInk`、`_freeInk` 还是页面 `InkCanvas`。
   - 是否进入 `SplitFreeStroke` / `SplitPageStroke`。
   - `RecordUndo` 是否真的被调用。
3. 重复绘制、撤销、重做的组合操作，确认是否是 `_liveInk` 与最终拆分后的双重 undo 顺序问题。
4. 如果确认是荧光笔 `IsHighlighter = true` 导致事件行为不同，考虑不依赖 `StrokeCollected`，而是在鼠标抬起、实时层拆分后统一记录 undo。

### 4.2 放大清晰度——初版已做，仍建议实测调优

当前策略：

- 页面按当前 `Zoom * 96` 计算 DPI。
- 缓存页 DPI 与目标 DPI 差超过 12 就重渲染。
- 非 Performance 模式上限 `384 DPI`，Performance 模式上限 `144 DPI`。

建议实测：

1. 放大到 150%、200%、300%、400%，观察文字是否清晰。
2. 观察渲染延迟、内存占用和 LRU 缓存行为。
3. 如果低倍率下频繁重渲染导致闪屏，可以把重渲染阈值从 `12` 提高到 `24` 或 `32`。
4. 如果文字仍然模糊，可以再提高 `MaxRenderDpi`，但要评估大页面的内存和性能。
5. `PerformanceMode = true` 时是否保留当前 `144 DPI`，需要用户确认或做设置项。

### 4.3 橡皮擦——初版已做，需要交互测试

建议测试点：

1. 画荧光笔，用不同尺寸橡皮擦除。
2. 橡皮在荧光笔边缘、文字附近、页面边界、跨页边界滑动。
3. 检查是否出现误擦、漏擦、撤销栈不正常、每次移动都产生大量 undo 步骤。
4. 如果一次拖动产生很多 undo 记录，后续可以改成“一次拖拽合并为一个 undo 组”。

### 4.4 荧光笔视觉——当前透明度可能仍需调参

当前改动：

- `0x55` → `0x2E`

建议准备一个视觉对比：

- 常用黄色荧光笔在不同页面底色、不同字号下测试。
- 如果仍偏浓，可以继续尝试 `0x26`、`0x22`、`0x1E`。
- 如果太淡，可回退到 `0x33` 或 `0x3C`。

### 4.5 书签 / 预览与页面滚动联动——未开始，待实现

需求：

- 右侧阅读区滚动时，左侧缩略图 / 预览面板自动定位到当前可见页。
- 左侧书签列表同步跟随：滚动到对应书签页时，书签列表滚动到位并高亮当前书签。
- 用户举例：滚到 2.1.1 时，预览指向对应页，书签跟随显示并定位到 2.1.1。

建议实现方向：

1. 先在 `InfiniteCanvas` / `MainWindow` 中找到当前"当前页"的计算来源（可能是 `UpdateVisiblePages` 或 `CurrentPage` 属性）。
2. 引入一个"当前页变化"事件（或使用已有的 `PropertyChanged`）通知主窗口。
3. 书签列表收到事件后：
   - 根据书签页码找到对应 `ListBox` / `TreeView` 项。
   - 调用 `BringIntoView()` 滚动到该项。
   - 设置选中 / 高亮状态（注意区分"用户点击书签导致的滚动"和"阅读滚动导致的书签联动"，避免回环触发）。
4. 预览面板同理，对缩略图列表调用 `ScrollIntoView` / `BringIntoView`。

注意事项：

- 需要防抖，避免快速滚动时频繁刷新选中项。
- 书签页码可能是 0-based 或 1-based，注意与 `CurrentPage` 换算。

### 4.6 侧栏可调大小——未开始，待实现

需求：用户希望能够拖拽调节左侧栏宽度，而不仅是折叠 / 展开。

建议实现方向：

1. 在 `MainWindow.xaml` 中，把侧栏列（`Grid.Column=0`）和折叠按钮列（`Grid.Column=1`）之间加一个 `GridSplitter`。
   - 或者把 `GridSplitter` 放在 `Column=1`，把折叠按钮移到侧栏内部 / 标题栏。
2. 需要定义合理的宽度范围：`MinWidth` 建议 `160`，`MaxWidth` 可以先设为 `450` 或窗口宽度的 40%。
3. 折叠逻辑需要与 `GridSplitter` 兼容：
   - 折叠时仍将 `SidebarColumn.Width` 设为 0，`MinWidth` 设为 0。
   - 展开时恢复到上次拖拽后的宽度（可存一个 `_sidebarWidth` 变量）。
4. 可选：把最终宽度保存到 `AppSettings`，重启后恢复。

### 4.7 侧栏折叠——已做基础版，可继续增强

可选增强：

1. 折叠状态保存到 `AppSettings`，下次启动保持一致。
2. 按钮图标与状态刷新逻辑抽成单独方法。
3. 若以后要支持拖拽调宽，可把 `SidebarColumn` 改成 `GridSplitter`。

## 5. 关键文件位置

```text
C:\Code\PDFReader-X\src\PDFReaderX.App\MainWindow.xaml
C:\Code\PDFReader-X\src\PDFReaderX.App\MainWindow.xaml.cs
C:\Code\PDFReader-X\src\PDFReaderX.App\Controls\InfiniteCanvas.xaml.cs
C:\Code\PDFReader-X\src\PDFReaderX.App\Controls\InfiniteCanvas.Undo.cs
C:\Code\PDFReader-X\src\PDFReaderX.App\Controls\InfiniteCanvas.Elements.cs
```

主界面折叠相关：

```text
MainWindow.xaml        ：SidebarColumn / SidebarToggle / SidebarToggleIcon
MainWindow.xaml.cs     ：OnSidebarToggleClick

联动 / 拆分器相关（待实现后补充）：

MainWindow.xaml        ：书签列表、预览列表、（新增）GridSplitter
MainWindow.xaml.cs     ：（新增）当前页 -> 书签/预览联动
```

橡皮擦、荧光笔、渲染清晰度：

```text
InfiniteCanvas.xaml.cs ：EraseStrokesAt / CreateDrawingAttributes / UpdateVisiblePages / RenderPageAsync
```

撤销：

```text
InfiniteCanvas.Undo.cs ：RecordUndo / Undo / Redo
InfiniteCanvas.xaml.cs ：OnStrokeCollected / OnStrokeErasing / SplitPageStroke / SplitFreeStroke
```

## 6. 当前 Git 状态

已修改但未提交：

```text
src/PDFReaderX.App/Controls/InfiniteCanvas.xaml.cs
src/PDFReaderX.App/MainWindow.xaml
src/PDFReaderX.App/MainWindow.xaml.cs
```

查看完整 diff：

```powershell
git diff -- src/PDFReaderX.App/Controls/InfiniteCanvas.xaml.cs src/PDFReaderX.App/MainWindow.xaml src/PDFReaderX.App/MainWindow.xaml.cs
```

## 7. 项目约定

- 默认回复中文。
- release 文案用 markdown，带标题和分组列表。
- git commit 前必须先把提交文案给用户确认。
- tag 推送等远程写操作前要先询问。
- 如果之后要发版，需要先同步更新 README 的版本徽章、下载/打包文件名、更新日志，再改 csproj 版本并打包。
