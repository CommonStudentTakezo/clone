using System.Security.Cryptography;

namespace ColdTurkeyClone.Services;

/// <summary>
/// Cria e verifica hashes de senha (PBKDF2-SHA256) para proteger ações críticas:
/// desinstalar, parar o serviço de bloqueio, ou apagar as listas de bloqueio.
/// Nunca guardamos a senha em texto puro.
/// </summary>
public static class PasswordManager
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;

        try
        {
            var parts = stored.Split('.');
            if (parts.Length != 3) return false;
            if (!int.TryParse(parts[0], out var iterations)) return false;

            var salt = Convert.FromBase64String(parts[1]);
            var expectedHash = Convert.FromBase64String(parts[2]);

            var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expectedHash.Length);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (Exception ex)
        {
            // Hash armazenado corrompido/malformado — trata como senha errada
            // em vez de propagar a exceção (isso não devia derrubar o app).
            Logger.LogException("PasswordManager.Verify (hash armazenado malformado)", ex);
            return false;
        }
    }
}
