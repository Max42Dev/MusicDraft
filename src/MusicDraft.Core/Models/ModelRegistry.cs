using System.Text.Json;
using System.Text.Json.Serialization;

namespace MusicDraft.Core.Models;

public sealed record RuntimeEntry(
    string Id, string DisplayName, string Url, string FileName, long SizeBytes, string Sha256, long ExtractedBytes,
    string InstallDir, string ArchiveRoot, string PythonRelativePath, string MainRelativePath,
    string License, string LicenseUrl, string SourceUrl, bool RequiresNvidia)
{
    /// <summary>Expected <c>__version__</c> in ComfyUI/comfyui_version.py.</summary>
    public string Version { get; init; } = "";
    /// <summary>Pinned 7-Zip console extractor used to unpack the .7z archive (Windows tar lacks LZMA).</summary>
    public ToolEntry? Extractor { get; init; }
}

public sealed record ToolEntry(string DisplayName, string Url, string FileName, long SizeBytes, string Sha256, string License, string LicenseUrl);

public sealed record ModelFile(string Id, string DisplayName, string Repo, string Revision, string Path, long SizeBytes, string Sha256)
{
    /// <summary>Hugging Face resolve URL, or a direct URL when <see cref="Repo"/> is a GitHub repository.</summary>
    public string Url => Repo.StartsWith("ggml-org/llama.cpp", StringComparison.OrdinalIgnoreCase)
        ? $"https://github.com/{Repo}/releases/download/{Revision}/{Path}"
        : $"https://huggingface.co/{Repo}/resolve/{Revision}/{Path}";
    public string SourcePage => Repo.StartsWith("ggml-org/llama.cpp", StringComparison.OrdinalIgnoreCase)
        ? $"https://github.com/{Repo}/releases/tag/{Revision}"
        : $"https://huggingface.co/{Repo}/tree/{Revision}";
    /// <summary>ComfyUI model folder (e.g. "checkpoints") and file name.</summary>
    public string Folder => Path.Split('/')[0];
    public string FileName => Path.Split('/')[^1];
}

/// <summary>The always-present local text model used to draft lyrics (run in-process via LLamaSharp).</summary>
public sealed record TextAssistantEntry(
    string ModelFile, string DisplayName, string License, string LicenseUrl, string SourceUrl);

[JsonConverter(typeof(JsonStringEnumConverter<ProfileStatus>))]
public enum ProfileStatus { Supported, Experimental }

public sealed record ModelProfile(
    string Id, string DisplayName, int VramClass, double MinVramGb, double MinRamGb, ProfileStatus Status, string StatusNote,
    string Runtime, string GeneratorFile, string CoverFile, string[] WorkerFlags, bool ForceTiledDecode,
    string License, string LicenseUrl);

public sealed class ModelRegistry
{
    public int SchemaVersion { get; init; }
    public List<RuntimeEntry> Runtimes { get; init; } = [];
    public List<ModelFile> Files { get; init; } = [];
    public List<ModelProfile> Profiles { get; init; } = [];
    public TextAssistantEntry? TextAssistant { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static ModelRegistry LoadEmbedded()
    {
        using var s = typeof(ModelRegistry).Assembly.GetManifestResourceStream("MusicDraft.Core.Models.registry.json")
                      ?? throw new InvalidOperationException("Model registry resource missing.");
        return JsonSerializer.Deserialize<ModelRegistry>(s, Options)!;
    }

    public static ModelRegistry Parse(string json) => JsonSerializer.Deserialize<ModelRegistry>(json, Options)!;

    public RuntimeEntry Runtime(string id) => Runtimes.Single(r => r.Id == id);
    public ModelFile File(string id) => Files.Single(f => f.Id == id);
    public ModelProfile? Profile(string? id) => Profiles.FirstOrDefault(p => p.Id == id);

    /// <summary>Bytes to download for a profile, optionally including the cover encoder and runtime.</summary>
    public long DownloadBytes(ModelProfile p, bool includeCover, bool includeRuntime)
    {
        var total = File(p.GeneratorFile).SizeBytes;
        if (includeCover) total += File(p.CoverFile).SizeBytes;
        if (includeRuntime) total += Runtime(p.Runtime).SizeBytes;
        return total;
    }
}
