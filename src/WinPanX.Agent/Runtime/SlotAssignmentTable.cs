using WinPanX.Core.Contracts;

namespace WinPanX.Agent.Runtime;

internal sealed class SlotAssignmentTable
{
    private readonly object _sync = new();
    private readonly Dictionary<AppRuntimeId, int> _assignedByApp = [];
    private readonly Dictionary<int, AppRuntimeId> _assignedBySlot = [];

    public int GetOrAssign(AppRuntimeId appId)
    {
        lock (_sync)
        {
            if (_assignedByApp.TryGetValue(appId, out var existing))
            {
                return existing;
            }

            for (var slot = 1; slot <= 7; slot++)
            {
                if (_assignedBySlot.ContainsKey(slot))
                {
                    continue;
                }

                _assignedBySlot[slot] = appId;
                _assignedByApp[appId] = slot;
                return slot;
            }

            _assignedByApp[appId] = 8;
            return 8;
        }
    }

    public bool TryGet(AppRuntimeId appId, out int slotIndex)
    {
        lock (_sync)
        {
            return _assignedByApp.TryGetValue(appId, out slotIndex);
        }
    }

    public bool Release(AppRuntimeId appId)
    {
        lock (_sync)
        {
            if (!_assignedByApp.Remove(appId, out var slotIndex))
            {
                return false;
            }

            if (slotIndex is >= 1 and <= 7)
            {
                _assignedBySlot.Remove(slotIndex);
            }

            return true;
        }
    }

    public IReadOnlyDictionary<AppRuntimeId, int> Snapshot()
    {
        lock (_sync)
        {
            return new Dictionary<AppRuntimeId, int>(_assignedByApp);
        }
    }
}

