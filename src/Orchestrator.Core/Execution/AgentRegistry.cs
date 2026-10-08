using Microsoft.Extensions.Options;
using Orchestrator.Core.Agents;
using Orchestrator.Core.Processes;

namespace Orchestrator.Core.Execution;

public sealed record AgentInfo(
    string Name,
    string DisplayName,
    bool BuiltIn,
    bool Enabled,
    string Executable,
    string? ResolvedPath,
    string? Model,
    string? InstallHint,
    string? Protocol,
    string? AttachUrl)
{
    public bool Available => Enabled && ResolvedPath is not null;
}

/// <summary>
/// The platforms the orchestrator can run: built-in adapters (Claude Code, Codex, OpenCode, Qoder) plus any custom
/// platform defined in configuration. Options are read on every call, so edits to config.json apply without a restart.
/// </summary>
public sealed class AgentRegistry
{
    private readonly Dictionary<string, IAgentAdapter> _builtIn;
    private readonly Func<OrchestratorOptions> _options;

    public AgentRegistry(IEnumerable<IAgentAdapter> adapters, Func<OrchestratorOptions> options)
    {
        _builtIn = adapters.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        _options = options;
    }

    public AgentRegistry(IEnumerable<IAgentAdapter> adapters, IOptions<OrchestratorOptions> options)
        : this(adapters, () => options.Value)
    {
    }

    /// <summary>Current options (re-read on every call, so Setup page edits apply to the next task turn).</summary>
    public OrchestratorOptions CurrentOptions => _options();

    public IReadOnlyCollection<string> Names => All().Select(a => a.Name).ToList();

    public bool IsBuiltIn(string name) => _builtIn.ContainsKey(name);

    public bool TryGet(string name, out IAgentAdapter adapter, out AgentOptions options)
    {
        adapter = null!;
        options = null!;
        if (Find(name) is not { } found) return false;
        options = OptionsFor(found);
        if (!options.Enabled) return false;
        adapter = found;
        return true;
    }

    /// <summary>Effective options: configuration, with the adapter's defaults where configuration is silent.</summary>
    public AgentOptions OptionsFor(string name) =>
        Find(name) is { } adapter ? OptionsFor(adapter) : new AgentOptions();

    public IReadOnlyList<AgentInfo> Describe() =>
        All()
            .Select(a =>
            {
                var o = OptionsFor(a);
                var exe = o.Executable ?? a.DefaultExecutable;
                return new AgentInfo(
                    a.Name, a.DisplayName, IsBuiltIn(a.Name), o.Enabled, exe, ExecutableResolver.Find(exe), o.Model,
                    a.InstallHint, (a as ConfiguredAgentAdapter)?.Protocol, o.AttachUrl);
            })
            .OrderBy(i => i.BuiltIn ? 0 : 1)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private IAgentAdapter? Find(string name)
    {
        if (_builtIn.TryGetValue(name, out var builtIn)) return builtIn;
        return _options().Agents.TryGetValue(name, out var custom) && custom.Protocol is not null
            ? new ConfiguredAgentAdapter(name.ToLowerInvariant(), custom)
            : null;
    }

    private IEnumerable<IAgentAdapter> All()
    {
        foreach (var adapter in _builtIn.Values) yield return adapter;
        foreach (var (name, o) in _options().Agents)
        {
            if (!_builtIn.ContainsKey(name) && o.Protocol is not null) yield return new ConfiguredAgentAdapter(name.ToLowerInvariant(), o);
        }
    }

    private AgentOptions OptionsFor(IAgentAdapter adapter)
    {
        var configured = _options().Agents.TryGetValue(adapter.Name, out var o) ? o : new AgentOptions();
        var environment = new Dictionary<string, string>(adapter.DefaultEnvironment);
        foreach (var (key, value) in configured.Environment) environment[key] = value;

        return new AgentOptions
        {
            Enabled = configured.Enabled,
            Executable = configured.Executable,
            Model = configured.Model,
            ExtraArgs = configured.ExtraArgs ?? [.. adapter.DefaultExtraArgs],
            Environment = environment,
            MaxConcurrent = configured.MaxConcurrent,
            AttachUrl = configured.AttachUrl,
            DisplayName = configured.DisplayName,
            Protocol = configured.Protocol,
            Args = configured.Args,
            ModelArgs = configured.ModelArgs,
            ResumeArgs = configured.ResumeArgs,
            PromptVia = configured.PromptVia,
            VersionArgs = configured.VersionArgs,
            InstallHint = configured.InstallHint,
            Integration = configured.Integration,
        };
    }
}
