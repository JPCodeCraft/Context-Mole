#:property TargetFramework=net10.0
#:project ../src/Core/MCPIndexSearch.Core.csproj
#:project ../src/Infrastructure/MCPIndexSearch.Infrastructure.csproj

using MCPIndexSearch.Core;
using MCPIndexSearch.Infrastructure;

var data = Environment.GetEnvironmentVariable("MCPINDEXSEARCH_DATA_DIR")
    ?? throw new InvalidOperationException("Set MCPINDEXSEARCH_DATA_DIR to an isolated smoke directory.");
var paths = new SmokePaths(Path.Combine(data, $"cpu-policy-{Guid.NewGuid():N}"));

AssertLimit(CpuUsageProfile.Light, 10, 2);
AssertLimit(CpuUsageProfile.Normal, 10, 4);
AssertLimit(CpuUsageProfile.Heavy, 10, 8);
AssertLimit(CpuUsageProfile.Light, 1, 1);

var settings = new CpuUsageSettings(paths);
if (settings.Profile != CpuUsageProfile.Normal)
    throw new InvalidOperationException("The first-run CPU profile was not Normal.");

var changes = 0;
settings.Changed += (_, _) => changes++;
settings.SetProfile(CpuUsageProfile.Heavy);
settings.SetProfile(CpuUsageProfile.Heavy);
if (changes != 1)
    throw new InvalidOperationException($"Expected one settings event, received {changes}.");
if (new CpuUsageSettings(paths).Profile != CpuUsageProfile.Heavy)
    throw new InvalidOperationException("The persisted CPU profile was not restored.");

var dynamicSettings = new FixedCpuSettings(CpuUsageProfile.Light, 10);
using var budget = new GlobalCpuBudget(dynamicSettings);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

var first = await budget.AcquireWorkerAsync(timeout.Token);
var second = await budget.AcquireWorkerAsync(timeout.Token);
var waitingWorker = budget.AcquireWorkerAsync(timeout.Token).AsTask();
if (await Task.WhenAny(waitingWorker, Task.Delay(150, timeout.Token)) == waitingWorker)
    throw new InvalidOperationException("Light admitted more than two one-thread workers from a ten-thread budget.");
first.Dispose();
using var admittedWorker = await waitingWorker.WaitAsync(timeout.Token);
second.Dispose();
admittedWorker.Dispose();

using var adaptiveWorker = await budget.AcquireWorkerAsync(timeout.Token);
using (adaptiveWorker.Activate())
{
    using var fullCapacity = await budget.AcquireFullCapacityAsync(timeout.Token);
    if (fullCapacity.ThreadCount != 2)
        throw new InvalidOperationException($"A lone Light job received {fullCapacity.ThreadCount} threads instead of two.");

    var blockedByFullCapacity = budget.AcquireWorkerAsync(timeout.Token).AsTask();
    if (await Task.WhenAny(blockedByFullCapacity, Task.Delay(150, timeout.Token)) == blockedByFullCapacity)
        throw new InvalidOperationException("A worker entered while one model held the complete CPU budget.");
    fullCapacity.Dispose();
    using var workerAfterDowngrade = await blockedByFullCapacity.WaitAsync(timeout.Token);
}

using var competingWorker = await budget.AcquireWorkerAsync(timeout.Token);
using (adaptiveWorker.Activate())
{
    var fullCapacityWaiting = budget.AcquireFullCapacityAsync(timeout.Token).AsTask();
    if (await Task.WhenAny(fullCapacityWaiting, Task.Delay(150, timeout.Token)) == fullCapacityWaiting)
        throw new InvalidOperationException("Full-capacity inference entered before a competing worker drained.");
    var lateWorker = budget.AcquireWorkerAsync(timeout.Token).AsTask();
    competingWorker.Dispose();
    using var fullAfterDrain = await fullCapacityWaiting.WaitAsync(timeout.Token);
    if (fullAfterDrain.ThreadCount != 2)
        throw new InvalidOperationException("The full budget changed while waiting for a competing worker.");
    if (lateWorker.IsCompleted)
        throw new InvalidOperationException("A newly queued worker bypassed a waiting full-capacity inference.");
    fullAfterDrain.Dispose();
    using var workerAfterFullCapacity = await lateWorker.WaitAsync(timeout.Token);
}

adaptiveWorker.Dispose();

using var cancellationWorker = await budget.AcquireWorkerAsync(timeout.Token);
using var cancellationCompetitor = await budget.AcquireWorkerAsync(timeout.Token);
using (cancellationWorker.Activate())
using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
{
    try
    {
        using var unexpected = await budget.AcquireFullCapacityAsync(cancelled.Token);
        throw new InvalidOperationException("A blocked full-capacity request ignored cancellation.");
    }
    catch (OperationCanceledException) when (cancelled.IsCancellationRequested)
    {
    }

    var workerAfterCancellation = budget.AcquireWorkerAsync(timeout.Token).AsTask();
    if (await Task.WhenAny(workerAfterCancellation, Task.Delay(150, timeout.Token)) == workerAfterCancellation)
        throw new InvalidOperationException("Cancelling a full-capacity request did not restore its worker reservation.");
    cancellationCompetitor.Dispose();
    using var admittedAfterCancellation = await workerAfterCancellation.WaitAsync(timeout.Token);
}
cancellationWorker.Dispose();

dynamicSettings.SetProfile(CpuUsageProfile.Heavy);
using var heavyCapacity = await budget.AcquireFullCapacityAsync(timeout.Token);
if (heavyCapacity.ThreadCount != 8)
    throw new InvalidOperationException($"Heavy full-capacity inference received {heavyCapacity.ThreadCount} threads instead of eight.");

Console.WriteLine("CPU_USAGE_POLICY_SMOKE_OK persisted=Heavy light=2/10 normal=4/10 heavy=8/10 lone_job=full_budget global_exclusivity=verified");

static void AssertLimit(CpuUsageProfile profile, int processors, int expected)
{
    var actual = CpuUsageSettings.CalculateThreadLimit(profile, processors);
    if (actual != expected)
        throw new InvalidOperationException($"{profile} expected {expected} threads for {processors} processors, received {actual}.");
}

sealed class FixedCpuSettings(CpuUsageProfile profile, int logicalProcessors) : ICpuUsageSettings
{
    public CpuUsageProfile Profile { get; private set; } = profile;
    public int LogicalProcessorCount { get; } = logicalProcessors;
    public int ThreadLimit => CpuUsageSettings.CalculateThreadLimit(Profile, LogicalProcessorCount);
    public int MaximumThreadLimit => CpuUsageSettings.CalculateThreadLimit(CpuUsageProfile.Heavy, LogicalProcessorCount);
    public event EventHandler? Changed;

    public void SetProfile(CpuUsageProfile next)
    {
        if (Profile == next) return;
        Profile = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

sealed class SmokePaths : IAppPaths
{
    public SmokePaths(string root)
    {
        DataDirectory = root;
        DatabasePath = Path.Combine(root, "index.db");
        AssetsDirectory = Path.Combine(root, "assets");
        LogsDirectory = Path.Combine(root, "logs");
        TempDirectory = Path.Combine(root, "temp");
    }

    public string DataDirectory { get; }
    public string DatabasePath { get; }
    public string AssetsDirectory { get; }
    public string LogsDirectory { get; }
    public string TempDirectory { get; }
}
