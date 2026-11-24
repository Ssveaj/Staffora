namespace Staffora.Core.Models;

public sealed record SetupChecklistItem(string Key, string Title, string Detail, bool Done, string Path, string LinkText);

public sealed record SetupChecklist(IReadOnlyList<SetupChecklistItem> Items)
{
    public int DoneCount => Items.Count(item => item.Done);

    public bool IsComplete => Items.Count > 0 && DoneCount == Items.Count;
}
