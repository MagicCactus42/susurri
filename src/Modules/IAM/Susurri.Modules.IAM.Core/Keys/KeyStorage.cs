using NSec.Cryptography;
using Susurri.Modules.IAM.Core.Abstractions;

namespace Susurri.Modules.IAM.Core.Keys;

public class KeyStorage : IKeyStorage
{
    private readonly string _filePath;
    private readonly SignatureAlgorithm _signatureAlgorithm = SignatureAlgorithm.Ed25519;

    public KeyStorage()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appDirectory = Path.Combine(appData, "Susurri");
        Directory.CreateDirectory(appDirectory);
        
        _filePath = Path.Combine(appDirectory, "keys.txt");
    }
    
    
    public void Save(Key privateKey)
    {
        byte[] rawKey = privateKey.Export(KeyBlobFormat.RawPrivateKey);
        File.WriteAllBytes(_filePath, rawKey);
    }

    public Key Load()
    {
        if (!File.Exists(_filePath))
            return null;

        byte[] rawKey = File.ReadAllBytes(_filePath);
        return Key.Import(_signatureAlgorithm, rawKey, KeyBlobFormat.RawPrivateKey);
    }

    public void Delete()
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);
    }
}