using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

/// <summary>
/// Stages and commits user-reviewed tag edits. The service intentionally
/// accepts only the Steam build that has been validated for this editor flow.
/// </summary>
public sealed class RuntimeTagEditSessionService(RuntimeTagMemoryService memory)
{
    public const string SupportedBuildId = "2026-08-17-steam";

    private readonly RuntimeTagMemoryService _memory = memory;

    public bool IsSupportedBuild =>
        _memory.IsConnected &&
        SupportedBuildId.Equals(_memory.BuildProfileId, StringComparison.OrdinalIgnoreCase);

    public string SupportMessage => IsSupportedBuild
        ? "Steam August 17 runtime tag editing is available."
        : $"Runtime tag commits are limited to Steam {SupportedBuildId}.";

    public RuntimeTagEditPatch Stage(
        RuntimeTagEditSession session,
        RuntimeTagFieldValue field,
        byte[] value,
        IReadOnlyList<RuntimeTagModBlockStep> blocks,
        RuntimeTagEntry? referenceTarget = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(blocks);
        if (!field.CanWrite)
            throw new InvalidOperationException($"'{field.Name}' cannot be written.");
        if (value.Length != field.Size)
            throw new ArgumentException(
                $"'{field.Name}' needs {field.Size} byte(s), but received {value.Length}.",
                nameof(value));

        byte[] expected = session.Patches
            .FirstOrDefault(patch => patch.Field.Address == field.Address)
            ?.Expected
            ?? _memory.ReadBytes(field.Address, value.Length);
        var patch = new RuntimeTagEditPatch(
            field,
            expected,
            value,
            blocks.ToArray(),
            referenceTarget);
        session.Stage(patch);
        return patch;
    }

    public IReadOnlyList<RuntimeMemoryWrite> Commit(RuntimeTagEditSession session)
    {
        EnsureSupportedBuild();
        IReadOnlyList<RuntimeMemoryWrite> writes = session.TakePendingWrites();
        if (writes.Count == 0)
            throw new InvalidOperationException("There are no staged runtime tag edits to commit.");

        _memory.ApplyTransaction(writes);
        session.MarkCommitted(writes);
        return writes;
    }

    public IReadOnlyList<RuntimeMemoryWrite> Undo(RuntimeTagEditSession session)
    {
        EnsureSupportedBuild();
        IReadOnlyList<RuntimeMemoryWrite> writes = session.TakeUndoWrites();
        _memory.ApplyTransaction(writes);
        session.MarkUndone();
        return writes;
    }

    private void EnsureSupportedBuild()
    {
        if (!IsSupportedBuild)
            throw new NotSupportedException(SupportMessage);
    }
}
