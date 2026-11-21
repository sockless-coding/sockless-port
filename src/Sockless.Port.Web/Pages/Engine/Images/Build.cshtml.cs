using System.Formats.Tar;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Sockless.Port.Docker;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Images;

public sealed class BuildInput
{
    /// <summary><c>dockerfile</c> (typed in) or <c>archive</c> (an uploaded build context).</summary>
    public string Source { get; set; } = "dockerfile";

    public string? Dockerfile { get; set; }

    public IFormFile? Archive { get; set; }

    /// <summary>The Dockerfile's path inside an uploaded archive.</summary>
    public string DockerfilePath { get; set; } = "Dockerfile";

    /// <summary>One or more <c>name:tag</c>, separated by spaces, commas or lines.</summary>
    public string? Tags { get; set; }

    public bool Pull { get; set; }

    public bool NoCache { get; set; }
}

[RequestSizeLimit(BuildModel.MaxContextBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = BuildModel.MaxContextBytes)]
public sealed class BuildModel(EngineConnections connections, ImageJobs jobs, IOptions<PortOptions> options) : EndpointPageModel(connections)
{
    /// <summary>Largest build context accepted: 2 GB.</summary>
    public const long MaxContextBytes = 2L * 1024 * 1024 * 1024;

    [BindProperty]
    public BuildInput Input { get; set; } = new();

    /// <summary>The build being shown, from <c>?job=</c>.</summary>
    public ImageJobView? Job { get; private set; }

    public string? Error { get; private set; }

    public IActionResult OnGet(string? job)
    {
        if (!string.IsNullOrEmpty(job))
        {
            if (jobs.Get(job) is not { Kind: ImageJobKind.Build } found || found.EndpointId != EndpointId)
            {
                Flash.Error(TempData, "That build is no longer known. Finished builds are kept for 15 minutes.");
                return RedirectToPage(new { endpointId = EndpointId });
            }
            Job = found.View();
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (Client is null)
            return Page();

        var tags = (Input.Tags ?? "").Split([' ', ',', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        foreach (var tag in tags)
        {
            if (ImageReference.Parse(tag) is not { Digest: null })
                ModelState.AddModelError("Input.Tags", $"'{tag}' isn't a valid name. Use name:tag, such as app:1.2.");
        }
        var dockerfilePath = Input.Source == "archive" ? (Input.DockerfilePath ?? "").Trim() : "Dockerfile";
        if (dockerfilePath.Length == 0 || dockerfilePath.StartsWith('/') || dockerfilePath.Contains("..", StringComparison.Ordinal))
            ModelState.AddModelError("Input.DockerfilePath", "Give the Dockerfile's path inside the archive, such as Dockerfile or docker/Dockerfile.");

        if (Input.Source == "archive")
        {
            if (Input.Archive is not { Length: > 0 })
                ModelState.AddModelError("Input.Archive", "Choose a .tar or .tar.gz file holding the build context.");
        }
        else if (string.IsNullOrWhiteSpace(Input.Dockerfile))
        {
            ModelState.AddModelError("Input.Dockerfile", "Write the Dockerfile, starting with a FROM line.");
        }
        if (!ModelState.IsValid)
            return Page();

        var path = Path.Combine(options.Value.TempPath, $"build-{Guid.NewGuid():n}.tar");
        try
        {
            if (Input.Source == "archive")
            {
                await using (var file = System.IO.File.Create(path))
                    await Input.Archive!.CopyToAsync(file, cancellationToken);
                if (!await LooksLikeArchiveAsync(path, cancellationToken))
                {
                    System.IO.File.Delete(path);
                    ModelState.AddModelError("Input.Archive", "That isn't a tar archive. Upload a .tar, or a .tar.gz compressed with gzip.");
                    return Page();
                }
            }
            else
            {
                await WriteDockerfileContextAsync(path, Input.Dockerfile!, cancellationToken);
            }

            var job = jobs.StartBuild(Connection, path, new ImageBuildOptions
            {
                Tags = tags,
                Dockerfile = dockerfilePath,
                Pull = Input.Pull,
                NoCache = Input.NoCache,
            });
            return RedirectToPage(new { endpointId = EndpointId, job = job.Id });
        }
        catch (ImageJobRejectedException ex)
        {
            Error = ex.Message;
            return Page();
        }
        catch (IOException ex)
        {
            System.IO.File.Delete(path);
            Error = $"The build context couldn't be stored: {ex.Message}";
            return Page();
        }
    }

    /// <summary>A build context holding just the Dockerfile.</summary>
    internal static async Task WriteDockerfileContextAsync(string path, string dockerfile, CancellationToken cancellationToken)
    {
        // Browsers send CRLF from textareas; a Dockerfile's continuation lines need LF.
        var bytes = Encoding.UTF8.GetBytes(dockerfile.Replace("\r\n", "\n", StringComparison.Ordinal));
        await using var file = System.IO.File.Create(path);
        await using var tar = new TarWriter(file, TarEntryFormat.Ustar, leaveOpen: false);
        var entry = new UstarTarEntry(TarEntryType.RegularFile, "Dockerfile")
        {
            DataStream = new MemoryStream(bytes),
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            ModificationTime = DateTimeOffset.UtcNow,
        };
        await tar.WriteEntryAsync(entry, cancellationToken);
    }

    /// <summary>gzip (1f 8b) or a tar header (<c>ustar</c> at offset 257).</summary>
    internal static async Task<bool> LooksLikeArchiveAsync(string path, CancellationToken cancellationToken)
    {
        var header = new byte[263];
        await using var file = System.IO.File.OpenRead(path);
        var read = await file.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        if (read >= 2 && header[0] == 0x1f && header[1] == 0x8b)
            return true;
        return read >= 262 && Encoding.ASCII.GetString(header, 257, 5) == "ustar";
    }
}
