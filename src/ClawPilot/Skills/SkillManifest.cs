namespace ClawPilot.Skills;

public sealed class SkillManifest
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = string.Empty;
    public string SystemPromptAppend { get; set; } = string.Empty;
    public Dictionary<string, McpServerConfig> McpServers { get; set; } = new();
    public List<string> Plugins { get; set; } = [];
    public bool Enabled { get; set; } = true;
}

public sealed class McpServerConfig
{
    public string Command { get; set; } = string.Empty;
    public List<string> Args { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = new();
}
