using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace ColdTurkeyClone.Services;

/// <summary>
/// Instala a extensão automaticamente no Chrome/Edge/Brave via
/// ExtensionInstallForcelist — a política oficial do Chrome pra instalar
/// extensões sem interação do usuário e SEM que ele consiga desativar ou
/// remover pela tela chrome://extensions (documentado em
/// developer.chrome.com/docs/extensions/develop/concepts/extensions-update-lifecycle).
///
/// Validação: o formato "extension_id;file:///caminho/manifesto.xml" usado
/// aqui é exatamente o mesmo documentado pela UiPath pra distribuir a
/// extensão Chrome deles via instalação offline por Política de Grupo
/// (docs.uipath.com/studio/standalone/latest/user-guide/chrome-group-policies) —
/// um caso real, em produção, do mesmo mecanismo.
///
/// Autoverificação: depois de gerar o .crx, o app relê o próprio arquivo do
/// zero (exatamente como o Chrome faria) e confere matematicamente se a
/// assinatura RSA bate com o conteúdo, e se o crx_id bate com a chave
/// pública embutida. Se algo não bater, o .crx NÃO é publicado (a política
/// não é configurada) — evita apontar pra um pacote corrompido de forma
/// silenciosa. É o mais perto que dá pra chegar de "testar no Chrome" sem
/// ter o Chrome disponível pra rodar de verdade.
///
/// Limite honesto: isso cobre Chrome/Edge/Brave. Não cobre Firefox (usa um
/// sistema de add-ons diferente) nem navegadores que ignoram políticas de
/// grupo do Windows. E mesmo com a autoverificação passando, ela confirma
/// que o ARQUIVO está correto — não substitui confirmar de verdade em
/// chrome://extensions no seu navegador.
/// </summary>
public static class ExtensionInstaller
{
    public const string ExtensionId = "lhakmhioicoklhgmkpemilobjfddiggi";

    private static readonly string[] ForcelistKeyPaths =
    {
        @"SOFTWARE\Policies\Google\Chrome\ExtensionInstallForcelist",
        @"SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist",
        @"SOFTWARE\Policies\BraveSoftware\Brave\ExtensionInstallForcelist",
    };

    // App e Watchdog chamam Install() de forma independente (o Watchdog a
    // cada ciclo de 5s enquanto Proteções permanentes estiver ativo). Sem
    // coordenação, os dois escrevendo extension.crx/update.xml ao mesmo
    // tempo causa exatamente o tipo de corrida que já vimos com o hosts e
    // com o quick-install.ps1 ("arquivo sendo usado por outro processo").
    private static readonly Mutex InstallMutex = new(false, "Global\\ColdTurkeyClone_ExtensionInstallMutex");

    public static void Install(string extensionFolderPath)
    {
        var acquired = false;
        try
        {
            acquired = InstallMutex.WaitOne(TimeSpan.FromSeconds(10));
            InstallInternal(extensionFolderPath);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
            InstallInternal(extensionFolderPath);
        }
        finally
        {
            if (acquired)
            {
                try { InstallMutex.ReleaseMutex(); } catch { }
            }
        }
    }

    private static void InstallInternal(string extensionFolderPath)
    {
        if (!Directory.Exists(extensionFolderPath))
        {
            Logger.Log($"ExtensionInstaller.Install: pasta da extensão não encontrada em {extensionFolderPath} — nada a fazer.");
            return;
        }

        var crxPath = Path.Combine(extensionFolderPath, "extension.crx");
        var updateXmlPath = Path.Combine(extensionFolderPath, "update.xml");

        // ExtensionSigningKey fica um nível acima da pasta BrowserExtension
        // (ambas lado a lado, na raiz da instalação).
        var installRoot = Path.GetDirectoryName(extensionFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var signingKeyPath = installRoot != null
            ? Path.Combine(installRoot, "ExtensionSigningKey", "extension_key.pem")
            : null;

        if (signingKeyPath != null && NeedsRegeneration(extensionFolderPath, crxPath, signingKeyPath))
        {
            var ok = GenerateSignedCrx(extensionFolderPath, signingKeyPath, crxPath);
            if (!ok)
            {
                Logger.Log("ExtensionInstaller.Install: geração/autoverificação do .crx falhou — NÃO vou configurar a política com um pacote possivelmente quebrado.");
                return;
            }
        }

        if (!File.Exists(crxPath))
        {
            // Nunca foi gerado com sucesso (nem antes, nem agora) — não tem o que publicar.
            return;
        }

        WriteUpdateManifest(updateXmlPath, crxPath);

        var updateUrl = new Uri(updateXmlPath).AbsoluteUri; // vira file:///C:/...
        var expectedValue = $"{ExtensionId};{updateUrl}";

        foreach (var keyPath in ForcelistKeyPaths)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(keyPath);
                key?.SetValue("1", expectedValue, RegistryValueKind.String);

                // Confere de verdade que a escrita "pegou" — o mesmo padrão
                // de releitura que já usamos pro hosts. Sem isso, uma falha
                // silenciosa aqui (ex: permissão negada mesmo elevado, chave
                // bloqueada por outra política) ficaria invisível.
                var readBack = key?.GetValue("1") as string;
                if (readBack != expectedValue)
                {
                    Logger.Log($"AVISO: escrevi a política em {keyPath} mas a releitura não bate. " +
                               $"Esperado: '{expectedValue}' | Lido: '{readBack ?? "(nulo)"}'.");
                }
                else
                {
                    Logger.Log($"Política ExtensionInstallForcelist confirmada em {keyPath}.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogException($"ExtensionInstaller.Install ({keyPath})", ex);
            }
        }
    }

    /// <summary>
    /// Lê de volta o que está REALMENTE configurado agora em cada navegador,
    /// pra diagnóstico (botão/painel de status). Não confia em "eu escrevi,
    /// deve estar lá" — lê o valor de verdade do Registro.
    /// </summary>
    public static Dictionary<string, string> GetPolicyStatus()
    {
        var result = new Dictionary<string, string>();
        foreach (var keyPath in ForcelistKeyPaths)
        {
            var browserName = keyPath.Contains("Google") ? "Chrome" : keyPath.Contains("Microsoft") ? "Edge" : "Brave";
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                var value = key?.GetValue("1") as string;
                result[browserName] = string.IsNullOrEmpty(value) ? "não configurada" : "configurada";
            }
            catch (Exception ex)
            {
                result[browserName] = $"erro ao ler: {ex.Message}";
            }
        }
        return result;
    }

    public static void Uninstall()
    {
        foreach (var keyPath in ForcelistKeyPaths)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
                key?.DeleteValue("1", throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                Logger.LogException($"ExtensionInstaller.Uninstall ({keyPath})", ex);
            }
        }
    }

    /// <summary>Reverifica um extension.crx já existente em disco. Uso: botão de diagnóstico na interface.</summary>
    public static (bool Ok, string Message) VerifyExistingCrx(string crxPath)
    {
        if (!File.Exists(crxPath)) return (false, $"Arquivo não encontrado: {crxPath}");
        try
        {
            return VerifyGeneratedCrx(File.ReadAllBytes(crxPath));
        }
        catch (Exception ex)
        {
            return (false, $"Erro ao ler o arquivo: {ex.Message}");
        }
    }

    // ---------- Geração do .crx (formato CRX3) ----------

    private static bool NeedsRegeneration(string extensionFolderPath, string crxPath, string signingKeyPath)
    {
        if (!File.Exists(crxPath)) return true;

        var crxTime = File.GetLastWriteTimeUtc(crxPath);

        // Se a CHAVE mudou depois do último .crx gerado (ex: usuário seguiu
        // as instruções de "perdi a chave, gerei outra" do LEIA-ME.txt), o
        // .crx antigo está assinado com a chave errada — precisa regenerar.
        if (File.Exists(signingKeyPath) && File.GetLastWriteTimeUtc(signingKeyPath) > crxTime)
        {
            Logger.Log("ExtensionInstaller: a chave de assinatura é mais nova que o .crx atual — regerando.");
            return true;
        }

        return SourceFiles(extensionFolderPath).Any(f => File.GetLastWriteTimeUtc(f) > crxTime);
    }

    private static IEnumerable<string> SourceFiles(string extensionFolderPath) =>
        Directory.GetFiles(extensionFolderPath, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("extension.crx", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).Equals("update.xml", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gera um .crx assinado (CRX3) a partir da pasta da extensão e da chave
    /// privada, sem depender do binário do Chrome, e autoverifica o
    /// resultado antes de aceitar como válido.
    /// </summary>
    private static bool GenerateSignedCrx(string extensionFolderPath, string privateKeyPemPath, string crxOutputPath)
    {
        if (!File.Exists(privateKeyPemPath))
        {
            Logger.Log($"Chave de assinatura não encontrada em {privateKeyPemPath} — não deu pra gerar o .crx automaticamente.");
            return false;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(privateKeyPemPath));

            var zipBytes = BuildExtensionZip(extensionFolderPath);

            var pubKeyDer = rsa.ExportSubjectPublicKeyInfo();
            var crxId = SHA256.HashData(pubKeyDer)[..16];

            // SignedData { crx_id = 1 } serializado em protobuf.
            var signedData = EncodeLengthDelimited(1, crxId);

            // Contexto de assinatura exigido pelo formato CRX3.
            var signature = SignCrxContext(rsa, signedData, zipBytes);

            // AsymmetricKeyProof { public_key = 1, signature = 2 }
            var asymmetricKeyProof = EncodeLengthDelimited(1, pubKeyDer)
                .Concat(EncodeLengthDelimited(2, signature)).ToArray();

            // CrxFileHeader { sha256_with_rsa = 2 (repeated), signed_header_data = 10000 }
            var crxFileHeader = EncodeLengthDelimited(2, asymmetricKeyProof)
                .Concat(EncodeLengthDelimited(10000, signedData)).ToArray();

            using var output = new MemoryStream();
            output.Write(System.Text.Encoding.ASCII.GetBytes("Cr24"));
            output.Write(BitConverter.GetBytes((uint)3));
            output.Write(BitConverter.GetBytes((uint)crxFileHeader.Length));
            output.Write(crxFileHeader);
            output.Write(zipBytes);

            var finalBytes = output.ToArray();

            // Autoverificação: relê o que acabamos de montar do zero, do
            // mesmo jeito que o Chrome leria, e confirma matematicamente que
            // a assinatura bate. Só escreve o arquivo se passar.
            var (verifyOk, verifyMessage) = VerifyGeneratedCrx(finalBytes);
            Logger.Log($"Autoverificação do .crx: {verifyMessage}");
            if (!verifyOk)
            {
                return false;
            }

            File.WriteAllBytes(crxOutputPath, finalBytes);
            Logger.Log($"extension.crx gerado e autoverificado com sucesso ({finalBytes.Length} bytes) em {crxOutputPath}.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogException("ExtensionInstaller.GenerateSignedCrx", ex);
            return false;
        }
    }

    private static byte[] SignCrxContext(RSA rsa, byte[] signedData, byte[] zipBytes)
    {
        var context = new List<byte>();
        context.AddRange(System.Text.Encoding.ASCII.GetBytes("CRX3 SignedData"));
        context.Add(0);
        context.AddRange(BitConverter.GetBytes((uint)signedData.Length));
        context.AddRange(signedData);
        context.AddRange(zipBytes);
        return rsa.SignData(context.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    /// <summary>
    /// Relê um .crx do zero (parseando o protobuf manualmente, do mesmo
    /// jeito que o Chrome faria) e confirma: (1) magic/versão corretos,
    /// (2) a assinatura RSA bate com o conteúdo real, (3) o crx_id bate com
    /// a chave pública embutida. Não prova que o Chrome vai aceitar — prova
    /// que o arquivo está estruturalmente e matematicamente correto.
    /// </summary>
    private static (bool Ok, string Message) VerifyGeneratedCrx(byte[] crxBytes)
    {
        try
        {
            if (crxBytes.Length < 12) return (false, "arquivo curto demais.");

            var magic = System.Text.Encoding.ASCII.GetString(crxBytes, 0, 4);
            if (magic != "Cr24") return (false, $"magic bytes incorretos ('{magic}').");

            var version = BitConverter.ToUInt32(crxBytes, 4);
            if (version != 3) return (false, $"versão {version} inesperada (esperava 3).");

            var headerSize = BitConverter.ToUInt32(crxBytes, 8);
            if (12 + headerSize > crxBytes.Length) return (false, "header_size maior que o arquivo inteiro.");

            var headerBytes = crxBytes[12..(int)(12 + headerSize)];
            var zipBytes = crxBytes[(int)(12 + headerSize)..];

            byte[]? proofBytes = null;
            byte[]? signedHeaderData = null;

            var pos = 0;
            while (pos < headerBytes.Length)
            {
                var (fieldNum, wireType, afterTag) = ReadTag(headerBytes, pos);
                if (wireType != 2) return (false, $"wire type {wireType} inesperado no header (campo {fieldNum}).");
                var (len, afterLen) = ReadVarint(headerBytes, afterTag);
                var fieldData = headerBytes[afterLen..(afterLen + (int)len)];
                pos = afterLen + (int)len;

                if (fieldNum == 2) proofBytes = fieldData;
                else if (fieldNum == 10000) signedHeaderData = fieldData;
            }

            if (proofBytes == null) return (false, "campo AsymmetricKeyProof (2) ausente.");
            if (signedHeaderData == null) return (false, "campo signed_header_data (10000) ausente.");

            byte[]? pubKey = null;
            byte[]? signature = null;
            pos = 0;
            while (pos < proofBytes.Length)
            {
                var (fieldNum, _, afterTag) = ReadTag(proofBytes, pos);
                var (len, afterLen) = ReadVarint(proofBytes, afterTag);
                var fieldData = proofBytes[afterLen..(afterLen + (int)len)];
                pos = afterLen + (int)len;

                if (fieldNum == 1) pubKey = fieldData;
                else if (fieldNum == 2) signature = fieldData;
            }

            if (pubKey == null) return (false, "chave pública ausente no AsymmetricKeyProof.");
            if (signature == null) return (false, "assinatura ausente no AsymmetricKeyProof.");

            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(pubKey, out _);

            var context = new List<byte>();
            context.AddRange(System.Text.Encoding.ASCII.GetBytes("CRX3 SignedData"));
            context.Add(0);
            context.AddRange(BitConverter.GetBytes((uint)signedHeaderData.Length));
            context.AddRange(signedHeaderData);
            context.AddRange(zipBytes);

            var signatureValid = rsa.VerifyData(context.ToArray(), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (!signatureValid) return (false, "a assinatura RSA NÃO bate com o conteúdo — pacote corrompido.");

            var (_, _, crxIdAfterTag) = ReadTag(signedHeaderData, 0);
            var (crxIdLen, crxIdAfterLen) = ReadVarint(signedHeaderData, crxIdAfterTag);
            var actualCrxId = signedHeaderData[crxIdAfterLen..(crxIdAfterLen + (int)crxIdLen)];
            var expectedCrxId = SHA256.HashData(pubKey)[..16];

            if (!actualCrxId.AsSpan().SequenceEqual(expectedCrxId))
                return (false, "crx_id não bate com a chave pública.");

            return (true, "OK — assinatura confere e crx_id bate com a chave pública. Pacote estruturalmente correto.");
        }
        catch (Exception ex)
        {
            return (false, $"exceção durante a verificação: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static (int fieldNum, int wireType, int newPos) ReadTag(byte[] data, int pos)
    {
        var (tag, newPos) = ReadVarint(data, pos);
        return ((int)(tag >> 3), (int)(tag & 0x7), newPos);
    }

    private static (ulong value, int newPos) ReadVarint(byte[] data, int pos)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return (result, pos);
    }

    private static byte[] BuildExtensionZip(string extensionFolderPath)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in SourceFiles(extensionFolderPath))
            {
                var relative = Path.GetRelativePath(extensionFolderPath, file).Replace('\\', '/');
                archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
            }
        }
        return ms.ToArray();
    }

    // ---------- Codificação protobuf mínima (varint + length-delimited) ----------

    private static byte[] EncodeVarint(ulong value)
    {
        var bytes = new List<byte>();
        while (value >= 0x80)
        {
            bytes.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        bytes.Add((byte)value);
        return bytes.ToArray();
    }

    private static byte[] EncodeLengthDelimited(int fieldNumber, byte[] data)
    {
        var tag = EncodeVarint((ulong)((fieldNumber << 3) | 2)); // wire type 2 = length-delimited
        var len = EncodeVarint((ulong)data.Length);
        return tag.Concat(len).Concat(data).ToArray();
    }

    private static void WriteUpdateManifest(string updateXmlPath, string crxPath)
    {
        var crxUrl = new Uri(crxPath).AbsoluteUri;
        var xml = $"""
            <?xml version='1.0' encoding='UTF-8'?>
            <gupdate xmlns='http://www.google.com/update2/response' protocol='2.0'>
              <app appid='{ExtensionId}'>
                <updatecheck codebase='{crxUrl}' version='1.0' />
              </app>
            </gupdate>
            """;
        try
        {
            File.WriteAllText(updateXmlPath, xml);
        }
        catch (Exception ex)
        {
            Logger.LogException("ExtensionInstaller.WriteUpdateManifest", ex);
        }
    }
}
