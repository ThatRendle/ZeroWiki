using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace ZeroWiki.Security;

/// <summary>
/// Argon2id password hashing. The salt and cost parameters are encoded alongside the
/// digest in PHC string format, so a stored hash is self-describing: verification uses
/// the parameters embedded in the hash it is checking, and the constants below can be
/// raised later without invalidating hashes already in the store.
/// </summary>
/// <remarks>
/// The PHC format is used for self-description, not interop: this class is the only thing
/// that reads these strings, and nothing here is checked against the Argon2 reference test
/// vectors, so do not assume another Argon2 implementation can consume them.
/// </remarks>
public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    /// <summary>
    /// Cost parameters for newly created hashes: 64 MiB of memory, three passes, single
    /// lane. Comfortably above the OWASP Argon2id floor (19 MiB, t=2, p=1) and cheap
    /// enough for a deployment with a handful of users and a low login rate.
    /// </summary>
    private const int MemorySizeKib = 65536;
    private const int Iterations = 3;
    private const int DegreeOfParallelism = 1;
    private const int SaltLength = 16;
    private const int HashLength = 32;

    /// <summary>Argon2 version 1.3 — the only version this hasher reads or writes.</summary>
    private const int Version = 19;

    private const string AlgorithmId = "argon2id";

    /// <summary>
    /// Bounds applied to parameters parsed out of a stored hash. Rejecting a corrupt or
    /// hostile hash string has to stay cheap, so the ceilings sit just above anything this
    /// class emits rather than at what Argon2 permits; the floors are RFC 9106's minimums.
    /// </summary>
    private const int MinParsedMemorySizeKib = 8;
    private const int MaxParsedMemorySizeKib = 256 * 1024;
    private const int MaxParsedIterations = 16;
    private const int MaxParsedDegreeOfParallelism = 16;
    private const int MinParsedSaltLength = 8;
    private const int MinParsedHashLength = 16;

    /// <summary>
    /// RFC 9106 requires at least eight KiB of memory per lane (Konscious itself only
    /// insists on four). Enforcing the stricter bound keeps the library from ever being the
    /// one to reject a parameter set, so <see cref="Verify"/> answers with
    /// <see langword="false"/> instead of an exception.
    /// </summary>
    private const int MinParsedMemorySizeKibPerLane = 8;

    public string Hash(string password)
    {
        // Argon2 has no defined output for a zero-length password, so an empty one can never
        // be stored — and therefore can never verify (see below).
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Derive(password, salt, MemorySizeKib, Iterations, DegreeOfParallelism, HashLength);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"${AlgorithmId}$v={Version}$m={MemorySizeKib},t={Iterations},p={DegreeOfParallelism}${EncodeB64(salt)}${EncodeB64(hash)}");
    }

    public bool Verify(string password, string? storedHash)
    {
        if (string.IsNullOrEmpty(password) || storedHash is null || !TryParse(storedHash, out var stored))
        {
            return false;
        }

        byte[] computed;
        try
        {
            computed = Derive(
                password,
                stored.Salt,
                stored.MemorySizeKib,
                stored.Iterations,
                stored.DegreeOfParallelism,
                stored.Hash.Length);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            // TryParse should already have rejected every parameter set the KDF refuses, so
            // this is the belt to that brace: a stored hash the KDF will not process is a
            // corrupt row, and answering "no" keeps login failures uniform instead of turning
            // one account's login into a server error. The filter is broad on purpose — the
            // escaping type is not predictable (Konscious runs lanes on tasks, so its own
            // validation surfaces as AggregateException), and a type list would only reopen
            // this hole for whatever a future version throws instead. The two exclusions say
            // nothing about the stored hash and must not be reported as a wrong password;
            // note the memory one is best-effort only, since a lane's OutOfMemoryException
            // arrives wrapped in an AggregateException and so still passes this filter. That
            // is acceptable because the parsed-parameter ceilings cap a single verification
            // well below anything that could plausibly exhaust memory by itself.
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(computed, stored.Hash);
    }

    public bool CanVerify(string? storedHash) => storedHash is not null && TryParse(storedHash, out _);

    private static byte[] Derive(
        string password,
        byte[] salt,
        int memorySizeKib,
        int iterations,
        int degreeOfParallelism,
        int hashLength)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon2 = new Argon2id(passwordBytes)
            {
                Salt = salt,
                MemorySize = memorySizeKib,
                Iterations = iterations,
                DegreeOfParallelism = degreeOfParallelism,
            };

            return argon2.GetBytes(hashLength);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static bool TryParse(string encoded, [NotNullWhen(true)] out EncodedHash? stored)
    {
        stored = null;

        // "$argon2id$v=19$m=65536,t=3,p=1$<salt>$<hash>" splits into six segments,
        // the first of which is empty because the string starts with the separator.
        var segments = encoded.Split('$');
        if (segments.Length != 6 || segments[0].Length != 0 || segments[1] != AlgorithmId)
        {
            return false;
        }

        if (!TryParseTagged(segments[2], "v", out var version) || version != Version)
        {
            return false;
        }

        var costs = segments[3].Split(',');
        if (costs.Length != 3
            || !TryParseTagged(costs[0], "m", out var memorySizeKib)
            || !TryParseTagged(costs[1], "t", out var iterations)
            || !TryParseTagged(costs[2], "p", out var degreeOfParallelism))
        {
            return false;
        }

        if (memorySizeKib < MinParsedMemorySizeKib || memorySizeKib > MaxParsedMemorySizeKib
            || iterations < 1 || iterations > MaxParsedIterations
            || degreeOfParallelism < 1 || degreeOfParallelism > MaxParsedDegreeOfParallelism
            || memorySizeKib < MinParsedMemorySizeKibPerLane * degreeOfParallelism)
        {
            return false;
        }

        if (!TryDecodeB64(segments[4], out var salt) || salt.Length < MinParsedSaltLength
            || !TryDecodeB64(segments[5], out var hash) || hash.Length < MinParsedHashLength)
        {
            return false;
        }

        stored = new EncodedHash(memorySizeKib, iterations, degreeOfParallelism, salt, hash);
        return true;
    }

    private static bool TryParseTagged(string segment, string name, out int value)
    {
        value = 0;

        return segment.Length > name.Length + 1
            && segment.StartsWith(name, StringComparison.Ordinal)
            && segment[name.Length] == '='
            && int.TryParse(
                segment.AsSpan(name.Length + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value);
    }

    /// <summary>PHC-format base64: the standard alphabet with the padding removed.</summary>
    private static string EncodeB64(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static bool TryDecodeB64(string value, [NotNullWhen(true)] out byte[]? bytes)
    {
        bytes = null;

        var padded = (value.Length % 4) switch
        {
            0 => value,
            2 => value + "==",
            3 => value + "=",
            _ => null,
        };

        if (padded is null || padded.Length == 0)
        {
            return false;
        }

        var buffer = new byte[padded.Length / 4 * 3];
        if (!Convert.TryFromBase64String(padded, buffer, out var written) || written == 0)
        {
            return false;
        }

        var decoded = buffer.AsSpan(0, written).ToArray();

        // Reject anything that is not the canonical encoding of these bytes — padding,
        // embedded whitespace, or non-zero trailing bits.
        if (!string.Equals(EncodeB64(decoded), value, StringComparison.Ordinal))
        {
            return false;
        }

        bytes = decoded;
        return true;
    }

    private sealed record EncodedHash(
        int MemorySizeKib,
        int Iterations,
        int DegreeOfParallelism,
        byte[] Salt,
        byte[] Hash);
}
