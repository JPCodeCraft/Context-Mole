using System.Text.Json;

using ContextMole.Broker.Protocol;
using ContextMole.Core;
using ContextMole.Infrastructure;

namespace ContextMole.Tests;

public sealed class BrokerTokenizationProtocolTests
{
    [Fact]
    public void LegacyPolicyDeserializationLosesTheCorrectedTokenizationIdentity()
    {
        var corrected = GraniteEmbeddingModels.Get(EmbeddingModelChoice.Granite97M).CreatePolicy(false);
        var json = JsonSerializer.Serialize(corrected, BrokerJson.Options);
        var legacy = JsonSerializer.Deserialize<LegacyEmbeddingPolicy>(json, BrokerJson.Options)!;

        Assert.Equal(corrected.ModelId, legacy.ModelId);
        Assert.Equal(corrected.PreparationVersion, legacy.PreparationVersion);
        Assert.Equal((corrected with { TokenizationVersion = null }).Key, legacy.Key);
        Assert.NotEqual(corrected.Key, legacy.Key);
        Assert.DoesNotContain("tokenization_version", JsonSerializer.Serialize(legacy, BrokerJson.Options));
        Assert.Equal(corrected, JsonSerializer.Deserialize<EmbeddingPolicy>(json, BrokerJson.Options));
    }

    [Fact]
    public void TokenizationProtocolUsesAnEndpointSeparateFromLegacyV3()
    {
        var endpoint = CreateEndpoint();

        Assert.Equal(4, BrokerProtocol.MajorVersion);
        Assert.Equal($"context-mole-v4-{endpoint.DataDirectoryId}", endpoint.PipeName);
        Assert.Equal("start-v4.lock", Path.GetFileName(endpoint.StartupLockPath));
        Assert.Equal("instance-v4.lock", Path.GetFileName(endpoint.InstanceLockPath));
        Assert.Equal("instance-v4.json", Path.GetFileName(endpoint.InstanceMetadataPath));
    }

    [Theory]
    [InlineData("1.0", "old-client")]
    [InlineData("2.0", "current-build")]
    [InlineData("3.0", "new-client")]
    public async Task ServerRejectsLegacyMajorBeforeAnyDeploymentOrApplicationDispatch(
        string clientVersion, string deploymentId)
    {
        var endpoint = CreateEndpoint();
        const string token = "test-authentication-token";
        await using var server = new BrokerPipeServer(endpoint, token, "2.0", "current-build",
            DateTimeOffset.UnixEpoch, (_, _) => throw new InvalidOperationException("Must not dispatch"));

        var response = server.ValidateHandshake(new BrokerHandshakeRequest(3, 0, token,
            endpoint.DataDirectoryId, clientVersion, deploymentId));

        Assert.False(response.Accepted);
        Assert.Equal(4, response.ProtocolMajor);
        Assert.Equal("protocol_mismatch", response.Error?.Code);
        Assert.False(response.Error?.Retryable);
    }

    [Theory]
    [InlineData("1.0", "old-broker")]
    [InlineData("2.0", "current-build")]
    [InlineData("3.0", "new-broker")]
    public void ClientRejectsAcceptedLegacyMajorBeforeDeploymentArbitration(
        string brokerVersion, string deploymentId)
    {
        var response = new BrokerHandshakeResponse(true, 3, 0, brokerVersion, deploymentId, ["embeddings"]);

        var error = Assert.Throws<BrokerRpcException>(() => BrokerRpcClient.ValidateHandshakeResponse(response));

        Assert.Equal("protocol_mismatch", error.Code);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task CurrentHandshakePreservesAuthenticationChecksAndAcceptsMatchingProtocol()
    {
        var endpoint = CreateEndpoint();
        const string token = "test-authentication-token";
        await using var server = new BrokerPipeServer(endpoint, token, "2.0", "current-build",
            DateTimeOffset.UnixEpoch, (_, _) => throw new InvalidOperationException("No socket is opened"));
        var request = new BrokerHandshakeRequest(BrokerProtocol.MajorVersion, BrokerProtocol.MinorVersion,
            token, endpoint.DataDirectoryId, "2.0", "current-build");

        var response = server.ValidateHandshake(request);
        Assert.True(response.Accepted);
        BrokerRpcClient.ValidateHandshakeResponse(response);
        var rejected = server.ValidateHandshake(request with { AuthenticationToken = "wrong-token" });
        Assert.False(rejected.Accepted);
        Assert.Equal("authentication_failed", rejected.Error?.Code);
        var error = Assert.Throws<BrokerRpcException>(() => BrokerRpcClient.ValidateHandshakeResponse(rejected));
        Assert.Equal("authentication_failed", error.Code);
    }

    private static BrokerEndpoint CreateEndpoint() =>
        new(Path.Combine(Path.GetTempPath(), $"context-mole-protocol-test-{Guid.NewGuid():N}"));

    // The v3 shape intentionally has no tokenization field. Its computed Key is
    // serialized but cannot be restored from a newer peer's read-only Key property.
    private sealed record LegacyEmbeddingPolicy(
        string ModelId,
        string Revision,
        string ModelSha256,
        string TokenizerSha256,
        string Precision,
        int SourceDimensions,
        int Dimensions,
        string Pooling,
        string Normalization,
        string PreparationVersion)
    {
        public string Key => string.Join(':', ModelId, Revision, ModelSha256, TokenizerSha256, Precision,
            SourceDimensions, Dimensions, Pooling, Normalization, PreparationVersion);
    }
}
