using System.Text.Json;
using ClawPilot.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClawPilot.Tests;

public class SkillLoaderTests : IDisposable
{
    private readonly string _skillsDir;
    private readonly SkillLoaderService _loader;

    public SkillLoaderTests()
    {
        _skillsDir = Path.Combine(Path.GetTempPath(), $"clawpilot-skills-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_skillsDir);
        _loader = new SkillLoaderService(_skillsDir, NullLogger<SkillLoaderService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_skillsDir))
            Directory.Delete(_skillsDir, true);
    }

    [Fact]
    public void LoadAll_ParsesValidSkillManifest()
    {
        var manifest = new SkillManifest
        {
            Name = "test-skill",
            Version = "1.0.0",
            Description = "A test skill",
            SystemPromptAppend = "You can also do math.",
        };
        File.WriteAllText(
            Path.Combine(_skillsDir, "test-skill.json"),
            JsonSerializer.Serialize(manifest));

        _loader.LoadAll();

        Assert.Single(_loader.LoadedSkills);
        Assert.Equal("test-skill", _loader.LoadedSkills[0].Name);
        Assert.Equal("1.0.0", _loader.LoadedSkills[0].Version);
    }

    [Fact]
    public void AppendSkillPrompts_InjectsEnabledSkillPrompts()
    {
        var manifest = new SkillManifest
        {
            Name = "math-skill",
            SystemPromptAppend = "You are also a math expert.",
            Enabled = true,
        };
        File.WriteAllText(
            Path.Combine(_skillsDir, "math-skill.json"),
            JsonSerializer.Serialize(manifest));
        _loader.LoadAll();

        var result = _loader.AppendSkillPrompts("Base prompt.");

        Assert.Contains("Base prompt.", result);
        Assert.Contains("[Skill: math-skill]", result);
        Assert.Contains("math expert", result);
    }

    [Fact]
    public void AppendSkillPrompts_SkipsDisabledSkills()
    {
        var manifest = new SkillManifest
        {
            Name = "disabled-skill",
            SystemPromptAppend = "Should not appear.",
            Enabled = false,
        };
        File.WriteAllText(
            Path.Combine(_skillsDir, "disabled-skill.json"),
            JsonSerializer.Serialize(manifest));
        _loader.LoadAll();

        var result = _loader.AppendSkillPrompts("Base prompt.");

        Assert.Equal("Base prompt.", result);
    }

    [Fact]
    public void InstallSkill_WritesManifestToDisk()
    {
        var json = JsonSerializer.Serialize(new SkillManifest
        {
            Name = "new-skill",
            Version = "2.0.0",
            Description = "Freshly installed",
        });

        var installed = _loader.InstallSkill(json);

        Assert.True(installed);
        Assert.Single(_loader.LoadedSkills);
        Assert.True(File.Exists(Path.Combine(_skillsDir, "new-skill.json")));
    }

    [Fact]
    public void UninstallSkill_RemovesFromDiskAndMemory()
    {
        var json = JsonSerializer.Serialize(new SkillManifest { Name = "removable" });
        _loader.InstallSkill(json);

        var removed = _loader.UninstallSkill("removable");

        Assert.True(removed);
        Assert.Empty(_loader.LoadedSkills);
        Assert.False(File.Exists(Path.Combine(_skillsDir, "removable.json")));
    }

    [Fact]
    public void SetSkillEnabled_TogglesState()
    {
        var json = JsonSerializer.Serialize(new SkillManifest { Name = "toggle-skill", Enabled = true });
        _loader.InstallSkill(json);

        _loader.SetSkillEnabled("toggle-skill", false);
        Assert.False(_loader.LoadedSkills[0].Enabled);

        _loader.SetSkillEnabled("toggle-skill", true);
        Assert.True(_loader.LoadedSkills[0].Enabled);
    }

    [Fact]
    public void LoadAll_SkipsInvalidJson()
    {
        File.WriteAllText(Path.Combine(_skillsDir, "bad.json"), "not valid json{{{}}}");
        File.WriteAllText(
            Path.Combine(_skillsDir, "good.json"),
            JsonSerializer.Serialize(new SkillManifest { Name = "good" }));

        _loader.LoadAll();

        Assert.Single(_loader.LoadedSkills);
        Assert.Equal("good", _loader.LoadedSkills[0].Name);
    }

    [Fact]
    public void LoadAll_CreatesDirectoryIfMissing()
    {
        var newDir = Path.Combine(Path.GetTempPath(), $"skills-missing-{Guid.NewGuid()}");
        var loader = new SkillLoaderService(newDir, NullLogger<SkillLoaderService>.Instance);

        loader.LoadAll();

        Assert.True(Directory.Exists(newDir));
        Assert.Empty(loader.LoadedSkills);

        Directory.Delete(newDir);
    }
}
