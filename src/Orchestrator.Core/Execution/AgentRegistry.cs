using Microsoft.Extensions.Options;
using Orchestrator.Core.Agents;
using Orchestrator.Core.Processes;

namespace Orchestrator.Core.Execution;

public sealed record AgentInfo(string Name, bool Enabled, string Executable, string? ResolvedPath, string? Model, string? AttachUrl)
{
    public bool Available => Enabled && ResolvedPath is not null;
}

/// <summary>Maps agent names to adapters and their configured options.</summary>
public sealed class AgentRegistry
{
    private readonly Dictionary<string, IAgentAdapter> _adapters;
    private readonly OrchestratorOptions _options;

    public AgentRegistry(IEnumerable<IAgentAdapter> adapters, IOptions<OrchestratorOptions> options)
    {
        _adapters = adapters.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        _options = options.Value;
    }

    public IReadOnlyCollection<string> Names => _adapters.Keys;

    public bool TryGet(string name, out IAgentAdapter adapter, out AgentOptions options)
    {
        options = OptionsFor(name);
        if (_adapters.TryGetValue(name, out adapter!) && options.Enabled) return true;
        adapter = null!;
        return false;
    }

    public AgentOptions OptionsFor(string name) =>
        _options.Agents.TryGetValue(name, out var o) ? o : new AgentOptions();

    public IReadOnlyList<AgentInfo> Describe() =>
        _adapters.Values
            .Select(a =>
            {
                var o = OptionsFor(a.Name);
                var exe = o.Executable ?? a.DefaultExecutable;
                return new AgentInfo(a.Name, o.Enabled, exe, ExecutableResolver.Find(exe), o.Model, o.AttachUrl);
            })
            .OrderBy(i => i.Name)
            .ToList();
}
