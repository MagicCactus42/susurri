using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Kademlia.Protocol;
using Susurri.Modules.DHT.Core.Network;
using Susurri.Modules.DHT.Core.Onion;
using Susurri.Modules.DHT.Core.Services;
using Susurri.Modules.IAM.Core.Abstractions;
using Susurri.Modules.IAM.Core.Crypto;

namespace Susurri.GUI.Services;

public sealed class AppSession : IAsyncDisposable
{
    private const string IdentityDomain = "susurri-identity-v1:";

    private readonly ICryptoKeyGenerator _keyGenerator;
    private readonly ICredentialsCache? _credentialsCache;
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;

    public string? Username { get; private set; }
    public ChatService? Chat { get; private set; }
    public ConversationStore? Conversations { get; private set; }

    public bool IsLoggedIn => Chat != null;

    public event Action? Changed;

    public AppSession(
        ICryptoKeyGenerator keyGenerator,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        ICredentialsCache? credentialsCache = null)
    {
        _keyGenerator = keyGenerator;
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _credentialsCache = credentialsCache;
    }

    public bool CacheExists => _credentialsCache?.Exists() == true;

    public (string Username, string Passphrase)? TryLoadCached(string pin)
    {
        if (_credentialsCache == null)
            return null;
        try
        {
            return _credentialsCache.Load(pin);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveCacheAsync(string username, string passphrase, string pin)
    {
        if (_credentialsCache != null)
            await _credentialsCache.SaveAsync(username, passphrase, pin);
    }

    public void ClearCache() => _credentialsCache?.Clear();

    public string GeneratePassphrase(int wordCount) => _keyGenerator.GeneratePassphrase(wordCount);

    public IReadOnlyList<string> ActiveSeeds { get; private set; } = Array.Empty<string>();

    public async Task LoginAsync(string username, string passphrase, int port,
        IReadOnlyList<string>? bootstrapNodes = null, IProgress<string>? progress = null)
    {
        if (IsLoggedIn)
            throw new InvalidOperationException($"Already online as '{Username}'.");

        progress?.Report("deriving identity keys — PBKDF2-SHA256 · 600 000 iterations");
        var salt = DeriveSalt(username);
        var keyPair = await Task.Run(() => _keyGenerator.GenerateKeyPair(passphrase, salt));
        var localStoreKey = keyPair.LocalStoreKey;

        var options = new ChatNodeOptions(
            EnableUdp: _configuration.GetValue("DHT:Nat:Enabled", true),
            UseStun: _configuration.GetValue("DHT:Nat:UseStun", false),
            NetworkId: ParseNetworkId(_configuration["DHT:NetworkId"]),
            PublicEndpoint: ParseEndpoint(_configuration["DHT:Nat:PublicEndpoint"]),
            AllowLoopback: _configuration.GetValue("DHT:AllowLoopback", false));

        var chat = new ChatService(
            keyPair.EncryptionKey,
            _loggerFactory.CreateLogger<ChatService>(),
            _loggerFactory.CreateLogger<KademliaDhtNode>(),
            _loggerFactory.CreateLogger<OnionRouter>(),
            _loggerFactory.CreateLogger<RelayService>(),
            _loggerFactory.CreateLogger<ConnectionManager>(),
            keyPair.SigningKey,
            options,
            keyPair.LocalStoreKey,
            _loggerFactory.CreateLogger<FileTransferService>());

        var seeds = bootstrapNodes is { Count: > 0 } ? bootstrapNodes.Distinct().ToList() : Seeds();

        try
        {
            progress?.Report("joining the network — dht bootstrap · onion setup");
            await chat.StartAsync(port, username, seeds);
        }
        catch
        {
            await chat.DisposeAsync();
            throw;
        }

        var history = localStoreKey != null
            ? new GuiHistoryStore(localStoreKey, chat.LocalPublicKey)
            : null;

        Chat = chat;
        Username = username;
        ActiveSeeds = seeds;
        Conversations = new ConversationStore(chat, username, history);
        Changed?.Invoke();
    }

    public async Task LogoutAsync()
    {
        Conversations?.Dispose();
        Conversations = null;
        ActiveSeeds = Array.Empty<string>();
        if (Chat != null)
        {
            await Chat.DisposeAsync();
            Chat = null;
        }
        Username = null;
        Changed?.Invoke();
    }

    public IReadOnlyList<string> Seeds()
    {
        var configured = _configuration.GetSection("DHT:BootstrapNodes").Get<string[]>() ?? Array.Empty<string>();
        return configured
            .Select(ParseEndpoint)
            .Where(e => e != null)
            .Select(e => e!.ToString())
            .Distinct()
            .ToList();
    }

    public static byte[] DeriveSalt(string username)
        => SHA256.HashData(Encoding.UTF8.GetBytes(IdentityDomain + username.Trim().ToLowerInvariant()));

    public static string? NormalizeSeed(string value) => ParseEndpoint(value)?.ToString();

    private static IPEndPoint? ParseEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        var text = endpoint.Trim();
        var lastColon = text.LastIndexOf(':');
        if (lastColon <= 0)
            return null;

        var host = text[..lastColon];
        var portText = text[(lastColon + 1)..];
        if (IPAddress.TryParse(host, out var ip) &&
            int.TryParse(portText, out var port) && port > 0 && port <= 65535)
        {
            return new IPEndPoint(ip, port);
        }

        return null;
    }

    private static uint ParseNetworkId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return KademliaMessage.DefaultNetworkId;

        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var hex))
            return hex;

        if (uint.TryParse(text, out var dec))
            return dec;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash);
    }

    public async ValueTask DisposeAsync()
    {
        await LogoutAsync();
    }
}
