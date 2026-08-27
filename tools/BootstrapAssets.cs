#:property TargetFramework=net10.0
#:project ../src/Core/ContextMole.Core.csproj
#:project ../src/Infrastructure/ContextMole.Infrastructure.csproj

using ContextMole.Infrastructure;

var paths = new AppPaths();
var modelSettings = new EmbeddingModelSettings(paths);
var model = GraniteEmbeddingModels.Get(modelSettings.Model);
Console.WriteLine("Context Mole semantic-search model setup");
Console.WriteLine($"Selected model: {model.DisplayName}");
if (model.RequiresGemmaTerms)
{
    Console.WriteLine("Its derived tokenizer is subject to the Gemma Terms of Use:");
    Console.WriteLine(GraniteModelInstaller.GemmaTermsUrl);
    if (!args.Contains("--accept-gemma-terms", StringComparer.Ordinal))
    {
        Console.Error.WriteLine("Review the terms, then rerun with --accept-gemma-terms. Desktop users can instead use Set up inside Context Mole.");
        return 2;
    }
}

var settings = new CpuUsageSettings(paths);
using var cpuBudget = new GlobalCpuBudget(settings);
using var installer = new GraniteModelInstaller(paths, modelSettings, cpuBudget);
if (!installer.IsSupported)
{
    Console.Error.WriteLine("Semantic search is unavailable on Intel macOS with the pinned ONNX Runtime 1.29 package.");
    return 3;
}

try
{
    var result = await installer.InstallAsync(true, new ConsoleInstallProgress());
    if (result.Validation is { } validation)
    {
        Console.WriteLine($"Granite parity: cosine={validation.MeanCorrespondingCosine:F6}; top-10 overlap={validation.MeanTop10Overlap:P1}; {validation.Decision}");
    }
    Console.WriteLine($"Semantic-search model is ready in {result.ModelDirectory}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Setup failed: {exception.Message}");
    return 1;
}

sealed class ConsoleInstallProgress : IProgress<ModelInstallProgress>
{
    public void Report(ModelInstallProgress value)
    {
        var bytes = value.TotalBytes is { } total
            ? $" ({value.BytesReceived:N0}/{total:N0} bytes)"
            : value.BytesReceived > 0 ? $" ({value.BytesReceived:N0} bytes)" : string.Empty;
        Console.WriteLine($"{value.Stage}: {value.AssetName}{bytes}");
    }
}
