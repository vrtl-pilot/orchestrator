using System.Text;

namespace Orchestrator.Core.Processes;

/// <summary>Keeps the last N characters of a stream of lines (for error reports and test output).</summary>
public sealed class OutputTail(int maxChars)
{
    private readonly LinkedList<string> _lines = new();
    private int _chars;
    private readonly Lock _lock = new();

    public void Add(string line)
    {
        lock (_lock)
        {
            line = Ansi.Strip(line);
            _lines.AddLast(line);
            _chars += line.Length + 1;
            while (_chars > maxChars && _lines.Count > 1)
            {
                _chars -= _lines.First!.Value.Length + 1;
                _lines.RemoveFirst();
            }
        }
    }

    public override string ToString()
    {
        lock (_lock)
        {
            var sb = new StringBuilder(_chars);
            foreach (var line in _lines) sb.AppendLine(line);
            return sb.ToString().TrimEnd();
        }
    }
}
