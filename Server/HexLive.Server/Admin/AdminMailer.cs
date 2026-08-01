using System;
using System.Net;
using System.Net.Mail;

namespace HexLive.Server.Admin
{

/// <summary>
/// Sends the password-reset link.
/// <para>
/// SMTP settings come from the environment (<c>HEXLIVE_SMTP_HOST</c>,
/// <c>_PORT</c>, <c>_USER</c>, <c>_PASSWORD</c>, <c>_FROM</c>) rather than from
/// a config file, because they include a credential and config files get
/// committed by accident.
/// </para>
/// <para>
/// <b>With no SMTP configured the link is printed to the server console instead
/// of being emailed.</b> That is a deliberate fallback, not a stub: whoever can
/// read this server's console is already the operator, so it is a legitimate
/// recovery path for a self-hosted box — and far better than a reset flow that
/// silently does nothing and leaves someone locked out.
/// </para>
/// </summary>
public sealed class AdminMailer
{
    private readonly string? _host;
    private readonly int _port;
    private readonly string? _user;
    private readonly string? _password;
    private readonly string _from;

    public AdminMailer()
    {
        _host = Environment.GetEnvironmentVariable("HEXLIVE_SMTP_HOST");
        _port = int.TryParse(Environment.GetEnvironmentVariable("HEXLIVE_SMTP_PORT"), out var port) ? port : 587;
        _user = Environment.GetEnvironmentVariable("HEXLIVE_SMTP_USER");
        _password = Environment.GetEnvironmentVariable("HEXLIVE_SMTP_PASSWORD");
        _from = Environment.GetEnvironmentVariable("HEXLIVE_SMTP_FROM") ?? "hexlive@localhost";
    }

    public bool CanSendMail => !string.IsNullOrWhiteSpace(_host);

    /// <summary>
    /// Delivers the reset link. Returns how it went so the page can tell the
    /// truth — "check your email" is a lie if nothing was sent.
    /// </summary>
    public string Send(string toEmail, string resetUrl)
    {
        if (!CanSendMail)
        {
            Console.WriteLine();
            Console.WriteLine("  [admin] PASSWORD RESET REQUESTED (no SMTP configured — link below)");
            Console.WriteLine($"  [admin] {resetUrl}");
            Console.WriteLine("  [admin] valid for 30 minutes, single use");
            Console.WriteLine();
            return "printed";
        }

        try
        {
            using var client = new SmtpClient(_host, _port) { EnableSsl = true };
            if (!string.IsNullOrEmpty(_user))
            {
                client.Credentials = new NetworkCredential(_user, _password);
            }

            using var message = new MailMessage(_from, toEmail)
            {
                Subject = "HexLive server — password reset",
                Body =
                    "Someone asked to reset the admin password for your HexLive server.\n\n" +
                    resetUrl + "\n\n" +
                    "The link works once and expires in 30 minutes.\n" +
                    "If this was not you, no action is needed — the current password still works.",
            };

            client.Send(message);
            return "sent";
        }
        catch (Exception ex)
        {
            // Falling back to the console beats leaving the operator with a
            // cheerful "check your email" and no email.
            Console.WriteLine($"  [admin] reset mail FAILED ({ex.Message}); link below");
            Console.WriteLine($"  [admin] {resetUrl}");
            return "failed";
        }
    }
}

}
