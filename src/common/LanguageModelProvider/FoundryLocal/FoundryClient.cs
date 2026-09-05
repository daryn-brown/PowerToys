// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ManagedCommon;
using Microsoft.AI.Foundry.Local;

namespace LanguageModelProvider.FoundryLocal;

internal sealed class FoundryClient : IDisposable
{
    private const string FoundryExecutable = "foundry";
    private const string ModelsEndpoint = "/v1/models";
    private const string LoadedModelsEndpoint = "/models/loaded";
    private const string ServiceStatusEndpoint = "/status";

    private static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(5);

    private readonly FoundryLocalManager? _legacyFoundryManager;
    private readonly HttpClient? _serviceClient;
    private readonly bool _ownsServiceClient;
    private readonly List<FoundryCatalogModel> _catalogModels = [];
    private Uri? _serviceUri;

    private bool UsesCurrentApi => _serviceClient is not null;

    private FoundryClient(FoundryLocalManager foundryManager)
    {
        _legacyFoundryManager = foundryManager;
    }

    internal FoundryClient(HttpClient serviceClient, Uri serviceUri)
        : this(serviceClient, serviceUri, ownsServiceClient: false)
    {
    }

    private FoundryClient(HttpClient serviceClient, Uri serviceUri, bool ownsServiceClient)
    {
        ArgumentNullException.ThrowIfNull(serviceClient);
        ArgumentNullException.ThrowIfNull(serviceUri);

        _serviceClient = serviceClient;
        _serviceUri = NormalizeServiceUri(serviceUri);
        _ownsServiceClient = ownsServiceClient;
        ConfigureHttpClient(serviceClient);
    }

    public static async Task<FoundryClient?> CreateAsync(CancellationToken cancellationToken = default)
    {
        var client = await TryCreateClientAsync(cancellationToken).ConfigureAwait(false);
        if (client is not null)
        {
            return client;
        }

        // PowerToys may have started before Foundry Local was installed.
        Logger.LogInfo("[FoundryClient] First attempt failed, refreshing PATH and retrying");
        RefreshEnvironmentPath();

        return await TryCreateClientAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetServiceUrl(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureRunning(cancellationToken).ConfigureAwait(false);
            return GetServiceUri()?.ToString().TrimEnd('/');
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[FoundryClient] Failed to get the service URL: {ex.Message}", ex);
            return null;
        }
    }

    public Uri? GetServiceUri()
    {
        if (UsesCurrentApi)
        {
            return _serviceUri;
        }

        try
        {
            return _legacyFoundryManager?.ServiceUri;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[FoundryClient] Failed to get the legacy service URI: {ex.Message}", ex);
            return null;
        }
    }

    public async Task<List<FoundryCatalogModel>> ListCatalogModels(CancellationToken cancellationToken = default)
    {
        if (_catalogModels.Count > 0)
        {
            return _catalogModels;
        }

        try
        {
            Logger.LogInfo("[FoundryClient] Listing catalog models");

            if (UsesCurrentApi)
            {
                var models = await ListCurrentApiModelsAsync(cancellationToken).ConfigureAwait(false);
                _catalogModels.AddRange(models.Select(model => new FoundryCatalogModel
                {
                    Name = model.Id,
                    DisplayName = model.Id,
                    ProviderType = "FoundryLocal",
                    Alias = model.Id,
                    Publisher = model.OwnedBy ?? string.Empty,
                }));
            }
            else
            {
                var models = await _legacyFoundryManager!.ListCatalogModelsAsync(cancellationToken).ConfigureAwait(false);

                if (models is not null)
                {
                    foreach (var model in models)
                    {
                        _catalogModels.Add(new FoundryCatalogModel
                        {
                            Name = model.ModelId ?? string.Empty,
                            DisplayName = model.DisplayName ?? string.Empty,
                            ProviderType = model.ProviderType ?? string.Empty,
                            Uri = model.Uri ?? string.Empty,
                            Version = model.Version ?? string.Empty,
                            ModelType = model.ModelType ?? string.Empty,
                            Publisher = model.Publisher ?? string.Empty,
                            Task = model.Task ?? string.Empty,
                            FileSizeMb = model.FileSizeMb,
                            Alias = model.Alias ?? string.Empty,
                            License = model.License ?? string.Empty,
                            LicenseDescription = model.LicenseDescription ?? string.Empty,
                            ParentModelUri = model.ParentModelUri ?? string.Empty,
                            SupportsToolCalling = model.SupportsToolCalling,
                        });
                    }
                }
            }

            Logger.LogInfo($"[FoundryClient] Found {_catalogModels.Count} catalog models");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[FoundryClient] Error listing catalog models: {ex.Message}", ex);

            // Surfacing errors here prevents listing other providers; return the last known list instead.
        }

        return _catalogModels;
    }

    public async Task<List<FoundryCachedModel>> ListCachedModels(CancellationToken cancellationToken = default)
    {
        try
        {
            Logger.LogInfo("[FoundryClient] Listing cached models");

            List<FoundryCachedModel> models;
            if (UsesCurrentApi)
            {
                var apiModels = await ListCurrentApiModelsAsync(cancellationToken).ConfigureAwait(false);
                models = apiModels
                    .Select(model => new FoundryCachedModel(model.Id, model.Id))
                    .ToList();
            }
            else
            {
                var cachedModels = await _legacyFoundryManager!.ListCachedModelsAsync(cancellationToken).ConfigureAwait(false);
                var catalogModels = await ListCatalogModels(cancellationToken).ConfigureAwait(false);

                models = [];
                foreach (var model in cachedModels)
                {
                    var catalogModel = catalogModels.FirstOrDefault(item => item.Name == model.ModelId);
                    var alias = catalogModel?.Alias ?? model.Alias;
                    models.Add(new FoundryCachedModel(model.ModelId ?? string.Empty, alias));
                }
            }

            Logger.LogInfo($"[FoundryClient] Found {models.Count} cached models");
            return models;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[FoundryClient] Error listing cached models: {ex.Message}", ex);
            return [];
        }
    }

    public async Task<bool> EnsureModelLoaded(string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        Logger.LogInfo($"[FoundryClient] EnsureModelLoaded called with: {modelId}");

        if (await IsModelLoaded(modelId, cancellationToken).ConfigureAwait(false))
        {
            Logger.LogInfo($"[FoundryClient] Model already loaded: {modelId}");
            return true;
        }

        Logger.LogInfo($"[FoundryClient] Loading model: {modelId}");

        if (UsesCurrentApi)
        {
            var escapedModelId = Uri.EscapeDataString(modelId);
            using var response = await _serviceClient!
                .GetAsync(GetServiceEndpoint($"/models/load/{escapedModelId}"), cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        else
        {
            await _legacyFoundryManager!.LoadModelAsync(modelId, ct: cancellationToken).ConfigureAwait(false);
        }

        var loaded = await IsModelLoaded(modelId, cancellationToken).ConfigureAwait(false);
        Logger.LogInfo($"[FoundryClient] Model load result: {loaded}");
        return loaded;
    }

    public async Task EnsureRunning(CancellationToken cancellationToken = default)
    {
        if (UsesCurrentApi)
        {
            _serviceUri = await EnsureCurrentServiceRunningAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (!_legacyFoundryManager!.IsServiceRunning)
        {
            await _legacyFoundryManager.StartServiceAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_ownsServiceClient)
        {
            _serviceClient?.Dispose();
        }

        _legacyFoundryManager?.Dispose();
    }

    private static async Task<FoundryClient?> TryCreateClientAsync(CancellationToken cancellationToken)
    {
        var currentClient = await TryCreateCurrentClientAsync(cancellationToken).ConfigureAwait(false);
        return currentClient ?? await TryCreateLegacyClientAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FoundryClient?> TryCreateCurrentClientAsync(CancellationToken cancellationToken)
    {
        HttpClient? serviceClient = null;

        try
        {
            Logger.LogInfo("[FoundryClient] Connecting to the current Foundry Local service");
            var serviceUri = await EnsureCurrentServiceRunningAsync(cancellationToken).ConfigureAwait(false);
            serviceClient = new HttpClient
            {
                Timeout = HttpTimeout,
            };

            var client = new FoundryClient(serviceClient, serviceUri, ownsServiceClient: true);
            await client.VerifyCurrentServiceAsync(cancellationToken).ConfigureAwait(false);
            Logger.LogInfo($"[FoundryClient] Connected to the current Foundry Local service at {serviceUri}");
            return client;
        }
        catch (OperationCanceledException)
        {
            serviceClient?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            serviceClient?.Dispose();
            Logger.LogWarning($"[FoundryClient] Current Foundry Local API is unavailable: {ex.Message}");
            return null;
        }
    }

    private static async Task<FoundryClient?> TryCreateLegacyClientAsync(CancellationToken cancellationToken)
    {
        FoundryLocalManager? manager = null;

        try
        {
            Logger.LogInfo("[FoundryClient] Falling back to the legacy Foundry Local SDK");
            manager = new FoundryLocalManager();

            if (!manager.IsServiceRunning)
            {
                await manager.StartServiceAsync(cancellationToken).ConfigureAwait(false);
            }

            return new FoundryClient(manager);
        }
        catch (OperationCanceledException)
        {
            manager?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            manager?.Dispose();
            Logger.LogError($"[FoundryClient] Error creating client: {ex.Message}", ex);
            return null;
        }
    }

    private static async Task<Uri> EnsureCurrentServiceRunningAsync(CancellationToken cancellationToken)
    {
        var status = await GetCurrentServerStatusAsync(cancellationToken).ConfigureAwait(false);
        var serviceUri = GetServiceUri(status);
        if (serviceUri is not null)
        {
            return serviceUri;
        }

        await InvokeFoundryAsync(["server", "start", "--output", "json"], cancellationToken).ConfigureAwait(false);

        status = await GetCurrentServerStatusAsync(cancellationToken).ConfigureAwait(false);
        serviceUri = GetServiceUri(status);
        return serviceUri ?? throw new InvalidOperationException("Foundry Local started without reporting a local service URL.");
    }

    private static async Task<FoundryServerStatus> GetCurrentServerStatusAsync(CancellationToken cancellationToken)
    {
        var output = await InvokeFoundryAsync(["server", "status", "--output", "json"], cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(output, FoundryJsonContext.Default.FoundryServerStatus)
            ?? throw new InvalidOperationException("Foundry Local returned an empty server status response.");
    }

    private static Uri? GetServiceUri(FoundryServerStatus status)
    {
        if (!status.Running)
        {
            return null;
        }

        foreach (var url in status.WebUrls)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                uri.IsLoopback &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return NormalizeServiceUri(uri);
            }
        }

        return null;
    }

    private static Uri NormalizeServiceUri(Uri serviceUri)
    {
        if (!serviceUri.IsAbsoluteUri ||
            !serviceUri.IsLoopback ||
            (serviceUri.Scheme != Uri.UriSchemeHttp && serviceUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Foundry Local must use an absolute loopback HTTP endpoint.", nameof(serviceUri));
        }

        return new UriBuilder(serviceUri.Scheme, serviceUri.Host, serviceUri.Port).Uri;
    }

    private static void ConfigureHttpClient(HttpClient serviceClient)
    {
        if (!serviceClient.DefaultRequestHeaders.Accept.Any(header => string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)))
        {
            serviceClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        if (!serviceClient.DefaultRequestHeaders.UserAgent.Any())
        {
            var version = typeof(FoundryClient).Assembly.GetName().Version?.ToString() ?? "unknown";
            serviceClient.DefaultRequestHeaders.UserAgent.ParseAdd($"PowerToys-AdvancedPaste/{version}");
        }
    }

    private static async Task<string> InvokeFoundryAsync(IReadOnlyCollection<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FoundryExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Avoid inheriting the development host setting when Foundry Local starts its daemon.
        startInfo.Environment.Remove("DOTNET_ENVIRONMENT");

        using var process = new Process
        {
            StartInfo = startInfo,
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the Foundry Local CLI.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryTerminateProcess(process);
            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var errorDetails = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException($"Foundry Local CLI exited with code {process.ExitCode}: {errorDetails.Trim()}");
        }

        return output;
    }

    private static void TryTerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogWarning($"[FoundryClient] Unable to stop the cancelled Foundry Local command: {ex.Message}");
        }
        catch (Win32Exception ex)
        {
            Logger.LogWarning($"[FoundryClient] Unable to stop the cancelled Foundry Local command: {ex.Message}");
        }
    }

    private static void RefreshEnvironmentPath()
    {
        try
        {
            Logger.LogInfo("[FoundryClient] Refreshing PATH environment variable from system");

            var currentPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process) ?? string.Empty;
            var machinePath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? string.Empty;
            var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;

            var pathsToAdd = new List<string>();

            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                pathsToAdd.AddRange(currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
            }

            if (!string.IsNullOrWhiteSpace(userPath))
            {
                var userPaths = userPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
                foreach (var path in userPaths)
                {
                    if (!pathsToAdd.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        pathsToAdd.Add(path);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(machinePath))
            {
                var machinePaths = machinePath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
                foreach (var path in machinePaths)
                {
                    if (!pathsToAdd.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        pathsToAdd.Add(path);
                    }
                }
            }

            var newPath = string.Join(Path.PathSeparator.ToString(), pathsToAdd);

            if (currentPath != newPath)
            {
                Logger.LogInfo("[FoundryClient] Updating process PATH with latest system values");
                Environment.SetEnvironmentVariable("PATH", newPath, EnvironmentVariableTarget.Process);
            }
            else
            {
                Logger.LogInfo("[FoundryClient] PATH is already up to date");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[FoundryClient] Failed to refresh PATH: {ex.Message}", ex);
        }
    }

    private async Task VerifyCurrentServiceAsync(CancellationToken cancellationToken)
    {
        using var response = await _serviceClient!
            .GetAsync(GetServiceEndpoint(ServiceStatusEndpoint), cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task<List<FoundryApiModel>> ListCurrentApiModelsAsync(CancellationToken cancellationToken)
    {
        using var response = await _serviceClient!
            .GetAsync(GetServiceEndpoint(ModelsEndpoint), cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content
            .ReadFromJsonAsync(FoundryJsonContext.Default.FoundryModelListResponse, cancellationToken)
            .ConfigureAwait(false);

        if (payload?.Data is null)
        {
            throw new InvalidOperationException("Foundry Local returned a model list without a data collection.");
        }

        return payload.Data
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .ToList();
    }

    private async Task<bool> IsModelLoaded(string modelId, CancellationToken cancellationToken)
    {
        try
        {
            string[] loadedModelIds;
            if (UsesCurrentApi)
            {
                using var response = await _serviceClient!
                    .GetAsync(GetServiceEndpoint(LoadedModelsEndpoint), cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                loadedModelIds = await response.Content
                    .ReadFromJsonAsync(FoundryJsonContext.Default.StringArray, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Foundry Local returned an empty loaded-model response.");
            }
            else
            {
                var loadedModels = await _legacyFoundryManager!.ListLoadedModelsAsync(cancellationToken).ConfigureAwait(false);
                loadedModelIds = loadedModels
                    .Select(model => model.ModelId)
                    .Where(modelId => !string.IsNullOrWhiteSpace(modelId))
                    .Cast<string>()
                    .ToArray();
            }

            var isLoaded = loadedModelIds.Contains(modelId, StringComparer.OrdinalIgnoreCase);
            Logger.LogInfo($"[FoundryClient] IsModelLoaded({modelId}): {isLoaded}");
            Logger.LogInfo($"[FoundryClient] Loaded models: {string.Join(", ", loadedModelIds)}");
            return isLoaded;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[FoundryClient] IsModelLoaded exception: {ex.Message}", ex);
            return false;
        }
    }

    private Uri GetServiceEndpoint(string path)
    {
        var serviceUri = _serviceUri ?? throw new InvalidOperationException("Foundry Local service URL is unavailable.");
        return new Uri(serviceUri, path);
    }
}
