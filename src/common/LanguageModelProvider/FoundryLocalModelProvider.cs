// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.ClientModel;
using LanguageModelProvider.FoundryLocal;
using ManagedCommon;
using Microsoft.Extensions.AI;
using OpenAI;

namespace LanguageModelProvider;

public sealed class FoundryLocalModelProvider : ILanguageModelProvider
{
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private FoundryClient? _foundryClient;
    private IEnumerable<FoundryCatalogModel>? _catalogModels;
    private string? _serviceUrl;

    public static FoundryLocalModelProvider Instance { get; } = new();

    public string Name => "FoundryLocal";

    public string ProviderDescription => "The model will run locally via Foundry Local";

    public IChatClient? GetIChatClient(string modelId)
    {
        return GetIChatClientAsync(modelId).GetAwaiter().GetResult();
    }

    public async Task<IChatClient?> GetIChatClientAsync(string modelId, CancellationToken cancellationToken = default)
    {
        Logger.LogInfo($"[FoundryLocal] GetIChatClient called with model ID: {modelId}");

        if (string.IsNullOrWhiteSpace(modelId))
        {
            Logger.LogError("[FoundryLocal] Model ID is empty");
            return null;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        if (!await EnsureModelInCatalogAsync(modelId, cancellationToken).ConfigureAwait(false))
        {
            var errorMessage = $"{modelId} is not supported in Foundry Local. Please configure supported models in Settings.";
            Logger.LogError($"[FoundryLocal] {errorMessage}");
            throw new InvalidOperationException(errorMessage);
        }

        if (!await EnsureModelLoadedWithRefreshAsync(modelId, cancellationToken).ConfigureAwait(false))
        {
            Logger.LogError($"[FoundryLocal] Failed to load model: {modelId}");
            throw new InvalidOperationException($"Failed to load the model '{modelId}'.");
        }

        var client = _foundryClient;
        if (client is null)
        {
            const string message = "Foundry Local client could not be created. Please make sure Foundry Local is installed and running.";
            Logger.LogError($"[FoundryLocal] {message}");
            throw new InvalidOperationException(message);
        }

        var baseUri = client.GetServiceUri();
        if (baseUri is null && await TryRefreshClientAsync("Service URI was not available", cancellationToken).ConfigureAwait(false))
        {
            baseUri = _foundryClient?.GetServiceUri();
        }

        if (baseUri is null)
        {
            const string message = "Foundry Local service URL is not available. Please make sure Foundry Local is installed and running.";
            Logger.LogError($"[FoundryLocal] {message}");
            throw new InvalidOperationException(message);
        }

        var endpointUri = new Uri($"{baseUri.ToString().TrimEnd('/')}/v1");
        Logger.LogInfo($"[FoundryLocal] Creating OpenAI client with endpoint: {endpointUri}");

        return new OpenAIClient(
            new ApiKeyCredential("none"),
            new OpenAIClientOptions { Endpoint = endpointUri, NetworkTimeout = TimeSpan.FromMinutes(5) })
            .GetChatClient(modelId)
            .AsIChatClient();
    }

    public string GetIChatClientString(string url)
    {
        try
        {
            InitializeAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.LogError($"[FoundryLocal] Failed to initialize the chat client string: {ex.Message}", ex);
            return string.Empty;
        }

        var modelId = url.Split('/').LastOrDefault();

        if (string.IsNullOrWhiteSpace(_serviceUrl) || string.IsNullOrWhiteSpace(modelId))
        {
            return string.Empty;
        }

        var endpoint = $"{_serviceUrl.TrimEnd('/')}/v1";
        return $"new OpenAIClient(new ApiKeyCredential(\"none\"), new OpenAIClientOptions{{ Endpoint = new Uri(\"{endpoint}\") }}).GetChatClient(\"{modelId}\").AsIChatClient()";
    }

    public async Task<IEnumerable<ModelDetails>> GetModelsAsync(CancellationToken cancelationToken = default)
    {
        await InitializeAsync(cancelationToken).ConfigureAwait(false);

        var client = _foundryClient;
        if (client is null)
        {
            return Array.Empty<ModelDetails>();
        }

        var cachedModels = await client.ListCachedModels(cancelationToken).ConfigureAwait(false);
        List<ModelDetails> downloadedModels = [];

        foreach (var model in cachedModels)
        {
            Logger.LogInfo($"[FoundryLocal] Adding cached model: {model.Name}");
            downloadedModels.Add(new ModelDetails
            {
                Id = $"fl-{model.Name}",
                Name = model.Name,
                Url = $"fl://{model.Name}",
                Description = $"{model.Name} running locally with Foundry Local",
                HardwareAccelerators = [HardwareAccelerator.FOUNDRYLOCAL],
                ProviderModelDetails = model,
            });
        }

        return downloadedModels;
    }

    public async Task<bool> IsAvailable(CancellationToken cancellationToken = default)
    {
        Logger.LogInfo("[FoundryLocal] Checking availability");

        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            var available = _foundryClient is not null;
            Logger.LogInfo($"[FoundryLocal] Available: {available}");
            return available;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[FoundryLocal] Availability check failed: {ex.Message}", ex);
            return false;
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_foundryClient is not null && _catalogModels?.Any() == true)
            {
                await _foundryClient.EnsureRunning(cancellationToken).ConfigureAwait(false);
                _serviceUrl = await _foundryClient.GetServiceUrl(cancellationToken).ConfigureAwait(false);
                return;
            }

            Logger.LogInfo("[FoundryLocal] Initializing provider");
            _foundryClient ??= await FoundryClient.CreateAsync(cancellationToken).ConfigureAwait(false);

            if (_foundryClient is null)
            {
                const string message = "Foundry Local client could not be created. Please make sure Foundry Local is installed and running.";
                Logger.LogError($"[FoundryLocal] {message}");
                throw new InvalidOperationException(message);
            }

            _serviceUrl = await _foundryClient.GetServiceUrl(cancellationToken).ConfigureAwait(false);
            Logger.LogInfo($"[FoundryLocal] Service URL: {_serviceUrl}");

            var catalogModels = await _foundryClient.ListCatalogModels(cancellationToken).ConfigureAwait(false);
            Logger.LogInfo($"[FoundryLocal] Found {catalogModels.Count} catalog models");
            _catalogModels = catalogModels;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task<bool> EnsureModelInCatalogAsync(string modelId, CancellationToken cancellationToken)
    {
        var isInCatalog = _catalogModels?.Any(model => model.Name == modelId) ?? false;
        if (isInCatalog)
        {
            return true;
        }

        Logger.LogWarning($"[FoundryLocal] Model not found in catalog. Refreshing client for model: {modelId}");
        if (!await TryRefreshClientAsync("Model not in catalog", cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return _catalogModels?.Any(model => model.Name == modelId) ?? false;
    }

    private async Task<bool> EnsureModelLoadedWithRefreshAsync(string modelId, CancellationToken cancellationToken)
    {
        try
        {
            if (await _foundryClient!.EnsureModelLoaded(modelId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[FoundryLocal] EnsureModelLoaded failed: {ex.Message}");
        }

        if (!await TryRefreshClientAsync("EnsureModelLoaded failed", cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            return await _foundryClient!.EnsureModelLoaded(modelId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[FoundryLocal] EnsureModelLoaded failed after refresh: {ex.Message}", ex);
            return false;
        }
    }

    private async Task<bool> TryRefreshClientAsync(string reason, CancellationToken cancellationToken)
    {
        Logger.LogInfo($"[FoundryLocal] Refreshing Foundry Local client: {reason}");

        try
        {
            _foundryClient?.Dispose();
            _foundryClient = null;
            _catalogModels = null;
            _serviceUrl = null;

            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            return _foundryClient is not null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[FoundryLocal] Failed to refresh Foundry Local client: {ex.Message}", ex);
            return false;
        }
    }
}
