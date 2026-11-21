using System.Text;
using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Docker;
using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Containers;

public sealed class LogsModel(EngineConnections connections) : ContainerPageModel(connections)
{
    public static readonly IReadOnlyList<int> TailChoices = [100, 500, 1000, 5000];

    public void OnGet()
    {
    }

    /// <summary>The whole log as a text file, with timestamps and the stream of each line.</summary>
    public async Task<IActionResult> OnGetDownloadAsync(CancellationToken cancellationToken)
    {
        if (Client is not { } client || Container is null)
            return NotFound();

        Response.ContentType = "text/plain; charset=utf-8";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{DisplayName}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.log\"";
        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024);
        try
        {
            var options = new LogOptions { Tail = null, Timestamps = true, Tty = Container.Config?.Tty ?? false };
            await foreach (var line in client.StreamLogsAsync(ContainerId, options, cancellationToken))
            {
                await writer.WriteAsync(line.Timestamp is { } time ? time.ToString("O", System.Globalization.CultureInfo.InvariantCulture) : "");
                await writer.WriteAsync(line.Stream == Docker.Streams.StreamKind.Stderr ? " stderr " : " stdout ");
                await writer.WriteLineAsync(line.Text);
            }
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            // The headers are sent; say what happened at the end of the file.
            await writer.WriteLineAsync($"[Sockless Port: the log stream failed: {EngineFailures.Describe(ex, Connection)}]");
        }
        return new EmptyResult();
    }
}
