using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AI.SmartStandards.InteractivePrompting {

  /// <summary>
  /// Protects (encrypts) and unprotects secrets like PATs/tokens before they are persisted.
  /// Hosts can provide their own implementation (for example based on DPAPI or a vault).
  /// </summary>
  public interface ISecretProtector {

    /// <summary>
    /// Returns a protected (encrypted) representation of the given plain secret.
    /// </summary>
    string Protect(string plainSecret);

    /// <summary>
    /// Returns the plain secret of a value which was created by 'Protect'.
    /// </summary>
    string Unprotect(string protectedSecret);

    /// <summary>
    /// Returns true if the given persisted value was created by this protector (otherwise it is treated as
    /// legacy plain text, which will be protected on the next write).
    /// </summary>
    bool IsProtected(string persistedValue);

  }

  /// <summary>
  /// Portable default 'ISecretProtector' (no platform interop): AES-256-CBC encryption with HMAC-SHA256
  /// authentication (encrypt-then-MAC). The random key is created on first use within a machine-local key file
  /// (default: '%LOCALAPPDATA%\SmartStandards\AiSupport\secrets.key'), which is intentionally NOT located beside
  /// the (possibly roaming or shared) file containing the protected values.
  /// </summary>
  public class KeyFileSecretProtector : ISecretProtector {

    private const string _Prefix = "enc1:";

    private const int _KeyLength = 32;

    private const int _IvLength = 16;

    private const int _MacLength = 32;

    private readonly string _KeyFileFullName;

    private readonly object _SyncRoot = new object();

    private byte[] _EncryptionKey;

    private byte[] _MacKey;

    /// <summary>
    /// Creates the protector using the default key file location.
    /// </summary>
    public KeyFileSecretProtector() : this(GetDefaultKeyFileFullName()) {
    }

    /// <summary>
    /// Creates the protector using an explicit key file location (is created on first use if missing).
    /// </summary>
    public KeyFileSecretProtector(string keyFileFullName) {

      if (string.IsNullOrWhiteSpace(keyFileFullName)) {
        throw new ArgumentNullException(nameof(keyFileFullName));
      }

      _KeyFileFullName = keyFileFullName;

    }

    /// <summary>
    /// Returns '%LOCALAPPDATA%\SmartStandards\AiSupport\secrets.key'.
    /// </summary>
    public static string GetDefaultKeyFileFullName() {

      string localDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

      return Path.Combine(localDirectory, "SmartStandards", "AiSupport", "secrets.key");
    }

    public string KeyFileFullName {
      get {
        return _KeyFileFullName;
      }
    }

    /// <summary>
    /// Returns true if the value carries the format prefix of this protector.
    /// </summary>
    public bool IsProtected(string persistedValue) {

      if (persistedValue == null) {
        return false;
      }

      return persistedValue.StartsWith(_Prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Encrypts the secret: 'enc1:' + base64(iv | ciphertext | hmac(iv | ciphertext)).
    /// </summary>
    public string Protect(string plainSecret) {

      if (plainSecret == null) {
        throw new ArgumentNullException(nameof(plainSecret));
      }

      this.EnsureKeys();

      byte[] plainBytes = Encoding.UTF8.GetBytes(plainSecret);
      byte[] iv = new byte[_IvLength];
      byte[] cipherBytes;

      using (RandomNumberGenerator random = RandomNumberGenerator.Create()) {
        random.GetBytes(iv);
      }

      using (Aes aes = Aes.Create()) {

        aes.Key = _EncryptionKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using (ICryptoTransform encryptor = aes.CreateEncryptor()) {
          cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        }

      }

      byte[] payload = new byte[_IvLength + cipherBytes.Length + _MacLength];
      Buffer.BlockCopy(iv, 0, payload, 0, _IvLength);
      Buffer.BlockCopy(cipherBytes, 0, payload, _IvLength, cipherBytes.Length);

      byte[] mac = this.ComputeMac(payload, _IvLength + cipherBytes.Length);
      Buffer.BlockCopy(mac, 0, payload, _IvLength + cipherBytes.Length, _MacLength);

      return _Prefix + Convert.ToBase64String(payload);
    }

    /// <summary>
    /// Decrypts a value created by 'Protect'. Throws a 'CryptographicException' if the value was manipulated
    /// or was protected with another key.
    /// </summary>
    public string Unprotect(string protectedSecret) {

      if (!this.IsProtected(protectedSecret)) {
        throw new ArgumentException("The value was not protected by this protector.", nameof(protectedSecret));
      }

      this.EnsureKeys();

      byte[] payload = Convert.FromBase64String(protectedSecret.Substring(_Prefix.Length));

      if (payload.Length < _IvLength + 16 + _MacLength) {
        throw new CryptographicException("The protected value is truncated.");
      }

      int signedLength = payload.Length - _MacLength;
      byte[] expectedMac = this.ComputeMac(payload, signedLength);

      if (!FixedTimeEquals(expectedMac, payload, signedLength)) {
        throw new CryptographicException("The protected value is invalid or was protected with another key.");
      }

      byte[] iv = new byte[_IvLength];
      Buffer.BlockCopy(payload, 0, iv, 0, _IvLength);

      using (Aes aes = Aes.Create()) {

        aes.Key = _EncryptionKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using (ICryptoTransform decryptor = aes.CreateDecryptor()) {

          byte[] plainBytes = decryptor.TransformFinalBlock(payload, _IvLength, signedLength - _IvLength);

          return Encoding.UTF8.GetString(plainBytes);
        }

      }

    }

    private byte[] ComputeMac(byte[] buffer, int length) {

      using (HMACSHA256 hmac = new HMACSHA256(_MacKey)) {
        return hmac.ComputeHash(buffer, 0, length);
      }

    }

    private static bool FixedTimeEquals(byte[] expectedMac, byte[] payload, int macOffset) {

      int difference = 0;

      for (int i = 0; i < _MacLength; i++) {
        difference = difference | (expectedMac[i] ^ payload[macOffset + i]);
      }

      return (difference == 0);
    }

    /// <summary>
    /// Loads the key material from the key file or creates a new random key file.
    /// </summary>
    private void EnsureKeys() {

      lock (_SyncRoot) {

        if (_EncryptionKey != null) {
          return;
        }

        byte[] keyMaterial;

        if (File.Exists(_KeyFileFullName)) {

          keyMaterial = Convert.FromBase64String(File.ReadAllText(_KeyFileFullName).Trim());

          if (keyMaterial.Length != _KeyLength * 2) {
            throw new CryptographicException($"The key file '{_KeyFileFullName}' is invalid.");
          }

        }
        else {

          keyMaterial = new byte[_KeyLength * 2];

          using (RandomNumberGenerator random = RandomNumberGenerator.Create()) {
            random.GetBytes(keyMaterial);
          }

          string directory = Path.GetDirectoryName(_KeyFileFullName);

          if (!string.IsNullOrWhiteSpace(directory)) {
            Directory.CreateDirectory(directory);
          }

          File.WriteAllText(_KeyFileFullName, Convert.ToBase64String(keyMaterial));

        }

        byte[] encryptionKey = new byte[_KeyLength];
        byte[] macKey = new byte[_KeyLength];
        Buffer.BlockCopy(keyMaterial, 0, encryptionKey, 0, _KeyLength);
        Buffer.BlockCopy(keyMaterial, _KeyLength, macKey, 0, _KeyLength);

        _MacKey = macKey;
        _EncryptionKey = encryptionKey;

      }

    }

  }

}
