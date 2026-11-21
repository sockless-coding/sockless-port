using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Images;

public sealed class HistoryModel(EngineConnections connections, ImageUpdates updates) : ImagePageModel(connections, updates)
{
    public IReadOnlyList<ImageHistoryItem> Layers { get; private set; } = [];

    public string? HistoryError { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (Client is not { } client || Image is null)
            return;
        try
        {
            Layers = await client.GetImageHistoryAsync(Image.Id, cancellationToken);
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            HistoryError = EngineFailures.Describe(ex, Connection);
        }
    }

    /// <summary>Classic builder lines read <c>/bin/sh -c #(nop)  CMD ["sh"]</c>; show the instruction, as the Dockerfile had it.</summary>
    public static string Instruction(string? createdBy)
    {
        var text = (createdBy ?? "").Trim();
        const string nop = "/bin/sh -c #(nop) ";
        if (text.StartsWith(nop, StringComparison.Ordinal))
            return text[nop.Length..].Trim();
        const string shell = "/bin/sh -c ";
        return text.StartsWith(shell, StringComparison.Ordinal) ? "RUN " + text[shell.Length..] : text;
    }
}
