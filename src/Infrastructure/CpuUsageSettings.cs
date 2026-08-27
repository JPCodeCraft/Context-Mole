using System.Text;

using ContextMole.Core;

namespace ContextMole.Infrastructure;

public sealed class CpuUsageSettings : ICpuUsageSettings
{
    private readonly object _gate = new();
    private readonly string _settingsPath;
    private CpuUsageProfile _profile;

    public CpuUsageSettings(IAppPaths paths)
    {
        _settingsPath = Path.Combine(paths.DataDirectory, "ui-state", "cpu-usage-profile.txt");
        _profile = LoadProfile();
    }

    public CpuUsageProfile Profile
    {
        get { lock (_gate) return _profile; }
    }

    public int LogicalProcessorCount => Math.Max(1, Environment.ProcessorCount);
    public int ThreadLimit => CalculateThreadLimit(Profile, LogicalProcessorCount);
    public int MaximumThreadLimit => CalculateThreadLimit(CpuUsageProfile.Heavy, LogicalProcessorCount);
    public event EventHandler? Changed;

    public void SetProfile(CpuUsageProfile profile)
    {
        if (!Enum.IsDefined(profile))
            throw new ArgumentOutOfRangeException(nameof(profile));

        lock (_gate)
        {
            if (_profile == profile) return;
            SaveProfile(profile);
            _profile = profile;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static int CalculateThreadLimit(CpuUsageProfile profile, int logicalProcessorCount)
    {
        if (logicalProcessorCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(logicalProcessorCount));
        var percentage = profile switch
        {
            CpuUsageProfile.Light => 20,
            CpuUsageProfile.Normal => 40,
            CpuUsageProfile.Heavy => 80,
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };
        return Math.Max(1, logicalProcessorCount * percentage / 100);
    }

    private CpuUsageProfile LoadProfile()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return CpuUsageProfile.Normal;
            var value = File.ReadAllText(_settingsPath).Trim();
            return Enum.TryParse<CpuUsageProfile>(value, ignoreCase: true, out var profile) && Enum.IsDefined(profile)
                ? profile
                : CpuUsageProfile.Normal;
        }
        catch (IOException)
        {
            return CpuUsageProfile.Normal;
        }
        catch (UnauthorizedAccessException)
        {
            return CpuUsageProfile.Normal;
        }
    }

    private void SaveProfile(CpuUsageProfile profile)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, profile.ToString(), new UTF8Encoding(false));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ContextMoleException("settings_write_failed",
                $"The CPU usage profile could not be saved: {exception.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}