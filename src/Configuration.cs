using Dalamud.Configuration;
namespace DSRMeteorPilot;
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public bool Enabled { get; set; }
    public bool AutoAntiKnockback { get; set; } = true;
    public bool RecordTelemetry { get; set; } = true;
}
