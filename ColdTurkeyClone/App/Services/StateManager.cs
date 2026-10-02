using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using ColdTurkeyClone.Models;

namespace ColdTurkeyClone.Services;

/// <summary>
/// Salva/lê o estado em dois lugares independentes — JSON (ProgramData) e
/// Registro (HKLM) — e usa o MAIOR horário de término válido entre os dois.
/// Cada valor é acompanhado de uma assinatura HMAC-SHA256 calculada com uma
/// chave embutida no binário. Isso significa que só "editar o número" no JSON
/// ou no Registro na mão não funciona mais para encurtar o bloqueio: sem
/// recalcular a assinatura corretamente (o que exige extrair a chave do
/// executável via engenharia reversa), aquele valor editado é tratado como
/// inválido e descartado — e a outra cópia (ainda íntegra) prevalece.
///
/// Limite honesto: isso NÃO é criptografia inquebrável — é ofuscação. Alguém
/// com conhecimento de engenharia reversa e tempo disponível pode extrair a
/// chave do binário compilado. O objetivo é impedir a edição trivial (abrir o
/// JSON/regedit e mudar um número), não resistir a um ataque sofisticado.
///
/// Sincronização: assim como o hosts, o state.json é lido/escrito por DOIS
/// processos (App e Watchdog) o tempo todo — usa o mesmo padrão de Mutex
/// nomeado pra evitar uma escrita corromper/apagar a outra.
///
/// Resiliência: se o JSON estiver corrompido por qualquer motivo (escrita
/// truncada por queda de energia, por exemplo), Load() não derruba o app —
/// loga o erro e recomeça com um estado novo, em vez de propagar a exceção
/// pra cima (isso já causou pelo menos um risco real de crash na abertura
/// do app, antes desta correção).
/// </summary>
public static class StateManager
{
    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ColdTurkeyClone");

    private static readonly string StateFile = Path.Combine(DataDir, "state.json");

    private const string RegKeyPath = @"SOFTWARE\ColdTurkeyClone";
    private const string RegTicksValue = "LockedUntilUtcTicks";
    private const string RegSigValue = "LockSignature";

    // Chave embutida no binário. Troque por bytes aleatórios seus antes de compilar
    // (isso NÃO é uma chave secreta de verdade — é ofuscação, não criptografia).
    private static readonly byte[] SigningKey =
        Convert.FromBase64String("k3F9zQpL8mR2vXo4TgYbN7hWc1JeSdAq6UiZtHfP0rMlKn5B");

    private static readonly Mutex StateMutex = new(false, "Global\\ColdTurkeyClone_StateFileMutex");

    private static T WithStateLock<T>(Func<T> action)
    {
        var acquired = false;
        try
        {
            acquired = StateMutex.WaitOne(TimeSpan.FromSeconds(10));
            return action();
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
            return action();
        }
        finally
        {
            if (acquired)
            {
                try { StateMutex.ReleaseMutex(); } catch { }
            }
        }
    }

    public static BlockState Load() => WithStateLock(() =>
    {
        Directory.CreateDirectory(DataDir);

        BlockState state;
        try
        {
            if (File.Exists(StateFile))
            {
                var json = File.ReadAllText(StateFile);
                state = JsonSerializer.Deserialize<BlockState>(json) ?? new BlockState();
            }
            else
            {
                state = new BlockState();
            }
        }
        catch (Exception ex)
        {
            // state.json corrompido (escrita truncada, concorrência, etc.) —
            // melhor recomeçar do zero do que derrubar o app inteiro.
            Logger.LogException("StateManager.Load (state.json corrompido — recomeçando do zero)", ex);
            state = new BlockState();
        }

        DateTime? jsonValid = (state.LockedUntilUtc.HasValue && Verify(state.LockedUntilUtc.Value.Ticks, state.LockSignature))
            ? state.LockedUntilUtc
            : null;

        var (regTicks, regSig) = ReadRegistryLock();
        DateTime? regValid = (regTicks.HasValue && Verify(regTicks.Value, regSig))
            ? new DateTime(regTicks.Value, DateTimeKind.Utc)
            : null;

        DateTime? finalUntil;
        if (jsonValid.HasValue && regValid.HasValue) finalUntil = jsonValid.Value > regValid.Value ? jsonValid : regValid;
        else finalUntil = jsonValid ?? regValid;

        state.LockedUntilUtc = finalUntil;
        state.LockSignature = finalUntil.HasValue ? Sign(finalUntil.Value.Ticks) : null;

        return state;
    });

    public static void Save(BlockState state) => WithStateLock(() =>
    {
        try
        {
            Directory.CreateDirectory(DataDir);

            state.LockSignature = state.LockedUntilUtc.HasValue ? Sign(state.LockedUntilUtc.Value.Ticks) : null;

            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StateFile, json);

            if (state.LockedUntilUtc.HasValue)
                WriteRegistryLock(state.LockedUntilUtc.Value, state.LockSignature!);
            else
                ClearRegistryLock();
        }
        catch (Exception ex)
        {
            Logger.LogException("StateManager.Save", ex);
        }

        return true;
    });

    private static string Sign(long ticks)
    {
        var hash = HMACSHA256.HashData(SigningKey, BitConverter.GetBytes(ticks));
        return Convert.ToBase64String(hash);
    }

    private static bool Verify(long ticks, string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        try
        {
            var expected = HMACSHA256.HashData(SigningKey, BitConverter.GetBytes(ticks));
            var actual = Convert.FromBase64String(signature);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }

    private static (long? ticks, string? sig) ReadRegistryLock()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegKeyPath);
            var ticksVal = key?.GetValue(RegTicksValue) as string;
            var sigVal = key?.GetValue(RegSigValue) as string;
            if (ticksVal != null && long.TryParse(ticksVal, out var ticks))
                return (ticks, sigVal);
        }
        catch (Exception ex)
        {
            Logger.LogException("StateManager.ReadRegistryLock", ex);
        }
        return (null, null);
    }

    private static void WriteRegistryLock(DateTime untilUtc, string signature)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(RegKeyPath);
            key?.SetValue(RegTicksValue, untilUtc.Ticks.ToString(), RegistryValueKind.String);
            key?.SetValue(RegSigValue, signature, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Logger.LogException("StateManager.WriteRegistryLock", ex);
        }
    }

    private static void ClearRegistryLock()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegKeyPath, writable: true);
            key?.DeleteValue(RegTicksValue, throwOnMissingValue: false);
            key?.DeleteValue(RegSigValue, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Logger.LogException("StateManager.ClearRegistryLock", ex);
        }
    }
}
