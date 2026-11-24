using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace AbyssRemote.Host;

/// <summary>Away from home (1.2). A reverse proxy on this PC (the Gaming Hub's Caddy) forwards one
/// public address, for example https://remote.example.duckdns.org, to Abyss Remote over
/// loopback. That address works at home too, so a device needs no other. A device is added
/// there with an away code, which the window shows on request, and a device that was paired
/// at home can take itself there. The pairing code never works there, and the root
/// certificate is not offered. Null in <see cref="ServerContext.Away"/> means the setting is off.</summary>
public sealed class AwayAccess
{
    public AwayAccess(string host, TimeProvider? clock = null)
    {
        if (!IsValidHost(host)) throw new ArgumentException("Not a usable away address.", nameof(host));
        Host = host.Trim().ToLowerInvariant();
        Codes = new AwayCodes(clock);
    }

    /// <summary>The public host name, without scheme, port or path.</summary>
    public string Host { get; }

    public string Url => "https://" + Host;

    public AwayCodes Codes { get; }

    /// <summary>A DNS name with at least two labels of letters, digits and hyphens. Not an IP
    /// address, and no scheme, port or path: the proxy decides those.</summary>
    public static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.Trim();
        if (host.Length > 253 || IPAddress.TryParse(host, out _)) return false;
        var labels = host.Split('.');
        if (labels.Length < 2 || labels[^1].All(char.IsAsciiDigit)) return false;
        return labels.All(label => label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-'
            && label.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-'));
    }
}

public enum AwayCodeResult { Accepted, WrongOrExpired, Locked }

/// <summary>One-time codes for the away address. There are two kinds. The window makes one on
/// request, to add a new device there (<see cref="NewDevice"/>). And a device that is already
/// paired asks for one for itself, from home: the hand-over behind "also use this device away
/// from home". A code works once and for ten minutes, and a newer code of the same owner
/// replaces the older one. Codes are twelve characters from 32 (2^60 possibilities), and 20
/// wrong codes in total destroy every open code, so guessing from the internet gets nowhere.</summary>
public sealed class AwayCodes
{
    /// <summary>The owner of the code the window makes. Used at the away address, it adds a
    /// new device instead of belonging to one.</summary>
    public const string NewDevice = "";

    /// <summary>Letters and digits without 0/O and 1/I, which are easy to mix up when typing.</summary>
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int Length = 12;
    public const int MaxFailures = 20;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly object _sync = new();
    private readonly TimeProvider _clock;
    private readonly List<Entry> _open = new();
    private int _failures;

    private sealed record Entry(string Code, string DeviceId, DateTimeOffset Expires);

    public AwayCodes(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>How many codes could still be used. For tests and the window.</summary>
    public int OpenCount
    {
        get
        {
            lock (_sync)
            {
                var now = _clock.GetUtcNow();
                return _open.Count(e => e.Expires > now);
            }
        }
    }

    /// <summary>True for text that could be an away code: twelve characters of the alphabet
    /// once spaces and dashes are gone. The eight digits of a pairing code never are.</summary>
    public static bool LooksLikeCode(string? typed)
    {
        string code = Normalize(typed);
        return code.Length == Length && code.All(Alphabet.Contains);
    }

    /// <summary>Takes back the open code of this owner. True when there was one.</summary>
    public bool Cancel(string deviceId)
    {
        lock (_sync) return _open.RemoveAll(e => e.DeviceId == deviceId) > 0;
    }

    /// <summary>True while this owner's code could still be used.</summary>
    public bool IsOpen(string deviceId)
    {
        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            return _open.Any(e => e.DeviceId == deviceId && e.Expires > now);
        }
    }

    /// <summary>A new code for this device, replacing any older one it had. For
    /// <see cref="NewDevice"/>: the window's code, which adds a device.</summary>
    public (string Code, DateTimeOffset ExpiresUtc) Issue(string deviceId)
    {
        var letters = new char[Length];
        for (int i = 0; i < Length; i++) letters[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        string code = new(letters);
        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            _open.RemoveAll(e => e.DeviceId == deviceId || e.Expires <= now);
            _open.Add(new Entry(code, deviceId, now + Lifetime));
            // A fresh code starts a fresh count; someone guessing still gets at most 20 tries per code.
            _failures = 0;
            return (code, now + Lifetime);
        }
    }

    /// <summary>Uses a code. On success returns the device it was made for (or
    /// <see cref="NewDevice"/>); the code is gone.</summary>
    public (AwayCodeResult Result, string? DeviceId) TryUse(string? typed)
    {
        string code = Normalize(typed);
        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            _open.RemoveAll(e => e.Expires <= now);
            if (_failures >= MaxFailures) return (AwayCodeResult.Locked, null);
            Entry? match = null;
            if (code.Length == Length)
            {
                byte[] given = Encoding.ASCII.GetBytes(code);
                // Compare with every open code (there are only a few), each in constant time.
                foreach (var entry in _open)
                    if (CryptographicOperations.FixedTimeEquals(given, Encoding.ASCII.GetBytes(entry.Code))) match = entry;
            }
            if (match is not null)
            {
                _open.Remove(match);
                return (AwayCodeResult.Accepted, match.DeviceId);
            }
            if (++_failures >= MaxFailures)
            {
                _open.Clear();
                return (AwayCodeResult.Locked, null);
            }
            return (AwayCodeResult.WrongOrExpired, null);
        }
    }

    /// <summary>Upper case, with the spaces and dashes people type or copy taken out.</summary>
    public static string Normalize(string? typed) =>
        new((typed ?? "").Where(ch => !char.IsWhiteSpace(ch) && ch != '-').Select(char.ToUpperInvariant).ToArray());

    /// <summary>"ABCD-EFGH-JKLM": three groups of four are easier to read and type.</summary>
    public static string Format(string code) => code.Length == Length ? $"{code[..4]}-{code[4..8]}-{code[8..]}" : code;
}

/// <summary>Where a request comes from, decided once per request before anything else runs.</summary>
public enum RequestRoute
{
    /// <summary>Straight from this PC, or from the home network in home-network mode, with a host name this PC answers to.</summary>
    Home,
    /// <summary>Through the away proxy on this PC, with the away address.</summary>
    Away,
    /// <summary>Anything else: the away address without the proxy, a proxied request for another
    /// name, an unknown host name (DNS rebinding). Answered with 421.</summary>
    Refused,
}

public static class AwayRequests
{
    private const string ItemKey = "AbyssRemote.Away";

    /// <summary>True when this request came through the away proxy (see <see cref="Route"/>).</summary>
    public static bool IsAway(HttpContext ctx) => ctx.Items.TryGetValue(ItemKey, out var value) && value is true;

    internal static void MarkAway(HttpContext ctx) => ctx.Items[ItemKey] = true;

    /// <summary>Headers that only a proxy adds. A browser never sends them, and Caddy always sets
    /// X-Forwarded-For to the address it really saw, replacing whatever the client sent.</summary>
    public static bool Forwarded(HttpRequest request) =>
        request.Headers.ContainsKey("X-Forwarded-For") || request.Headers.ContainsKey("Forwarded");

    public static bool FromLoopback(HttpContext ctx) => ctx.Connection.RemoteIpAddress is { } ip
        && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);

    /// <summary>Away means all three: the away address, a proxy header, and a connection from this
    /// PC itself. The internet can only reach Abyss Remote through that proxy, so an away request
    /// can never pass as a home one, and the away address never works without the proxy.</summary>
    public static RequestRoute Route(HttpContext ctx, RequestProtection protection, AwayAccess? away)
    {
        bool forwarded = Forwarded(ctx.Request);
        if (away is not null && ctx.Request.Host.Host.Equals(away.Host, StringComparison.OrdinalIgnoreCase))
            return forwarded && FromLoopback(ctx) ? RequestRoute.Away : RequestRoute.Refused;
        if (forwarded) return RequestRoute.Refused;
        return protection.KnownHost(ctx) ? RequestRoute.Home : RequestRoute.Refused;
    }

    /// <summary>The device's own address, for the activity list: what the proxy reports for away
    /// requests (checked to be an address, so it cannot inject text), otherwise the connection's.</summary>
    public static string ClientAddress(HttpContext ctx)
    {
        if (IsAway(ctx))
        {
            string first = ctx.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
            return IPAddress.TryParse(first, out var address) ? address.ToString() : "an unknown address";
        }
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}

public sealed record AwayRedeemRequest(string? Code);
