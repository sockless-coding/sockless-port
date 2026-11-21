using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Docker.Streams;

namespace Sockless.Port.Web.Hubs;

/// <summary>
/// Interactive shells (exec with a TTY) bridged to browsers. A session belongs to the SignalR
/// connection that opened it: other connections can't use it, and it closes when that connection
/// drops.
/// </summary>
public sealed partial class TerminalSessions(IHubContext<EngineHub> hub, ILogger<TerminalSessions> logger)
{
    /// <summary>Open shells per browser connection; more is almost certainly a runaway script.</summary>
    internal const int MaxPerConnection = 8;

    private readonly ConcurrentDictionary<(string Connection, string Session), Session> _sessions = new();

    public async Task<string> OpenAsync(string connectionId, DockerClient client, string containerId, string command, string? user,
        int columns, int rows, CancellationToken cancellationToken)
    {
        if (_sessions.Keys.Count(k => k.Connection == connectionId) >= MaxPerConnection)
            throw new HubException($"Close a terminal first: at most {MaxPerConnection} can be open at once.");

        var cmd = ContainerSpecBuilder.ShellWords(command);
        if (cmd.Count == 0)
            throw new HubException("Enter a command to run, e.g. /bin/sh.");

        var execId = await client.CreateExecAsync(containerId, new ExecCreateRequest
        {
            Cmd = cmd,
            Env = ["TERM=xterm-256color", "COLORTERM=truecolor"],
            User = string.IsNullOrWhiteSpace(user) ? null : user.Trim(),
        }, cancellationToken);
        var exec = await client.StartExecAsync(execId, cancellationToken);
        await TryResizeAsync(client, execId, columns, rows);

        var sessionId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        var session = new Session(client, exec);
        _sessions[(connectionId, sessionId)] = session;
        session.Pump = Task.Run(() => PumpAsync(connectionId, sessionId, session), CancellationToken.None);
        LogOpened(logger, containerId, string.Join(' ', cmd));
        return sessionId;
    }

    public async Task InputAsync(string connectionId, string sessionId, string data)
    {
        if (!_sessions.TryGetValue((connectionId, sessionId), out var session) || data.Length == 0)
            return;
        await session.Gate.WaitAsync();
        try
        {
            await session.Exec.Stream.WriteAsync(Encoding.UTF8.GetBytes(data));
            await session.Exec.Stream.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The shell has gone; the pump reports the exit.
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public Task ResizeAsync(string connectionId, string sessionId, int columns, int rows) =>
        _sessions.TryGetValue((connectionId, sessionId), out var session)
            ? TryResizeAsync(session.Client, session.Exec.ExecId, columns, rows)
            : Task.CompletedTask;

    public async Task CloseAsync(string connectionId, string sessionId)
    {
        if (_sessions.TryRemove((connectionId, sessionId), out var session))
            await session.Exec.DisposeAsync();
    }

    public async Task CloseAllAsync(string connectionId)
    {
        foreach (var key in _sessions.Keys.Where(k => k.Connection == connectionId).ToList())
            await CloseAsync(key.Connection, key.Session);
    }

    /// <summary>Copies terminal output to the browser until the shell exits or the session closes.</summary>
    private async Task PumpAsync(string connectionId, string sessionId, Session session)
    {
        var client = hub.Clients.Client(connectionId);
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[16 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        try
        {
            int read;
            while ((read = await session.Exec.Stream.ReadAsync(bytes)) > 0)
            {
                // The decoder keeps a multi-byte character split across reads for the next one.
                var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                if (count > 0)
                    await client.SendAsync("terminalOutput", sessionId, new string(chars, 0, count));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Closed by the browser, or the connection to the engine dropped.
        }

        int? exitCode = null;
        try
        {
            exitCode = (await session.Client.InspectExecAsync(session.Exec.ExecId)).ExitCode;
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or ObjectDisposedException or TaskCanceledException)
        {
        }

        if (_sessions.TryRemove((connectionId, sessionId), out _))
            await session.Exec.DisposeAsync();
        await client.SendAsync("terminalExit", sessionId, exitCode);
    }

    private static async Task TryResizeAsync(DockerClient client, string execId, int columns, int rows)
    {
        if (columns is < 2 or > 1000 || rows is < 2 or > 1000)
            return;
        try
        {
            await client.ResizeExecAsync(execId, rows, columns);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TaskCanceledException)
        {
            // Resizing a shell that just exited fails; that's fine.
        }
    }

    private sealed class Session(DockerClient client, ExecSession exec)
    {
        public DockerClient Client { get; } = client;
        public ExecSession Exec { get; } = exec;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Task? Pump { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Terminal opened in container {ContainerId}: {Command}")]
    private static partial void LogOpened(ILogger logger, string containerId, string command);
}
