using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace HexLive.Updates
{
    // §166: identical wire contract and trust checks in Player and installer.
    [Serializable] public sealed class ReleaseEnvelope
    {
        public string payload = "";
        public string signature = "";
    }
    [Serializable] public sealed class ReleaseManifest
    {
        public int schemaVersion;
        public string product = "", channel = "", platform = "", architecture = "";
        public string version = "", minimumVersion = "", publishedUtc = "";
        public string notesEn = "", notesRu = "", driveFileId = "", sha256 = "";
        public long size, unpackedSize;
        public void Validate(string expectedPlatform, string expectedArchitecture)
        {
            if (schemaVersion != 1 || product != "client" || channel != "stable" ||
                platform != expectedPlatform || architecture != expectedArchitecture ||
                !Regex.IsMatch(sha256 ?? "", "^[a-f0-9]{64}$") || size <= 0 || unpackedSize <= 0 ||
                !Regex.IsMatch(driveFileId ?? "", "^[A-Za-z0-9_-]+$") ||
                ParseVersion(minimumVersion) > ParseVersion(version))
                throw new InvalidDataException("Invalid release contract");
            ParseVersion(version);
            if (!DateTimeOffset.TryParse(publishedUtc, out _)) throw new InvalidDataException("Invalid date");
        }
        public static Version ParseVersion(string value)
        {
            if (!Regex.IsMatch(value ?? "", @"^\d+\.\d+\.\d+$") || !Version.TryParse(value, out var result))
                throw new InvalidDataException("Invalid release version");
            return result;
        }
    }
    [Serializable] public sealed class UpdateConfig
    {
        public string endpoint = "", modulus = "", exponent = "AQAB";
        public string platform = "", architecture = "";
    }
    public static class ReleaseTrust
    {
        public static byte[] Verify(ReleaseEnvelope envelope, UpdateConfig config)
        {
            if (envelope == null || envelope.payload.Length > 131072) throw new InvalidDataException("Invalid envelope");
            var bytes = Convert.FromBase64String(envelope.payload);
            using (var rsa = RSA.Create())
            {
                rsa.ImportParameters(new RSAParameters {
                    Modulus = Convert.FromBase64String(config.modulus), Exponent = Convert.FromBase64String(config.exponent) });
                if (rsa.KeySize < 3072 || !rsa.VerifyData(bytes, Convert.FromBase64String(envelope.signature),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new CryptographicException("Invalid release signature");
            }
            return bytes;
        }
        public static void VerifyArchive(string path, ReleaseManifest manifest)
        {
            if (new FileInfo(path).Length != manifest.size) throw new InvalidDataException("Archive size mismatch");
            using (var input = File.OpenRead(path))
            using (var sha = SHA256.Create())
                if (BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant() != manifest.sha256)
                    throw new InvalidDataException("Archive hash mismatch");
        }
    }
}
