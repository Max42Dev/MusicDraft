using MusicDraft.Core.Covers;
using MusicDraft.Core.Hardware;
using MusicDraft.Core.Models;

namespace MusicDraft.Tests;

public class ExcerptRulesTests
{
    [Theory]
    [InlineData(10, false, 0)]
    [InlineData(20, true, 0)]
    [InlineData(30, true, 10)]
    [InlineData(60, true, 20)]
    [InlineData(180, true, 60)]
    public void Max_start_within_first_third(double duration, bool allowed, double maxStart)
    {
        Assert.Equal(allowed, ExcerptRules.ExcerptAllowed(duration));
        Assert.Equal(maxStart, ExcerptRules.MaxStart(duration), 6);
    }

    [Fact]
    public void Validation_messages()
    {
        Assert.NotNull(ExcerptRules.Validate(ReferenceMode.Excerpt, 10, 0));
        Assert.Null(ExcerptRules.Validate(ReferenceMode.WholeSong, 10, 0));
        Assert.NotNull(ExcerptRules.Validate(ReferenceMode.Excerpt, 60, 21));
        Assert.Null(ExcerptRules.Validate(ReferenceMode.Excerpt, 60, 20));
        Assert.NotNull(ExcerptRules.Validate(ReferenceMode.WholeSong, 11 * 60, 0));
        Assert.NotNull(ExcerptRules.Validate(ReferenceMode.WholeSong, 0, 0));
    }
}

public class ProfileSelectorTests
{
    private static readonly ModelRegistry Reg = ModelRegistry.LoadEmbedded();

    private static HardwareInfo Hw(double vramGb, bool nvidia = true, double ram = 32)
    {
        var g = new GpuAdapter("Test GPU", nvidia ? 0x10DEu : 0x1002u, (long)(vramGb * 1024 * 1024 * 1024), false);
        return new HardwareInfo([g], g, null, ram, ram / 2);
    }

    [Fact]
    public void Registry_is_consistent()
    {
        foreach (var p in Reg.Profiles)
        {
            Assert.NotNull(Reg.Runtime(p.Runtime));
            Assert.NotNull(Reg.File(p.GeneratorFile));
            Assert.NotNull(Reg.File(p.CoverFile));
        }
        Assert.All(Reg.Files, f => Assert.Equal(64, f.Sha256.Length));
    }

    [Fact]
    public void No_gpu_is_player_only()
    {
        var r = ProfileSelector.Recommend(Reg, new HardwareInfo([], null, null, 16, 8));
        Assert.Null(r.Profile);
    }

    [Theory]
    [InlineData(2, null)]
    [InlineData(4, "yue2-int8-4gb")]
    [InlineData(6, "yue2-int8-4gb")]
    [InlineData(8, "yue2-int8-8gb")]
    [InlineData(12, "yue2-int8-8gb")]
    [InlineData(24, "yue2-bf16-16gb")] // bf16 passed a smoke test (09-spike-results.md) and is now supported
    public void Picks_class_by_vram(double vram, string? expected)
    {
        Assert.Equal(expected, ProfileSelector.Recommend(Reg, Hw(vram)).Profile?.Id);
    }

    [Fact]
    public void Experimental_profile_carries_warning()
    {
        Assert.NotEmpty(ProfileSelector.Recommend(Reg, Hw(4)).Warnings);
    }

    [Fact]
    public void Non_nvidia_rejected()
    {
        Assert.Null(ProfileSelector.Recommend(Reg, Hw(16, nvidia: false)).Profile);
    }

    [Fact]
    public void Low_ram_rejected()
    {
        Assert.Null(ProfileSelector.Recommend(Reg, Hw(12, ram: 8)).Profile);
    }
}
