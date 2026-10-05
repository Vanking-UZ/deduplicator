using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.Core.Models;

namespace Deduplicator.App.ViewModels.Items;

/// <summary>Запись журнала операций.</summary>
public sealed class JournalItemViewModel
{
    public OperationLogEntry Model { get; }
    public JournalItemViewModel(OperationLogEntry model) => Model = model;

    public long Id => Model.Id;
    public string FileName => Model.FileName ?? "—";
    public string SizeText => Model.SizeBytes is { } s ? Format.Bytes(s) : "—";
    public string WhenText => Format.DateShort(Model.PerformedAt);
    public string SrcPath => Model.SrcPath ?? "—";
    public string DstPath => Model.DstPath ?? "—";

    public bool CanRestore => Model is { Reversible: true, Restored: false, OpType: OperationType.MoveToQuarantine };

    public string OpText => Model.OpType switch
    {
        OperationType.DeleteToRecycle => Strings.OpDeleteRecycle,
        OperationType.MoveToQuarantine => Strings.OpQuarantine,
        OperationType.Hardlink => Strings.OpHardlink,
        OperationType.Restore => Strings.OpRestore,
        _ => "—"
    };

    public string StatusText => Model.Restored ? Strings.OpRestored : "";
}
