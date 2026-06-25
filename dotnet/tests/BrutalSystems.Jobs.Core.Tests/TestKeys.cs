using System.Security.Cryptography;

namespace BrutalSystems.Jobs.Core.Tests;

public static class TestKeys
{
    /// <summary>A throwaway 2048-bit RSA key, mirroring the Python rsa_keypair fixture.</summary>
    public static RSA NewRsa() => RSA.Create(2048);
}
