// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace LanguageModelProvider.FoundryLocal;

internal sealed record FoundryApiModel
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("owned_by")]
    public string? OwnedBy { get; init; }
}
