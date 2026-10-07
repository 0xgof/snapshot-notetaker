namespace SnapshotNotetaker.Model;

/// <summary>
/// Snapshot-based undo. Callers push the state captured *before* a change.
/// The manager outlives document instances (it is re-bound when a snapshot is reopened),
/// so history survives switching between snapshots in the library.
/// </summary>
public sealed class UndoManager
{
    private const int Capacity = 200;
    private readonly List<DocumentState> _undo = new();
    private readonly Stack<DocumentState> _redo = new();

    public UndoManager(AnnotationDocument document) => Document = document;

    public AnnotationDocument Document { get; set; }

    public event EventHandler? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Push(DocumentState before)
    {
        _undo.Add(before);
        if (_undo.Count > Capacity) _undo.RemoveAt(0);
        _redo.Clear();
        Document.IsDirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs a mutation and records it as one undo step if anything changed.</summary>
    public void Record(Action mutate)
    {
        var before = Document.CaptureState();
        mutate();
        if (!before.SameContent(Document.CaptureState())) Push(before);
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var state = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Push(Document.CaptureState());
        Document.RestoreState(state);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(Document.CaptureState());
        Document.RestoreState(_redo.Pop());
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
