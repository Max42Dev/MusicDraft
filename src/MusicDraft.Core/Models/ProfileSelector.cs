using MusicDraft.Core.Hardware;

namespace MusicDraft.Core.Models;

public sealed record ProfileRecommendation(ModelProfile? Profile, string Reason, IReadOnlyList<string> Warnings);

public sealed record ProfileCompatibility(ModelProfile Profile, bool Fits, IReadOnlyList<string> Problems);

/// <summary>
/// Conservative OS-reported-VRAM heuristic: 4–&lt;8 GB → 4 GB class, 8–&lt;16 GB → 8 GB class, ≥16 GB → largest
/// class whose stated requirements fit; otherwise step down to the next smaller real profile. Never invents a profile.
/// </summary>
public static class ProfileSelector
{
    public static ProfileCompatibility Check(ModelProfile p, HardwareInfo hw, ModelRegistry reg)
    {
        var problems = new List<string>();
        var gpu = hw.PreferredGpu;
        var runtime = reg.Runtime(p.Runtime);
        if (gpu == null) problems.Add("No GPU was detected.");
        else
        {
            if (runtime.RequiresNvidia && !gpu.IsNvidia) problems.Add($"{gpu.Name} is not an NVIDIA GPU; the bundled runtime needs NVIDIA CUDA.");
            if (gpu.DedicatedGb + 0.05 < p.MinVramGb) problems.Add($"Needs about {p.MinVramGb:0.#} GB VRAM; this GPU reports {gpu.DedicatedGb:0.#} GB.");
        }
        if (hw.TotalRamGb > 0 && hw.TotalRamGb + 0.5 < p.MinRamGb) problems.Add($"Needs about {p.MinRamGb:0} GB system RAM; this PC has {hw.TotalRamGb:0.#} GB.");
        return new ProfileCompatibility(p, problems.Count == 0, problems);
    }

    public static ProfileRecommendation Recommend(ModelRegistry reg, HardwareInfo hw)
    {
        var gpu = hw.PreferredGpu;
        if (gpu == null)
            return new(null, "No GPU was detected, so MusicDraft will run as a player only.", []);
        var vram = gpu.DedicatedGb;
        var targetClass = vram >= 16 ? 16 : vram >= 8 ? 8 : vram >= 4 ? 4 : 0;
        if (targetClass == 0)
            return new(null, $"{gpu.Name} reports {vram:0.#} GB VRAM; no YuE2 profile is known to run below 4 GB. Player-only mode is available.", []);

        foreach (var p in reg.Profiles.Where(p => p.VramClass <= targetClass).OrderByDescending(p => p.VramClass)
                     .ThenBy(p => p.Status == ProfileStatus.Supported ? 0 : 1))
        {
            var c = Check(p, hw, reg);
            if (!c.Fits) continue;
            // Prefer a supported profile of the same class over an experimental larger one.
            if (p.Status == ProfileStatus.Experimental && p.VramClass > 4)
            {
                var supportedBelow = reg.Profiles.Where(q => q.VramClass < p.VramClass && q.Status == ProfileStatus.Supported)
                    .OrderByDescending(q => q.VramClass).FirstOrDefault(q => Check(q, hw, reg).Fits);
                if (supportedBelow != null)
                    return new(supportedBelow, $"{gpu.Name} ({vram:0.#} GB) fits the {supportedBelow.VramClass} GB class, which has passed smoke tests. " +
                                               $"The larger {p.DisplayName} is available as an experimental option in Settings.", []);
            }
            var warnings = new List<string>();
            if (p.Status == ProfileStatus.Experimental) warnings.Add($"This profile is experimental: {p.StatusNote}");
            return new(p, $"{gpu.Name} reports {vram:0.#} GB VRAM, matching the {p.VramClass} GB class.", warnings);
        }
        var anyProblems = reg.Profiles.Select(p => Check(p, hw, reg)).SelectMany(c => c.Problems).Distinct().ToList();
        return new(null, "No compatible model profile was found. " + string.Join(" ", anyProblems), []);
    }
}
