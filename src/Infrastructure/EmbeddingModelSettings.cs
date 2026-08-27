using System.Text;

using ContextMole.Core;

namespace ContextMole.Infrastructure;

public sealed class EmbeddingModelSettings : IEmbeddingModelSettings
{
    private readonly object _gate = new();
    private readonly string _settingsPath;
    private EmbeddingModelChoice _model;

    public EmbeddingModelSettings(IAppPaths paths)
    {
        _settingsPath = Path.Combine(paths.DataDirectory, "ui-state", "embedding-model.txt");
        _model = TryLoadModel(out var model) ? model : GraniteEmbeddingModels.DefaultChoice;
    }

    public EmbeddingModelChoice Model
    {
        get { lock (_gate) return _model; }
    }

    public event EventHandler? Changed;

    public void SetModel(EmbeddingModelChoice model)
    {
        if (!Enum.IsDefined(model))
            throw new ArgumentOutOfRangeException(nameof(model));

        lock (_gate)
        {
            if (_model == model) return;
            SaveModel(model);
            _model = model;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool RefreshFromDisk()
    {
        var changed = false;
        lock (_gate)
        {
            if (!TryLoadModel(out var model) || _model == model) return false;
            _model = model;
            changed = true;
        }

        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    private bool TryLoadModel(out EmbeddingModelChoice model)
    {
        model = GraniteEmbeddingModels.DefaultChoice;
        try
        {
            if (!File.Exists(_settingsPath)) return false;
            var value = File.ReadAllText(_settingsPath).Trim();
            if (Enum.TryParse<EmbeddingModelChoice>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            {
                model = parsed;
                return true;
            }
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void SaveModel(EmbeddingModelChoice model)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, model.ToString(), new UTF8Encoding(false));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ContextMoleException("settings_write_failed",
                $"The embedding model selection could not be saved: {exception.Message}");
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