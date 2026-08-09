namespace PDFReaderX.App.Controls;

/// <summary>无限画布的简单撤销/重做：墨迹笔划、元素增删与移动缩放均记录为成对的逆操作。</summary>
public partial class InfiniteCanvas
{
    private const int MaxUndoDepth = 200;

    private readonly List<(Action Undo, Action Redo)> _undoStack = new();
    private readonly List<(Action Undo, Action Redo)> _redoStack = new();

    /// <summary>撤销/重做可用状态变化（供按钮刷新）。</summary>
    public event EventHandler? UndoStateChanged;

    /// <summary>内容或视口被修改时触发（供保存按钮判断是否需要写入）。</summary>
    public event EventHandler? ModifiedChanged;

    public bool IsModified { get; private set; }

    /// <summary>保存成功后清除修改标记。</summary>
    public void ResetModified() => IsModified = false;

    private void MarkModified()
    {
        IsModified = true;
        ModifiedChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool CanUndo => _undoStack.Count > 0;

    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>记录一步可撤销操作。新记录会清空重做栈。</summary>
    private void RecordUndo(Action undo, Action redo)
    {
        _undoStack.Add((undo, redo));
        if (_undoStack.Count > MaxUndoDepth)
        {
            _undoStack.RemoveAt(0); // 丢弃最旧的历史
        }
        _redoStack.Clear();
        UndoStateChanged?.Invoke(this, EventArgs.Empty);
        MarkModified();
    }

    public void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }
        var operation = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        operation.Undo();
        _redoStack.Add(operation);
        UndoStateChanged?.Invoke(this, EventArgs.Empty);
        MarkModified();
    }

    public void Redo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }
        var operation = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        operation.Redo();
        _undoStack.Add(operation);
        UndoStateChanged?.Invoke(this, EventArgs.Empty);
        MarkModified();
    }

    /// <summary>切换文档时清空全部历史。</summary>
    private void ClearUndoHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        UndoStateChanged?.Invoke(this, EventArgs.Empty);
    }
}
