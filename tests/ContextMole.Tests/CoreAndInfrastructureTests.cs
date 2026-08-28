using ContextMole.Core;
using ContextMole.Infrastructure;

namespace ContextMole.Tests;

public sealed class CoreAndInfrastructureTests
{
    [Fact]
    public void TextNormalization_CleansAndNormalizesSearchText()
    {
        Assert.Equal("Hello\nWorld", TextNormalization.ForDisplay("\u00ADHello\u200B \t\r\nWorld\0"));
        Assert.Equal("ABC informação", TextNormalization.ForSearch("  ＡＢＣ\t\r\ninfor-\nmação  ",
            dehyphenateLineBreaks: true));
        Assert.Equal("AÇÃO 9", TextNormalization.NameKey(" ação\t9 "));

        Assert.Equal("\"alpha\" OR \"ação\" OR \"beta_2\" OR \"42\"",
            TextNormalization.QuoteFtsTerms("alpha, ação beta_2 + 42"));

        var manyTerms = TextNormalization.QuoteFtsTerms(
            string.Join(' ', Enumerable.Range(0, 70).Select(index => $"term{index}")))
            .Split(" OR ", StringSplitOptions.None);
        Assert.Equal(64, manyTerms.Length);
        Assert.Equal("\"term63\"", manyTerms[^1]);
    }

    [Fact]
    public void SupportedContent_RecognizesThePublishedFormatCatalogCaseInsensitively()
    {
        string[] expected =
        [
            ".pdf",
            ".docx", ".docm", ".dotx", ".dotm",
            ".xlsx", ".xlsm", ".xltx", ".xltm",
            ".pptx", ".pptm", ".ppsx", ".ppsm", ".potx", ".potm",
            ".odt", ".ods", ".odp", ".rtf",
            ".txt", ".log", ".rst", ".adoc", ".tex", ".md", ".markdown",
            ".csv", ".tsv", ".json", ".jsonl", ".xml", ".yaml", ".yml", ".toml",
            ".html", ".htm", ".mht", ".mhtml", ".epub",
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff",
            ".eml", ".msg",
            ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".tar.gz"
        ];

        Assert.Equal(expected, SupportedContent.Extensions);
        Assert.All(expected, extension =>
            Assert.True(SupportedContent.IsSupported($"document{extension.ToUpperInvariant()}")));
        Assert.False(SupportedContent.IsSupported("document.exe"));
        Assert.False(SupportedContent.IsSupported("README"));
        Assert.False(SupportedContent.IsSupported("document.pdf.backup"));
    }

    [Theory]
    [InlineData(CpuUsageProfile.Light, 1, 1)]
    [InlineData(CpuUsageProfile.Normal, 1, 1)]
    [InlineData(CpuUsageProfile.Heavy, 1, 1)]
    [InlineData(CpuUsageProfile.Light, 6, 1)]
    [InlineData(CpuUsageProfile.Normal, 6, 2)]
    [InlineData(CpuUsageProfile.Heavy, 6, 4)]
    [InlineData(CpuUsageProfile.Light, 10, 2)]
    [InlineData(CpuUsageProfile.Normal, 10, 4)]
    [InlineData(CpuUsageProfile.Heavy, 10, 8)]
    public void CpuThreadLimit_UsesTheConfiguredPercentageWithAtLeastOneThread(
        CpuUsageProfile profile,
        int logicalProcessorCount,
        int expected)
    {
        Assert.Equal(expected, CpuUsageSettings.CalculateThreadLimit(profile, logicalProcessorCount));
    }

    [Fact]
    public void CpuThreadLimit_RejectsInvalidInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CpuUsageSettings.CalculateThreadLimit(CpuUsageProfile.Normal, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CpuUsageSettings.CalculateThreadLimit((CpuUsageProfile)999, 8));
    }

    [Fact]
    public async Task GlobalCpuBudget_AdmitsWorkersOnlyUpToTheGlobalLimit()
    {
        var settings = new TestCpuUsageSettings(CpuUsageProfile.Light, logicalProcessorCount: 10);
        using var budget = new GlobalCpuBudget(settings);
        using var first = await budget.AcquireWorkerAsync(CancellationToken.None);
        using var second = await budget.AcquireWorkerAsync(CancellationToken.None);

        var pending = budget.AcquireWorkerAsync(CancellationToken.None).AsTask();
        Assert.False(pending.IsCompleted);

        first.Dispose();
        using var admitted = await pending.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GlobalCpuBudget_ActiveWorkerCanBorrowTheFullCapacity()
    {
        var settings = new TestCpuUsageSettings(CpuUsageProfile.Light, logicalProcessorCount: 10);
        using var budget = new GlobalCpuBudget(settings);
        using var worker = await budget.AcquireWorkerAsync(CancellationToken.None);
        using var activation = worker.Activate();
        using var fullCapacity = await budget.AcquireFullCapacityAsync(CancellationToken.None);

        Assert.Equal(2, fullCapacity.ThreadCount);
        var pending = budget.AcquireWorkerAsync(CancellationToken.None).AsTask();
        Assert.False(pending.IsCompleted);

        fullCapacity.Dispose();
        using var admitted = await pending.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GlobalCpuBudget_CancelledFullCapacityRequestRestoresItsWorkerReservation()
    {
        var settings = new TestCpuUsageSettings(CpuUsageProfile.Light, logicalProcessorCount: 10);
        using var budget = new GlobalCpuBudget(settings);
        using var first = await budget.AcquireWorkerAsync(CancellationToken.None);
        using var second = await budget.AcquireWorkerAsync(CancellationToken.None);
        using var activation = first.Activate();
        using var cancellation = new CancellationTokenSource();

        var pendingFullCapacity = budget.AcquireFullCapacityAsync(cancellation.Token).AsTask();
        Assert.False(pendingFullCapacity.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pendingFullCapacity);

        var pendingWorker = budget.AcquireWorkerAsync(CancellationToken.None).AsTask();
        Assert.False(pendingWorker.IsCompleted);
        second.Dispose();
        using var admitted = await pendingWorker.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public void CpuUsageSettings_PersistsChangesOnceAndFallsBackForInvalidDiskState()
    {
        using var paths = new TemporaryAppPaths();
        var settings = new CpuUsageSettings(paths);
        var changes = 0;
        settings.Changed += (_, _) => changes++;

        Assert.Equal(CpuUsageProfile.Normal, settings.Profile);
        settings.SetProfile(CpuUsageProfile.Heavy);
        settings.SetProfile(CpuUsageProfile.Heavy);

        Assert.Equal(1, changes);
        Assert.Equal(CpuUsageProfile.Heavy, new CpuUsageSettings(paths).Profile);
        Assert.Equal("Heavy", File.ReadAllText(paths.CpuSettingsPath));

        File.WriteAllText(paths.CpuSettingsPath, "not-a-profile");
        Assert.Equal(CpuUsageProfile.Normal, new CpuUsageSettings(paths).Profile);
    }

    [Fact]
    public void EmbeddingModelSettings_PersistsSignalsAndIgnoresInvalidRefreshes()
    {
        using var paths = new TemporaryAppPaths();
        var settings = new EmbeddingModelSettings(paths);
        var changes = 0;
        settings.Changed += (_, _) => changes++;

        Assert.Equal(EmbeddingModelChoice.Granite311M, settings.Model);
        settings.SetModel(EmbeddingModelChoice.Granite97M);
        settings.SetModel(EmbeddingModelChoice.Granite97M);

        Assert.Equal(1, changes);
        Assert.Equal(EmbeddingModelChoice.Granite97M, new EmbeddingModelSettings(paths).Model);
        Assert.Equal("Granite97M", File.ReadAllText(paths.EmbeddingSettingsPath));

        new EmbeddingModelSettings(paths).SetModel(EmbeddingModelChoice.Granite311M);
        Assert.True(settings.RefreshFromDisk());
        Assert.Equal(EmbeddingModelChoice.Granite311M, settings.Model);
        Assert.Equal(2, changes);

        File.WriteAllText(paths.EmbeddingSettingsPath, "not-a-model");
        Assert.False(settings.RefreshFromDisk());
        Assert.Equal(EmbeddingModelChoice.Granite311M, settings.Model);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task EmbeddingGenerator_CachesUnavailableLoadsUntilRelevantStateChanges()
    {
        using var paths = new TemporaryAppPaths();
        var cpuSettings = new TestCpuUsageSettings(CpuUsageProfile.Normal, logicalProcessorCount: 8);
        var cpuBudget = new CountingCpuBudget(cpuSettings);
        var modelSettings = new EmbeddingModelSettings(paths);
        await using var generator = new GraniteEmbeddingGenerator(paths, cpuSettings, modelSettings, cpuBudget);
        var cancellationToken = TestContext.Current.CancellationToken;

        await generator.ReloadAsync(cancellationToken);
        Assert.False(generator.IsAvailable);
        Assert.Equal(1, cpuBudget.FullCapacityAcquisitions);

        await generator.ReloadAsync(cancellationToken);
        Assert.Equal(1, cpuBudget.FullCapacityAcquisitions);

        var model = GraniteEmbeddingModels.Get(modelSettings.Model);
        var modelDirectory = Path.Combine(paths.AssetsDirectory, "granite", model.Revision);
        Directory.CreateDirectory(modelDirectory);
        File.WriteAllText(Path.Combine(modelDirectory, "tokenizer.json"), "installation changed");

        await generator.ReloadAsync(cancellationToken);
        await generator.ReloadAsync(cancellationToken);
        Assert.Equal(2, cpuBudget.FullCapacityAcquisitions);

        cpuSettings.SetProfile(CpuUsageProfile.Heavy);
        await generator.ReloadAsync(cancellationToken);
        Assert.Equal(3, cpuBudget.FullCapacityAcquisitions);
    }

    [Fact]
    public void ModelInstallerDistinguishesMissingAssetsFromAssetsNeedingRepair()
    {
        using var paths = new TemporaryAppPaths();
        var cpuSettings = new TestCpuUsageSettings(CpuUsageProfile.Normal, logicalProcessorCount: 8);
        var cpuBudget = new CountingCpuBudget(cpuSettings);
        var modelSettings = new EmbeddingModelSettings(paths);
        using var installer = new GraniteModelInstaller(paths, modelSettings, cpuBudget);
        var model = GraniteEmbeddingModels.Get(modelSettings.Model);
        var modelDirectory = Path.Combine(paths.AssetsDirectory, "granite", model.Revision);

        Assert.False(installer.HasModelAssets(model.Choice));
        Directory.CreateDirectory(modelDirectory);
        File.WriteAllText(Path.Combine(modelDirectory, "tokenizer.json"), "test tokenizer");
        File.WriteAllText(Path.Combine(modelDirectory, "model.onnx"), "test model");
        File.WriteAllText(Path.Combine(modelDirectory, "model_quint8_avx2.onnx"), "test model");

        Assert.True(installer.HasModelAssets(model.Choice));
        Assert.False(installer.IsModelInstalled(model.Choice));
    }

    [Fact]
    public void GraniteModelCatalog_PreservesPinnedCompatibilityMetadata()
    {
        Assert.Equal(EmbeddingModelChoice.Granite311M, GraniteEmbeddingModels.DefaultChoice);
        Assert.Equal(2, GraniteEmbeddingModels.All.Count);
        Assert.Equal(2, GraniteEmbeddingModels.All.Select(model => model.Choice).Distinct().Count());

        AssertModel(
            GraniteEmbeddingModels.Get(EmbeddingModelChoice.Granite311M),
            "ibm-granite/granite-embedding-311m-multilingual-r2",
            "44399559930365213510b1ee2eb15ded83374f0e",
            "0087c868b33bad550a78a08d19798cfd7f713cde4f020803b8f51f405503e15f",
            "f1fdd44e7e1ac51f12ab7957c7bd092e064d596c288513bf9d326842f669edee",
            "75f9f258bf5013f5fe8a4dad61dd0fd16ac0cbaa7a106e3d3f41c2d04a42d541",
            bosTokenId: 2,
            sourceDimensions: 768,
            normalization: "l2-after-matryoshka",
            requiresGemmaTerms: true);

        AssertModel(
            GraniteEmbeddingModels.Get(EmbeddingModelChoice.Granite97M),
            "ibm-granite/granite-embedding-97m-multilingual-r2",
            "835ad14087e140460703cf0fae09f97d469d65c2",
            "4f2842d568e2724370aec203652a42ac783c7937f8347a1a2cc7506d71f1582f",
            "a6022dd8220ea6f6595562a1328ee216f4a94faa55362f2f4747c80f1e78772e",
            "68e592b160673d30250824c1116bc6ab33f70efb22b97c9e1d7ce1e69c1c9d70",
            bosTokenId: 179934,
            sourceDimensions: 384,
            normalization: "l2",
            requiresGemmaTerms: false);

        Assert.Throws<ArgumentOutOfRangeException>(() => GraniteEmbeddingModels.Get((EmbeddingModelChoice)999));
    }

    private static void AssertModel(
        GraniteEmbeddingModelDefinition model,
        string modelId,
        string revision,
        string tokenizerSha,
        string quantizedSha,
        string fp32Sha,
        long bosTokenId,
        int sourceDimensions,
        string normalization,
        bool requiresGemmaTerms)
    {
        Assert.Equal(modelId, model.ModelId);
        Assert.Equal(revision, model.Revision);
        Assert.Equal(tokenizerSha, model.TokenizerSha);
        Assert.Equal(quantizedSha, model.QuantizedSha);
        Assert.Equal(fp32Sha, model.Fp32Sha);
        Assert.Equal(bosTokenId, model.BosTokenId);
        Assert.Equal(sourceDimensions, model.SourceDimensions);
        Assert.Equal(384, model.Dimensions);
        Assert.Equal("cls", model.Pooling);
        Assert.Equal(normalization, model.Normalization);
        Assert.Equal(requiresGemmaTerms, model.RequiresGemmaTerms);
        Assert.Matches("^[0-9a-f]{40}$", model.Revision);
        Assert.All(new[] { model.TokenizerSha, model.QuantizedSha, model.Fp32Sha },
            sha => Assert.Matches("^[0-9a-f]{64}$", sha));
    }

    private sealed class TestCpuUsageSettings(CpuUsageProfile profile, int logicalProcessorCount)
        : ICpuUsageSettings
    {
        public CpuUsageProfile Profile { get; private set; } = profile;
        public int LogicalProcessorCount { get; } = logicalProcessorCount;
        public int ThreadLimit => CpuUsageSettings.CalculateThreadLimit(Profile, LogicalProcessorCount);
        public int MaximumThreadLimit =>
            CpuUsageSettings.CalculateThreadLimit(CpuUsageProfile.Heavy, LogicalProcessorCount);
        public event EventHandler? Changed;

        public void SetProfile(CpuUsageProfile newProfile)
        {
            if (Profile == newProfile) return;
            Profile = newProfile;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class CountingCpuBudget(ICpuUsageSettings settings) : IGlobalCpuBudget
    {
        public int MaximumWorkerCount => settings.MaximumThreadLimit;
        public int FullCapacityAcquisitions { get; private set; }

        public ValueTask<ICpuWorkerLease> AcquireWorkerAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ICpuFullCapacityLease> AcquireFullCapacityAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FullCapacityAcquisitions++;
            return ValueTask.FromResult<ICpuFullCapacityLease>(new FullCapacityLease(settings.ThreadLimit));
        }

        private sealed class FullCapacityLease(int threadCount) : ICpuFullCapacityLease
        {
            public int ThreadCount { get; } = threadCount;
            public void Dispose()
            {
            }
        }
    }

    private sealed class TemporaryAppPaths : IAppPaths, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ContextMole.Tests", Guid.NewGuid().ToString("N"));

        public TemporaryAppPaths()
        {
            DataDirectory = Path.Combine(_root, "data");
            DatabasePath = Path.Combine(DataDirectory, "index.db");
            AssetsDirectory = Path.Combine(_root, "assets");
            LogsDirectory = Path.Combine(_root, "logs");
            TempDirectory = Path.Combine(_root, "temp");
            Directory.CreateDirectory(DataDirectory);
        }

        public string DataDirectory { get; }
        public string DatabasePath { get; }
        public string AssetsDirectory { get; }
        public string LogsDirectory { get; }
        public string TempDirectory { get; }
        public string CpuSettingsPath => Path.Combine(DataDirectory, "ui-state", "cpu-usage-profile.txt");
        public string EmbeddingSettingsPath => Path.Combine(DataDirectory, "ui-state", "embedding-model.txt");

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
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
