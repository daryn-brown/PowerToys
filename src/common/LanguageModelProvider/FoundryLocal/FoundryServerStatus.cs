// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace LanguageModelProvider.FoundryLocal;

internal sealed record FoundryServerStatus
{
    [JsonPropertyName("running")]
    public bool Running { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("webUrls")]
    public List<string> WebUrls { get; init; } = [];
}
