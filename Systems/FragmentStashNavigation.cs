using ExileCore;
using ExileCore.PoEMemory;

namespace AutoExile.Systems;

/// <summary>Resolve visible special-tab section labels instead of hard-coded UI offsets.</summary>
public static class FragmentStashNavigation
{
    private static readonly HashSet<string> ReportedMissing = new();
    public static bool IsFragmentTab(GameController gc) =>
        gc.IngameState.IngameUi.StashElement?.VisibleStash?.InvType.ToString() == "FragmentStash";

    public static string? SectionFor(string path) =>
        path.Contains("Scarabs/", StringComparison.OrdinalIgnoreCase) ? "Scarabs" :
        path.Contains("CurrencyVaalFragment", StringComparison.OrdinalIgnoreCase) ? "Fragments" : null;

    public static bool Select(GameController gc, string section)
    {
        var labelText = section == "Fragments" ? "General" : section == "Scarabs" ? "Scarab" : section;
        var root = gc.IngameState.IngameUi.StashElement;
        if (root?.IsVisible != true || !BotInput.CanAct) return false;
        var diagnostic = new List<object>();
        var queue = new Queue<(Element Element, int Depth, string Path)>();
        queue.Enqueue((root, 0, ""));
        for (var visited = 0; queue.Count > 0 && visited < 2500; visited++)
        {
            var (element, depth, path) = queue.Dequeue();
            if (!ReportedMissing.Contains(section) && element.IsVisible)
            {
                var r = element.GetClientRect();
                diagnostic.Add(new { path, text = element.Text, children = element.ChildCount, r.X, r.Y, r.Width, r.Height });
            }
            if (element.IsVisible && string.Equals(element.Text?.Trim(), labelText, StringComparison.OrdinalIgnoreCase))
            {
                var rect = element.GetClientRect();
                if (rect.Width > 0 && rect.Height > 0)
                    return BotInput.ClickLabel(gc, rect);
            }
            if (depth >= 14) continue;
            for (var i = 0; i < Math.Min(element.ChildCount, 600); i++)
            {
                var child = element.GetChildAtIndex(i);
                if (child?.IsVisible == true) queue.Enqueue((child, depth + 1, path + "," + i));
            }
        }
        if (ReportedMissing.Add(section))
        {
            var file = System.IO.Path.Combine(BotCore.Instance!.DirectoryFullName, "AwakeningData", "fragment-ui-" + section + ".json");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            System.IO.File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(diagnostic));
            DebugWindow.LogMsg("[FragmentStashNavigation] Missing section label " + section + "; UI evidence: " + file);
        }
        return false;
    }
}
