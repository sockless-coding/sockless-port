namespace Sockless.Port;

/// <summary>Application-wide settings, bound from the <c>Port</c> configuration section.</summary>
public sealed class PortOptions
{
    public const string SectionName = "Port";

    /// <summary>
    /// Directory for everything Sockless Port persists: the SQLite database, Data Protection keys
    /// and stack files. In the container this is the <c>/data</c> volume.
    /// </summary>
    public string DataPath { get; set; } = "/data";

    /// <summary>
    /// Address of the local engine, e.g. <c>unix:///run/podman/podman.sock</c>. When empty, the
    /// address is detected on first start (<c>DOCKER_HOST</c>, then the well-known sockets). When
    /// set, it also replaces the stored local endpoint's address on every start.
    /// </summary>
    public string? LocalEngine { get; set; }

    /// <summary>
    /// Image for the short-lived helper containers that read a volume's files. It needs
    /// <c>find</c> and <c>stat</c> (busybox has both). Pulled when missing, so set one the engine
    /// already has on hosts without internet access.
    /// </summary>
    public string HelperImage { get; set; } = "busybox:1.37";

    /// <summary>
    /// The Compose command, e.g. <c>docker-compose</c>, <c>docker compose</c> or a full path. When
    /// empty, <c>docker-compose</c> is tried first, then <c>docker compose</c>. The image bundles
    /// <c>docker-compose</c>.
    /// </summary>
    public string? Compose { get; set; }

    /// <summary>
    /// The Docker CLI, for swarm stacks (<c>docker stack deploy</c>): <c>docker</c> or a full path.
    /// When empty, <c>docker</c> on the search path. The image bundles it.
    /// </summary>
    public string? DockerCli { get; set; }

    /// <summary>
    /// The git client, for stacks whose files come from a repository: <c>git</c> or a full path.
    /// When empty, <c>git</c> on the search path. The image bundles it, with <c>ssh</c>.
    /// </summary>
    public string? Git { get; set; }

    /// <summary>
    /// The agent image the install commands use. When empty, the image matching this server's
    /// version (<c>ghcr.io/sockless-coding/sockless-port-agent:{version}</c>).
    /// </summary>
    public string? AgentImage { get; set; }

    /// <summary>The vulnerability scanner (<c>Port:Scanner</c>). The image bundles Trivy.</summary>
    public ScannerOptions Scanner { get; set; } = new();

    /// <summary>Sign-in attempts (password or setup) allowed per client address per minute.</summary>
    public int SignInAttemptsPerMinute { get; set; } = 10;

    /// <summary>Built-in HTTPS for the UI (<c>Port:Https</c>). Off unless a port is set.</summary>
    public HttpsOptions Https { get; set; } = new();

    /// <summary>Single sign-on through an OpenID Connect provider (<c>Port:Oidc</c>). Off unless an authority is set.</summary>
    public OidcOptions Oidc { get; set; } = new();

    public string DatabasePath => Path.Combine(DataPath, "port.db");

    public string KeysPath => Path.Combine(DataPath, "keys");

    /// <summary>Stack files: <c>stacks/{endpointId}/{name}/compose.yaml</c> and <c>.env</c>.</summary>
    public string StacksPath => Path.Combine(DataPath, "stacks");

    /// <summary>Checkouts of git-backed stacks: <c>git/{endpointId}/{name}</c>. Not backed up: they're fetched again.</summary>
    public string GitPath => Path.Combine(DataPath, "git");

    /// <summary>Uploaded build contexts while they're built: on the data volume, since they can be large. Emptied on start.</summary>
    public string TempPath => Path.Combine(DataPath, "tmp");

    /// <summary>A restore waiting for the next start, extracted from an uploaded backup.</summary>
    public string RestorePendingPath => Path.Combine(DataPath, "restore-pending");

    /// <summary>The scanner's vulnerability databases. Not backed up: they're downloaded again.</summary>
    public string ScannerCachePath => Path.Combine(DataPath, "scanner");
}

/// <summary>
/// Scanning images for known vulnerabilities with Trivy. Images are exported from their engine
/// to the server and scanned there, so remote endpoints need nothing extra.
/// </summary>
public sealed class ScannerOptions
{
    /// <summary>The Trivy command: <c>trivy</c> or a full path. When empty, <c>trivy</c> on the search path. The image bundles it.</summary>
    public string? Command { get; set; }

    /// <summary>
    /// Don't download the vulnerability databases: for servers without internet access. Put them
    /// in <c>{DataPath}/scanner</c> yourself (<c>trivy image --download-db-only --cache-dir …</c>
    /// elsewhere, then copy the <c>db</c> and <c>java-db</c> folders), or use a mirror instead.
    /// Scans then also skip the online lookups some Java packages need.
    /// </summary>
    public bool SkipDatabaseUpdate { get; set; }

    /// <summary>OCI repositories to download the vulnerability database from, tried in order (a mirror). When empty, Trivy's default.</summary>
    public string[] DatabaseRepositories { get; set; } = [];

    /// <summary>OCI repositories for the Java database (jar files), as <see cref="DatabaseRepositories"/>.</summary>
    public string[] JavaDatabaseRepositories { get; set; } = [];

    /// <summary>The longest one scan may take, export included, in minutes. Scans run one at a time.</summary>
    public int TimeoutMinutes { get; set; } = 30;
}

/// <summary>
/// An HTTPS listener next to the HTTP one. With <see cref="Certificate"/> it serves that
/// certificate (reloaded when the file changes, e.g. after a renewal); without, a certificate the
/// internal CA issues for <see cref="HostNames"/>, renewed automatically. The CA certificate is
/// downloadable from Settings, to install in browsers.
/// </summary>
public sealed class HttpsOptions
{
    /// <summary>The port to listen on, e.g. 8443. Null turns HTTPS off.</summary>
    public int? Port { get; set; }

    /// <summary>A PEM certificate (with <see cref="CertificateKey"/>) or a PKCS#12 (.pfx/.p12) file.</summary>
    public string? Certificate { get; set; }

    /// <summary>The PEM private key for <see cref="Certificate"/>; empty when the key is in the same file.</summary>
    public string? CertificateKey { get; set; }

    /// <summary>The password of a PKCS#12 file or an encrypted PEM key.</summary>
    public string? CertificatePassword { get; set; }

    /// <summary>
    /// Names the internal CA's certificate covers: DNS names or IP addresses, comma-separated in
    /// environment variables. When empty: <c>localhost</c>, <c>127.0.0.1</c> and the machine name.
    /// </summary>
    public string[] HostNames { get; set; } = [];

    /// <summary>Send plain HTTP requests to HTTPS, and tell browsers to stay there (HSTS). The health check stays on HTTP.</summary>
    public bool RedirectHttp { get; set; }

    /// <summary>The port browsers reach HTTPS on, when the container publishes it as another (<c>9443:8443</c>). For the redirect.</summary>
    public int? PublicPort { get; set; }

    public bool Enabled => Port is > 0;

    public bool UsesInternalCa => string.IsNullOrWhiteSpace(Certificate);
}

/// <summary>
/// Sign-in through an OpenID Connect provider (Keycloak, Authentik, Entra ID, …), next to local
/// passwords. Register Sockless Port as a confidential client with the redirect URI
/// <c>https://{host}/signin-oidc</c>.
/// </summary>
public sealed class OidcOptions
{
    /// <summary>The issuer, e.g. <c>https://auth.example.com/realms/main</c>. Empty turns single sign-on off.</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>The sign-in button's label.</summary>
    public string DisplayName { get; set; } = "Single sign-on";

    /// <summary>Scopes besides <c>openid</c> and <c>profile</c>. <c>email</c> is needed to link existing users.</summary>
    public string[] Scopes { get; set; } = ["email"];

    /// <summary>
    /// Create a user on their first sign-in, with no access until an administrator grants some
    /// (or <see cref="DefaultRole"/>). Otherwise only users who exist already can sign in: linked
    /// by a verified email address that matches theirs.
    /// </summary>
    public bool CreateUsers { get; set; }

    /// <summary>The default role of users created on sign-in: None, Viewer or Operator.</summary>
    public Access.EndpointRole DefaultRole { get; set; } = Access.EndpointRole.None;

    /// <summary>Hide the password form, leaving only single sign-on. Local passwords still work at <c>/account/login?local=true</c>, for recovery.</summary>
    public bool DisablePasswordLogin { get; set; }

    /// <summary>Allow an http:// authority, for testing only.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    public bool Enabled => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);
}
