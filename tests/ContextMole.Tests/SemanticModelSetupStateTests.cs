using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;

using ContextMole.App.UI;
using ContextMole.App.UI.ViewModels;
using ContextMole.Core;
using ContextMole.Indexing;
using ContextMole.Infrastructure;

using Microsoft.Extensions.Logging.Abstractions;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class SemanticModelSetupStateTests
{
    private static bool SupportsQuantization => RuntimeInformation.ProcessArchitecture == Architecture.X64 && Avx2.IsSupported;

    [Fact]
    public async Task StaleValidationOffersRepairWhileFullPrecisionRemainsAvailable()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.WriteInstalledModel(EmbeddingModelChoice.Granite97M);
        var viewModel = fixture.ViewModel;

        Assert.True(fixture.Installer.IsModelInstalled(EmbeddingModelChoice.Granite97M));
        Assert.Equal(SupportsQuantization, fixture.Installer.NeedsTokenizationValidation(EmbeddingModelChoice.Granite97M));
        Assert.False(viewModel.IsSemanticSearchUnavailable);
        Assert.Equal("fp32", fixture.Embeddings.Policy.Precision);
        Assert.Equal(SupportsQuantization, viewModel.NeedsSemanticModelValidation);
        Assert.Equal(SupportsQuantization && fixture.Installer.IsSupported, viewModel.IsSemanticSearchSetupVisible);
        Assert.Equal(SupportsQuantization && fixture.Installer.IsSupported, viewModel.CanSetUpSemanticSearch);
        Assert.Equal("Verify and repair semantic model", viewModel.SemanticSearchSetupButtonLabel);
        Assert.Equal(SupportsQuantization ? "Verification recommended" : "Ready", viewModel.SemanticSearchStatusLabel);
        if (SupportsQuantization) Assert.Contains("full precision", viewModel.SemanticSearchStatusMessage);
        Assert.Equal(!SupportsQuantization, viewModel.IsSemanticSearchReadyStatus);
        Assert.Equal(SupportsQuantization && fixture.Installer.IsSupported, viewModel.IsSemanticSearchWarningStatus);
        Assert.False(viewModel.IsSemanticSearchErrorStatus);
        Assert.False(fixture.Installer.HasRecordedTermsAcceptance);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CurrentValidationOrDisabledQuantizationDoesNotOfferUnneededRepair(
        bool currentValidation, bool quantizationDisabled)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.WriteInstalledModel(EmbeddingModelChoice.Granite97M, currentValidation, quantizationDisabled);
        var viewModel = fixture.ViewModel;

        Assert.True(fixture.Installer.IsModelInstalled(EmbeddingModelChoice.Granite97M));
        Assert.False(fixture.Installer.NeedsTokenizationValidation(EmbeddingModelChoice.Granite97M));
        Assert.False(viewModel.NeedsSemanticModelValidation);
        Assert.False(viewModel.IsSemanticSearchSetupVisible);
        Assert.False(viewModel.CanSetUpSemanticSearch);
        Assert.True(viewModel.IsSemanticSearchReadyStatus);
        Assert.False(viewModel.IsSemanticSearchWarningStatus);
        Assert.Equal("Ready", viewModel.SemanticSearchStatusLabel);
        var dialogCalls = 0;
        await viewModel.SetUpSemanticSearchAsync(_ =>
        {
            dialogCalls++;
            return Task.FromResult(true);
        });
        Assert.Equal(0, dialogCalls);
        Assert.Equal(0, fixture.Embeddings.ReloadCount);
    }

    [Fact]
    public async Task RevalidationStateAndBindingNotificationsFollowSole97MModel()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.WriteInstalledModel(EmbeddingModelChoice.Granite97M);
        var viewModel = fixture.ViewModel;
        Assert.Equal(SupportsQuantization, viewModel.NeedsSemanticModelValidation);
        Assert.Single(viewModel.EmbeddingModelChoices);
        Assert.Equal(EmbeddingModelChoice.Granite97M, viewModel.SelectedEmbeddingModel.Choice);
        Assert.False(viewModel.CanChangeEmbeddingModel);
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Settings.SetModel(EmbeddingModelChoice.Granite311M));
        Assert.False(fixture.Installer.NeedsTokenizationValidation(EmbeddingModelChoice.Granite311M));
        Assert.Equal(SupportsQuantization, fixture.Installer.NeedsTokenizationValidation(EmbeddingModelChoice.Granite97M));
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        fixture.WriteInstalledModel(EmbeddingModelChoice.Granite97M, currentValidation: true);
        viewModel.RefreshAssetAvailability();
        Assert.False(viewModel.NeedsSemanticModelValidation);
        Assert.False(viewModel.IsSemanticSearchSetupVisible);
        Assert.False(viewModel.CanSetUpSemanticSearch);
        Assert.Equal("Ready", viewModel.SemanticSearchStatusLabel);
        Assert.Contains(nameof(MainViewModel.NeedsSemanticModelValidation), changed);
        Assert.Contains(nameof(MainViewModel.IsSemanticSearchSetupVisible), changed);
        Assert.Contains(nameof(MainViewModel.CanSetUpSemanticSearch), changed);
        Assert.Contains(nameof(MainViewModel.SemanticSearchStatusLabel), changed);
    }

    [Fact]
    public async Task FullPrecisionOnlyInstallationDoesNotRequestOptimizedModelDownload()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.WriteInstalledModel(EmbeddingModelChoice.Granite97M, includeQuantizedModel: false);

        Assert.True(fixture.Installer.IsModelInstalled(EmbeddingModelChoice.Granite97M));
        Assert.False(fixture.Installer.NeedsTokenizationValidation(EmbeddingModelChoice.Granite97M));
        Assert.False(fixture.ViewModel.NeedsSemanticModelValidation);
        Assert.False(fixture.ViewModel.IsSemanticSearchSetupVisible);
        Assert.True(fixture.ViewModel.IsSemanticSearchReadyStatus);
        Assert.Equal("fp32", fixture.Embeddings.Policy.Precision);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task LoadingOrSwitchingKeepsRepairDisabled(bool preparing, bool changing)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.WriteInstalledModel(EmbeddingModelChoice.Granite97M);
        var viewModel = fixture.ViewModel;
        viewModel.IsPreparingEmbeddingModel = preparing;
        viewModel.IsChangingEmbeddingModel = changing;

        Assert.Equal(SupportsQuantization && fixture.Installer.IsSupported, viewModel.IsSemanticSearchSetupVisible);
        Assert.False(viewModel.CanSetUpSemanticSearch);
        Assert.False(viewModel.CanChangeEmbeddingModel);
        Assert.False(viewModel.IsSemanticSearchWarningStatus);
        Assert.False(viewModel.IsSemanticSearchReadyStatus);
        await viewModel.SetUpSemanticSearchAsync(_ => throw new InvalidOperationException("Setup must not open."));
        Assert.Equal(0, fixture.Embeddings.ReloadCount);
    }

    [Fact]
    public async Task CancelingRepairKeepsFallbackAndBlocksRepeatedDialogsAndModelChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.WriteInstalledModel(EmbeddingModelChoice.Granite97M);
        if (!fixture.Installer.IsSupported || !SupportsQuantization) return;
        var viewModel = fixture.ViewModel;
        var closeDialog = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var setup = viewModel.SetUpSemanticSearchAsync(model =>
        {
            Assert.Equal(EmbeddingModelChoice.Granite97M, model.Choice);
            return closeDialog.Task;
        });
        try
        {
            Assert.True(viewModel.IsSettingUpEmbeddingModel);
            Assert.True(viewModel.IsSemanticSearchSetupVisible);
            Assert.False(viewModel.CanSetUpSemanticSearch);
            Assert.False(viewModel.CanChangeEmbeddingModel);
            Assert.False(viewModel.IsSemanticSearchUnavailable);
            Assert.Equal("Setting up", viewModel.SemanticSearchStatusLabel);
            await viewModel.SetUpSemanticSearchAsync(_ => throw new InvalidOperationException("Duplicate setup opened."));
        }
        finally
        {
            closeDialog.TrySetResult(false);
            await setup;
        }

        Assert.False(viewModel.IsSettingUpEmbeddingModel);
        Assert.True(viewModel.CanSetUpSemanticSearch);
        Assert.False(viewModel.CanChangeEmbeddingModel);
        Assert.True(viewModel.NeedsSemanticModelValidation);
        Assert.Equal(EmbeddingModelChoice.Granite97M, fixture.Settings.Model);
        Assert.Equal(0, fixture.Embeddings.ReloadCount);
        Assert.False(fixture.Installer.HasRecordedTermsAcceptance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRepairReloadsSameModelAndClearsWarningAfterValidation(bool quantizationDisabled)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.WriteInstalledModel(EmbeddingModelChoice.Granite97M);
        if (!fixture.Installer.IsSupported || !SupportsQuantization) return;
        var viewModel = fixture.ViewModel;
        var dialogCalls = 0;
        var busyDuringReload = false;
        fixture.Embeddings.OnReload = () =>
        {
            busyDuringReload = viewModel.IsSettingUpEmbeddingModel && viewModel.IsChangingEmbeddingModel;
            Assert.False(viewModel.CanSetUpSemanticSearch);
            Assert.False(viewModel.CanChangeEmbeddingModel);
        };

        await viewModel.SetUpSemanticSearchAsync(model =>
        {
            dialogCalls++;
            Assert.Equal(EmbeddingModelChoice.Granite97M, model.Choice);
            fixture.WriteInstalledModel(model.Choice, currentValidation: true, quantizationDisabled);
            return Task.FromResult(true);
        });

        Assert.Equal(1, dialogCalls);
        Assert.Equal(1, fixture.Embeddings.ReloadCount);
        Assert.True(busyDuringReload);
        Assert.False(viewModel.IsSettingUpEmbeddingModel);
        Assert.False(viewModel.IsChangingEmbeddingModel);
        Assert.False(viewModel.NeedsSemanticModelValidation);
        Assert.False(viewModel.IsSemanticSearchSetupVisible);
        Assert.False(viewModel.IsSemanticSearchUnavailable);
        Assert.True(viewModel.IsSemanticSearchReadyStatus);
        Assert.Equal(EmbeddingModelChoice.Granite97M, viewModel.SelectedEmbeddingModel.Choice);
        Assert.Equal(EmbeddingModelChoice.Granite97M, fixture.Settings.Model);
        if (quantizationDisabled) Assert.Equal("fp32", fixture.Embeddings.Policy.Precision);
        Assert.False(fixture.Installer.HasRecordedTermsAcceptance);
    }

    [Fact]
    public async Task FailedRepairRestoresRetryWithoutChangingTheUsableModel()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.WriteInstalledModel(EmbeddingModelChoice.Granite97M);
        if (!fixture.Installer.IsSupported || !SupportsQuantization) return;
        var viewModel = fixture.ViewModel;

        await Assert.ThrowsAsync<IOException>(() => viewModel.SetUpSemanticSearchAsync(_ =>
            throw new IOException("Verification failed.")));

        Assert.False(viewModel.IsSettingUpEmbeddingModel);
        Assert.True(viewModel.CanSetUpSemanticSearch);
        Assert.False(viewModel.CanChangeEmbeddingModel);
        Assert.True(viewModel.NeedsSemanticModelValidation);
        Assert.False(viewModel.IsSemanticSearchUnavailable);
        Assert.Equal(EmbeddingModelChoice.Granite97M, fixture.Settings.Model);
        Assert.Equal(0, fixture.Embeddings.ReloadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableModelsStillOfferDownloadOrRepair(bool hasAssets)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (hasAssets) fixture.WriteInstalledModel(EmbeddingModelChoice.Granite97M, currentValidation: true);
        fixture.Embeddings.IsAvailable = false;
        var viewModel = fixture.ViewModel;

        Assert.Equal(fixture.Installer.IsSupported, viewModel.CanSetUpSemanticSearch);
        Assert.Equal(hasAssets ? "Verify and repair semantic model" : "Download semantic search model",
            viewModel.SemanticSearchSetupButtonLabel);
        Assert.False(viewModel.IsSemanticSearchReadyStatus);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly StorageTestDatabase _database;
        private readonly GlobalCpuBudget _cpuBudget;
        private readonly ApplicationUpdateService _updates;

        private Fixture(StorageTestDatabase database)
        {
            _database = database;
            Settings = new EmbeddingModelSettings(database.Paths);
            Settings.SetModel(EmbeddingModelChoice.Granite97M);
            var cpuSettings = new StorageFixedCpuSettings();
            _cpuBudget = new GlobalCpuBudget(cpuSettings);
            Installer = new GraniteModelInstaller(database.Paths, Settings, _cpuBudget);
            Embeddings = new FakeEmbeddings(database.Paths, Settings);
            _updates = new ApplicationUpdateService(NullLogger<ApplicationUpdateService>.Instance);
            ViewModel = new MainViewModel(database.Writer, database.Store, new StorageNoOcr(), Embeddings,
                Settings, cpuSettings,
                new WindowsStartupService(database.Paths, NullLogger<WindowsStartupService>.Instance),
                new ProjectOrderService(database.Paths, NullLogger<ProjectOrderService>.Instance), Installer,
                new AiConnectionsService(database.Paths, new McpServerDeploymentService(database.Paths)),
                new IndexingActivityTracker(), new UnusedProjectControl(), new EmbeddingPolicyRefreshTracker(),
                _updates, new WindowsUninstallService(database.Paths, NullLogger<WindowsUninstallService>.Instance),
                initializeWindowsStartup: false)
            {
                IsPreparingEmbeddingModel = false
            };
        }

        public EmbeddingModelSettings Settings { get; }
        public GraniteModelInstaller Installer { get; }
        public FakeEmbeddings Embeddings { get; }
        public MainViewModel ViewModel { get; }

        public static async Task<Fixture> CreateAsync() =>
            new(await StorageTestDatabase.CreateAsync(TestContext.Current.CancellationToken));

        public void WriteInstalledModel(EmbeddingModelChoice choice, bool currentValidation = false,
            bool quantizationDisabled = false, bool includeQuantizedModel = true)
        {
            var model = GraniteEmbeddingModels.Get(choice);
            var directory = Path.Combine(_database.Paths.AssetsDirectory, "granite", model.Revision);
            Directory.CreateDirectory(directory);
            foreach (var file in new[] { "tokenizer.json", "model.onnx", "installation-complete" })
                File.WriteAllText(Path.Combine(directory, file), "Fixture only; never loaded by a real model.");
            var quantized = Path.Combine(directory, "model_quint8_avx2.onnx");
            if (includeQuantizedModel) File.WriteAllText(quantized, "Fixture only; never loaded by a real model.");
            else File.Delete(quantized);
            File.WriteAllText(Path.Combine(directory, "validation.json"), JsonSerializer.Serialize(new
            {
                quantized_enabled = !quantizationDisabled,
                tokenization_version = currentValidation ? model.TokenizationVersion : null
            }));
            var disabled = Path.Combine(directory, "quantization-disabled");
            if (quantizationDisabled) File.WriteAllText(disabled, "Fixture parity failure");
            else File.Delete(disabled);
        }

        public async ValueTask DisposeAsync()
        {
            _updates.Dispose();
            Installer.Dispose();
            _cpuBudget.Dispose();
            await Embeddings.DisposeAsync();
            await _database.DisposeAsync();
        }
    }

    private sealed class FakeEmbeddings(IAppPaths paths, IEmbeddingModelSettings settings) : IEmbeddingGenerator
    {
        public bool IsAvailable { get; set; } = true;
        public string? UnavailableReason => IsAvailable ? null : "Fixture model is unavailable.";
        public int ReloadCount { get; private set; }
        public Action? OnReload { get; set; }
        public EmbeddingPolicy Policy
        {
            get
            {
                var model = GraniteEmbeddingModels.Get(settings.Model);
                var directory = Path.Combine(paths.AssetsDirectory, "granite", model.Revision);
                return model.CreatePolicy(GraniteEmbeddingProfiles.UseQuantized(directory, model));
            }
        }

        public Task ReloadAsync(CancellationToken cancellationToken = default)
        {
            ReloadCount++;
            OnReload?.Invoke();
            return Task.CompletedTask;
        }

        public int CountTokens(string text) => throw new NotSupportedException();
        public Task<EmbeddingBatch> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnusedProjectControl : IProjectIndexingControl
    {
        public void BeginPause(Guid projectId) => throw new NotSupportedException();
        public Task DrainPausedAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public void Resume(Guid projectId) => throw new NotSupportedException();
    }
}
