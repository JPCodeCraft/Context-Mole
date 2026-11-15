using ContextMole.Core;

namespace ContextMole.App.UI.ViewModels;

/// <summary>A CPU profile with the plain-language trade-off shown in Settings.</summary>
public sealed record CpuUsageOption(CpuUsageProfile Profile, int Percent, string Description)
{
    public static IReadOnlyList<CpuUsageOption> All { get; } =
    [
        new(CpuUsageProfile.Light, 20, "Slowest indexing. Keeps your PC responsive while you work."),
        new(CpuUsageProfile.Normal, 40, "Balanced for everyday use."),
        new(CpuUsageProfile.Heavy, 80, "Fastest indexing. Your PC may feel busy while it runs.")
    ];

    public string Title => Profile.ToString();
    public string Limit => $"Up to {Percent}% of CPU";

    public static CpuUsageOption For(CpuUsageProfile profile) =>
        All.FirstOrDefault(option => option.Profile == profile) ?? throw new ArgumentOutOfRangeException(nameof(profile));
}
