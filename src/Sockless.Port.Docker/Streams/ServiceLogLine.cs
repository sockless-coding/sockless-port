namespace Sockless.Port.Docker.Streams;

/// <summary>A line of a service's logs, with the task and node it came from.</summary>
/// <param name="TaskId">From the line's details; null if the engine sent none.</param>
public readonly record struct ServiceLogLine(LogLine Line, string? TaskId, string? NodeId, string? ServiceId)
{
    private const string TaskKey = "com.docker.swarm.task.id";
    private const string NodeKey = "com.docker.swarm.node.id";
    private const string ServiceKey = "com.docker.swarm.service.id";

    /// <summary>
    /// With <c>details=true</c>, each line starts with its attributes, comma-separated and
    /// URL-encoded, then a space: <c>com.docker.swarm.node.id=…,com.docker.swarm.service.id=…,com.docker.swarm.task.id=… message</c>.
    /// </summary>
    public static ServiceLogLine From(LogLine line)
    {
        var text = line.Text;
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        var head = space < 0 ? text : text[..space];
        if (!head.Contains(TaskKey + "=", StringComparison.Ordinal))
            return new ServiceLogLine(line, null, null, null);

        string? task = null, node = null, service = null;
        foreach (var pair in head.Split(','))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
                continue;
            var value = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            switch (pair[..equals])
            {
                case TaskKey:
                    task = value;
                    break;
                case NodeKey:
                    node = value;
                    break;
                case ServiceKey:
                    service = value;
                    break;
            }
        }
        return new ServiceLogLine(line with { Text = space < 0 ? "" : text[(space + 1)..] }, task, node, service);
    }
}
