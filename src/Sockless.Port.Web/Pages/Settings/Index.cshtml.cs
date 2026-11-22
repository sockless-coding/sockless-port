using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Sockless.Port.Agents;
using Sockless.Port.Backup;
using Sockless.Port.Https;
using Sockless.Port.Settings;
using Sockless.Port.Vulnerabilities;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Settings;

[RequestSizeLimit(MaxBackupBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxBackupBytes)]
public sealed class IndexModel(PortSettings settings, HttpsCertificates https, InternalCa ca, DataBackup backup,
    IOptions<PortOptions> options, IHostApplicationLifetime lifetime, TrivyCli scanner) : PageModel
{
    /// <summary>Most a backup upload may be (compressed).</summary>
    public const long MaxBackupBytes = 1024L * 1024 * 1024;

    [BindProperty]
    public TimeDisplay TimeDisplay { get; set; }

    [BindProperty]
    public int SessionTimeoutMinutes { get; set; }

    [BindProperty]
    public int AuditRetentionDays { get; set; }

    [BindProperty]
    public int ImageUpdateCheckHours { get; set; }

    [BindProperty]
    public int VulnerabilityScanHours { get; set; }

    [BindProperty]
    public IFormFile? Backup { get; set; }

    /// <summary>The scanner's version and databases; null with <see cref="ScannerProblem"/> when it can't run.</summary>
    public ScannerStatus? Scanner { get; private set; }

    public string? ScannerProblem { get; private set; }

    public ScannerOptions ScannerOptions => options.Value.Scanner;

    public string ScannerCachePath => options.Value.ScannerCachePath;

    public HttpsStatus Https { get; private set; } = null!;

    public OidcOptions Oidc => options.Value.Oidc;

    public bool RestorePending => backup.IsRestorePending;

    /// <summary>The server stops after this response.</summary>
    public bool Restarting { get; private set; }

    public string DataPath => options.Value.DataPath;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Load(fromSettings: true);
        (Scanner, ScannerProblem) = await scanner.TryStatusAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var problems = await settings.SaveAsync(new AppSettings(TimeDisplay, SessionTimeoutMinutes, AuditRetentionDays, ImageUpdateCheckHours, VulnerabilityScanHours), cancellationToken);
        if (problems.Count > 0)
        {
            foreach (var problem in problems)
                ModelState.AddModelError("", problem);
            Load(fromSettings: false);
            (Scanner, ScannerProblem) = await scanner.TryStatusAsync(cancellationToken);
            return Page();
        }
        Flash.Success(TempData, "Saved the settings. A new session timeout applies from each user's next sign-in.");
        return RedirectToPage();
    }

    /// <summary>The internal CA's certificate, to install in browsers (for the built-in HTTPS) or to check agents with.</summary>
    [NoAudit]
    public IActionResult OnGetCa() =>
        File(Encoding.ASCII.GetBytes(ca.CaCertificatePem), "application/x-pem-file", "sockless-port-ca.crt");

    /// <summary>A backup of the data volume. A post, so it's in the audit log and no link can start one.</summary>
    public async Task<IActionResult> OnPostBackupAsync(CancellationToken cancellationToken)
    {
        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
        Response.ContentType = "application/gzip";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{backup.FileName()}\"";
        Response.Headers.CacheControl = "no-store";
        await backup.WriteAsync(Response.Body, version, cancellationToken);
        return new EmptyResult();
    }

    public async Task<IActionResult> OnPostRestoreAsync(CancellationToken cancellationToken)
    {
        if (Backup is not { Length: > 0 } upload)
        {
            Flash.Error(TempData, "Choose a backup file (.tar.gz) to restore.");
            return RedirectToPage();
        }
        AuditTrail.Target(HttpContext, upload.FileName);
        try
        {
            await using var stream = upload.OpenReadStream();
            var staged = await backup.StageAsync(stream, cancellationToken);
            Flash.Success(TempData, $"The backup from {Format.Timestamp(staged.Manifest.CreatedAt)} is ready to restore. Restart Sockless Port to apply it.");
        }
        catch (BackupRejectedException ex)
        {
            Flash.Error(TempData, ex.Message);
        }
        return RedirectToPage();
    }

    public IActionResult OnPostCancelRestore()
    {
        backup.CancelPendingRestore();
        Flash.Success(TempData, "Cancelled the restore. Nothing was changed.");
        return RedirectToPage();
    }

    /// <summary>Stops the server once this response is sent; the container's restart policy starts it again.</summary>
    public IActionResult OnPostRestart()
    {
        Response.OnCompleted(() =>
        {
            lifetime.StopApplication();
            return Task.CompletedTask;
        });
        Restarting = true;
        Load(fromSettings: true);
        return Page();
    }

    private void Load(bool fromSettings)
    {
        if (fromSettings)
        {
            var current = settings.Current;
            TimeDisplay = current.TimeDisplay;
            SessionTimeoutMinutes = current.SessionTimeoutMinutes;
            AuditRetentionDays = current.AuditRetentionDays;
            ImageUpdateCheckHours = current.ImageUpdateCheckHours;
            VulnerabilityScanHours = current.VulnerabilityScanHours;
        }
        Https = https.Status();
    }
}
