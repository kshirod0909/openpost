using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenPost.Api.Contracts;
using OpenPost.Api.Security;

namespace OpenPost.Api.Controllers;

[ApiController]
[Route("api/v1/system")]
public sealed class SystemController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly MailServerOptions _mail;
    private readonly SpamOptions _spam;
    private readonly AntivirusOptions _antivirus;

    public SystemController(
        IHostEnvironment environment,
        IOptions<MailServerOptions> mail,
        IOptions<SpamOptions> spam,
        IOptions<AntivirusOptions> antivirus)
    {
        _environment = environment;
        _mail = mail.Value;
        _spam = spam.Value;
        _antivirus = antivirus.Value;
    }

    /// <summary>Public instance metadata used by the web client's login screen and by monitoring.</summary>
    [HttpGet("info")]
    [AllowAnonymous]
    public ActionResult<InstanceInfoDto> Info()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        var features = new List<string>
        {
            "smtp-inbound",
            "smtp-submission",
            "dkim-signing",
            "dmarc-reporting",
            "spam-filtering",
            "multi-domain",
            "rest-api"
        };

        if (_spam.RspamdUrl is not null) features.Add("rspamd");
        if (_antivirus.Enabled) features.Add("clamav");
        if (_mail.TlsCertificatePath is not null) features.Add("starttls");

        return Ok(new InstanceInfoDto(
            "OpenPost",
            version,
            _environment.EnvironmentName,
            _mail.HostName,
            DateTimeOffset.UtcNow,
            "PostgreSQL",
            features));
    }
}
