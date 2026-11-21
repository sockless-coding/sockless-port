using System.Text.Json.Serialization;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Docker;

/// <summary>
/// Source-generated serialization (AOT-safe). The engine uses PascalCase with some
/// all-caps names (<c>ID</c>, <c>NCPU</c>) and lower-case event fields, so matching is
/// case-insensitive.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(VersionResponse))]
[JsonSerializable(typeof(SystemInfo))]
[JsonSerializable(typeof(DiskUsage))]
[JsonSerializable(typeof(List<ContainerSummary>))]
[JsonSerializable(typeof(List<ImageSummary>))]
[JsonSerializable(typeof(VolumeListResponse))]
[JsonSerializable(typeof(VolumeSummary))]
[JsonSerializable(typeof(VolumeCreateRequest))]
[JsonSerializable(typeof(List<NetworkSummary>))]
[JsonSerializable(typeof(DockerEvent))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(ContainerInspect))]
[JsonSerializable(typeof(ContainerCreateResponse))]
[JsonSerializable(typeof(ContainerStats))]
[JsonSerializable(typeof(ExecCreateRequest))]
[JsonSerializable(typeof(ExecStartRequest))]
[JsonSerializable(typeof(ExecInspect))]
[JsonSerializable(typeof(IdResponse))]
[JsonSerializable(typeof(PullProgress))]
[JsonSerializable(typeof(ImageInspect))]
[JsonSerializable(typeof(List<ImageHistoryItem>))]
[JsonSerializable(typeof(List<ImageDeleteItem>))]
[JsonSerializable(typeof(ImagePruneResponse))]
[JsonSerializable(typeof(DistributionInspect))]
[JsonSerializable(typeof(BuildProgress))]
[JsonSerializable(typeof(AuthConfig))]
[JsonSerializable(typeof(AuthResponse))]
[JsonSerializable(typeof(Dictionary<string, AuthConfig>))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
internal sealed partial class DockerJsonContext : JsonSerializerContext;
