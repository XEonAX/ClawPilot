using System.Text.Json;

namespace ClawPilot.Skills;

public class SkillLoaderService
{
    private readonly string _skillsDirectory;
    private readonly ILogger<SkillLoaderService> _logger;
    private readonly List<SkillManifest> _loadedSkills = [];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public SkillLoaderService(string skillsDirectory, ILogger<SkillLoaderService> logger)
    {
        _skillsDirectory = skillsDirectory;
        _logger = logger;
    }

    public IReadOnlyList<SkillManifest> LoadedSkills => _loadedSkills;

    public void LoadAll()
    {
        _loadedSkills.Clear();

        if (!Directory.Exists(_skillsDirectory))
        {
            _logger.LogInformation("Skills directory {Dir} not found, creating...", _skillsDirectory);
            Directory.CreateDirectory(_skillsDirectory);
            return;
        }

        var files = Directory.GetFiles(_skillsDirectory, "*.json");
        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var manifest = JsonSerializer.Deserialize<SkillManifest>(json, JsonOptions);
                if (manifest is null)
                {
                    _logger.LogWarning("Failed to parse skill manifest: {File}", file);
                    continue;
                }
                _loadedSkills.Add(manifest);
                _logger.LogInformation("Loaded skill: {Name} v{Version}", manifest.Name, manifest.Version);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load skill from {File}", file);
            }
        }

        _logger.LogInformation("Loaded {Count} skills from {Dir}", _loadedSkills.Count, _skillsDirectory);
    }

    public string AppendSkillPrompts(string basePrompt)
    {
        var enabledSkills = _loadedSkills.Where(s => s.Enabled).ToList();
        if (enabledSkills.Count == 0)
            return basePrompt;

        var parts = new List<string> { basePrompt };
        foreach (var skill in enabledSkills)
        {
            if (!string.IsNullOrWhiteSpace(skill.SystemPromptAppend))
                parts.Add($"[Skill: {skill.Name}] {skill.SystemPromptAppend}");
        }

        return string.Join("\n\n", parts);
    }

    public bool InstallSkill(string json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<SkillManifest>(json, JsonOptions);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Name))
                return false;

            var fileName = $"{manifest.Name.ToLowerInvariant().Replace(' ', '-')}.json";
            var filePath = Path.Combine(_skillsDirectory, fileName);

            if (!Directory.Exists(_skillsDirectory))
                Directory.CreateDirectory(_skillsDirectory);

            File.WriteAllText(filePath, json);
            _loadedSkills.Add(manifest);
            _logger.LogInformation("Installed skill: {Name}", manifest.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to install skill");
            return false;
        }
    }

    public bool UninstallSkill(string name)
    {
        var skill = _loadedSkills.FirstOrDefault(s =>
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
            return false;

        var fileName = $"{skill.Name.ToLowerInvariant().Replace(' ', '-')}.json";
        var filePath = Path.Combine(_skillsDirectory, fileName);

        if (File.Exists(filePath))
            File.Delete(filePath);

        _loadedSkills.Remove(skill);
        _logger.LogInformation("Uninstalled skill: {Name}", name);
        return true;
    }

    public IReadOnlyList<SkillManifest> ListSkills() => _loadedSkills;

    public bool SetSkillEnabled(string name, bool enabled)
    {
        var skill = _loadedSkills.FirstOrDefault(s =>
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
            return false;

        skill.Enabled = enabled;
        _logger.LogInformation("Skill {Name} {State}", name, enabled ? "enabled" : "disabled");
        return true;
    }
}
