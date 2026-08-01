using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace HexLive.Server.Admin
{

/// <summary>
/// The single operator account that can pause, restart and shut down this
/// server.
/// <para>
/// <b>Passwords are never stored.</b> Only a PBKDF2-SHA256 hash with a per-account
/// random salt, so the file on disk cannot be turned back into a password even
/// if someone walks off with it. The iteration count is deliberately high enough
/// to make guessing slow and low enough that a small VPS still logs in briskly.
/// </para>
/// <para>
/// On first run there is no password to know, so one is GENERATED and printed to
/// the server console — the one place that proves you are the person who started
/// the process. It is marked temporary, and the panel refuses to do anything else
/// until it has been changed and an email address recorded for recovery.
/// </para>
/// </summary>
public sealed class AdminAccount
{
    // OWASP's PBKDF2-SHA256 guidance is 600k; 210k is the widely used compromise
    // and keeps first login under ~0.2 s on the kind of 1-vCPU box this is meant
    // to run on. Raise it, never lower it.
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly string _path;

    private byte[] _salt = Array.Empty<byte>();
    private byte[] _hash = Array.Empty<byte>();

    private AdminAccount(string path) => _path = path;

    /// <summary>True until the generated first-run password has been replaced.</summary>
    public bool PasswordIsTemporary { get; private set; }

    /// <summary>Where a reset link is sent. Empty until the operator sets it.</summary>
    public string Email { get; private set; } = string.Empty;

    /// <summary>Anything still required before the panel is fully usable.</summary>
    public bool SetupIncomplete => PasswordIsTemporary || Email.Length == 0;

    /// <summary>
    /// Loads the account, creating it with a printed one-time password if this is
    /// the first run.
    /// </summary>
    public static AdminAccount LoadOrCreate(string path)
    {
        var account = new AdminAccount(path);
        if (File.Exists(path) && account.TryLoad())
        {
            return account;
        }

        var temporary = GenerateReadablePassword();
        account.SetPassword(temporary);
        account.PasswordIsTemporary = true;
        account.Save();

        Console.WriteLine();
        Console.WriteLine("  ┌──────────────────────────────────────────────────────────┐");
        Console.WriteLine("  │  ADMIN PANEL — first run                                 │");
        Console.WriteLine("  │  Sign in at  /admin  with this one-time password:        │");
        Console.WriteLine($"  │      {temporary,-52}│");
        Console.WriteLine("  │  You will be asked to change it and add a recovery email.│");
        Console.WriteLine("  └──────────────────────────────────────────────────────────┘");
        Console.WriteLine();
        return account;
    }

    public bool Verify(string? password)
    {
        if (string.IsNullOrEmpty(password) || _hash.Length == 0)
        {
            return false;
        }

        var candidate = Derive(password, _salt);
        // Constant-time: a comparison that returns early leaks how much of the
        // hash matched, one byte at a time.
        return CryptographicOperations.FixedTimeEquals(candidate, _hash);
    }

    /// <summary>Replaces the password and clears the temporary flag.</summary>
    public void ChangePassword(string password)
    {
        SetPassword(password);
        PasswordIsTemporary = false;
        Save();
    }

    public void SetEmail(string? email)
    {
        Email = (email ?? string.Empty).Trim();
        Save();
    }

    /// <summary>
    /// Minimum a password must clear. Short rules, stated plainly, rather than a
    /// character-class puzzle nobody can satisfy without a manager anyway.
    /// </summary>
    public static string ValidatePassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 10)
        {
            return "Use at least 10 characters.";
        }

        return password.Trim().Length != password.Length
            ? "Leading or trailing spaces are too easy to mistype."
            : string.Empty;
    }

    public static bool LooksLikeEmail(string? email)
    {
        var text = (email ?? string.Empty).Trim();
        var at = text.IndexOf('@');
        // Deliberately loose: the real proof an address works is that the reset
        // mail arrives, and rejecting valid-but-unusual addresses helps nobody.
        return at > 0 && at < text.Length - 3 && text.IndexOf('.', at) > at + 1 && !text.Contains(' ');
    }

    private void SetPassword(string password)
    {
        _salt = RandomNumberGenerator.GetBytes(SaltBytes);
        _hash = Derive(password, _salt);
    }

    private static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

    /// <summary>
    /// Words-and-digits rather than random symbols: this one gets read off a
    /// terminal and typed into a browser, and an unreadable password just gets
    /// pasted into a chat window.
    /// </summary>
    private static string GenerateReadablePassword()
    {
        string[] words =
        {
            "island", "palm", "coral", "lagoon", "driftwood", "monsoon", "harbor",
            "compass", "lantern", "anchor", "cinder", "thicket", "seabird", "tideline",
        };

        var builder = new StringBuilder();
        for (var i = 0; i < 3; i++)
        {
            builder.Append(words[RandomNumberGenerator.GetInt32(words.Length)]);
            builder.Append('-');
        }

        builder.Append(RandomNumberGenerator.GetInt32(1000, 9999).ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    // A tiny hand-rolled record: four lines, no dependency, and readable if an
    // operator ever opens the file.
    private bool TryLoad()
    {
        try
        {
            foreach (var line in File.ReadAllLines(_path))
            {
                var split = line.IndexOf('=');
                if (split <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, split);
                var value = line.Substring(split + 1);
                switch (key)
                {
                    case "salt": _salt = Convert.FromBase64String(value); break;
                    case "hash": _hash = Convert.FromBase64String(value); break;
                    case "email": Email = value; break;
                    case "temporary": PasswordIsTemporary = value == "1"; break;
                }
            }

            return _salt.Length > 0 && _hash.Length > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[admin] account file unreadable ({ex.Message}) — recreating it");
            return false;
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllLines(_path, new[]
        {
            "salt=" + Convert.ToBase64String(_salt),
            "hash=" + Convert.ToBase64String(_hash),
            "email=" + Email,
            "temporary=" + (PasswordIsTemporary ? "1" : "0"),
        });

        TryRestrictPermissions();
    }

    private void TryRestrictPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            // No Unix modes there; Windows inherits the directory's ACL.
            return;
        }

        try
        {
            // Owner-only: this file holds a password hash and a recovery address.
            // Best effort — a failure here must not stop the server from running.
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
        }
    }
}

}
