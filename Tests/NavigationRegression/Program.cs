using AutoExile.Systems;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;

void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    Console.WriteLine($"PASS: {name}");
}

// Exercise the production NavigateTo entry point. A null game is intentional:
// reusing/consuming an existing request must not read or clone game memory again.
var target = new Vector2(40, 40);
var resultType = typeof(NavigationSystem).GetNestedType("PathResult", BindingFlags.NonPublic)!;
var taskSourceType = typeof(TaskCompletionSource<>).MakeGenericType(resultType);
var taskSource = Activator.CreateInstance(taskSourceType)!;
var task = taskSourceType.GetProperty("Task")!.GetValue(taskSource);
var nav = new NavigationSystem();
void SetField(string name, object value) => typeof(NavigationSystem)
    .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(nav, value);
void Prepare(object pending)
{
    SetField("_pendingPathTask", pending);
    SetField("_pendingPathTarget", target);
    typeof(NavigationSystem).GetProperty(nameof(NavigationSystem.IsNavigating))!.SetValue(nav, true);
}
Prepare(task!);
Check(nav.NavigateTo(null!, target) && nav.IsPathfinding, "Repeated request preserves unfinished route");
var result = Activator.CreateInstance(resultType)!;
resultType.GetProperty("Path")!.SetValue(result, new List<NavWaypoint>());
taskSourceType.GetMethod("SetResult")!.Invoke(taskSource, [result]);
Check(!nav.NavigateTo(null!, target) && !nav.IsPathfinding && !nav.IsNavigating,
    "Completed failed route is consumed, never discarded and requeued");

var cancelledSource = Activator.CreateInstance(taskSourceType)!;
taskSourceType.GetMethod("SetCanceled", Type.EmptyTypes)!.Invoke(cancelledSource, null);
Prepare(taskSourceType.GetProperty("Task")!.GetValue(cancelledSource)!);
Check(!nav.NavigateTo(null!, target) && !nav.IsNavigating && !nav.IsPathfinding,
    "Cancelled route clears planning state instead of blocking movement forever");

var nativeInput = typeof(NavigationSystem).Assembly.GetType("AutoExile.Systems.NativeMouseInput+NativeInput")!;
Check(Marshal.SizeOf(nativeInput) == (Environment.Is64BitProcess ? 40 : 28),
    "Native mouse INPUT layout matches Windows ABI without sending mouse events");

var resources = typeof(NavigationSystem).Assembly.GetManifestResourceNames();
Check(resources.Contains("webui.index.html") && resources.Contains("uniqueArtMapping.json"),
    "Built plugin retains the embedded dashboard and item resources");
