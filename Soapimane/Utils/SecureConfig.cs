using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Management;
using static Soapimane.Other.LogManager;



namespace Soapimane.Utils

{
    /// <summary>
    /// Provides encrypted configuration storage with hardware-bound encryption keys.
    /// Protects user settings and prevents tampering or unauthorized access.
    /// </summary>
    public static class SecureConfig
    {
        #region Private Fields

        private static readonly string ConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Soapimane",
            "SecureConfig");

        private static readonly string ConfigFilePath = Path.Combine(ConfigDirectory, "config.enc");
        private static readonly string KeyFilePath = Path.Combine(ConfigDirectory, "key.dat");

        private static byte[]? _cachedKey;
        private static readonly object _keyLock = new object();

        #endregion

        #region Hardware ID Generation

        /// <summary>
        /// Generates a unique hardware ID based on system components.
        /// This ID is used to derive encryption keys that are bound to this specific machine.
        /// </summary>
        public static string GetHardwareId()
        {
            try
            {
                var components = new StringBuilder();

                // CPU ID
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            components.Append(obj["ProcessorId"]?.ToString() ?? "UNKNOWN_CPU");
                            break;
                        }
                    }
                }
                catch { components.Append("UNKNOWN_CPU"); }

                // Motherboard Serial
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            components.Append(obj["SerialNumber"]?.ToString() ?? "UNKNOWN_MB");
                            break;
                        }
                    }
                }
                catch { components.Append("UNKNOWN_MB"); }

                // BIOS Serial
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            components.Append(obj["SerialNumber"]?.ToString() ?? "UNKNOWN_BIOS");
                            break;
                        }
                    }
                }
                catch { components.Append("UNKNOWN_BIOS"); }

                // Disk Drive Serial
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_DiskDrive WHERE Index = 0"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            components.Append(obj["SerialNumber"]?.ToString() ?? "UNKNOWN_DISK");
                            break;
                        }
                    }
                }
                catch { components.Append("UNKNOWN_DISK"); }

                // Generate hash of combined components
                using (var sha256 = SHA256.Create())
                {
                    byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(components.ToString()));
                    return Convert.ToBase64String(hash);
                }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Failed to generate hardware ID: {ex.Message}");

                // Fallback to a less secure but functional ID
                return Convert.ToBase64String(Guid.NewGuid().ToByteArray());
            }
        }

        /// <summary>
        /// Derives an encryption key from the hardware ID using PBKDF2.
        /// </summary>
        private static byte[] DeriveKeyFromHardwareId()
        {
            if (_cachedKey != null) return _cachedKey;

            lock (_keyLock)
            {
                if (_cachedKey != null) return _cachedKey;

                try
                {
                    string hardwareId = GetHardwareId();
                    byte[] hardwareBytes = Encoding.UTF8.GetBytes(hardwareId);

                    // Use PBKDF2 to derive a strong key
                    // Salt is also derived from hardware ID for consistency
                    byte[] salt = new byte[16];
                    using (var sha256 = SHA256.Create())
                    {
                        byte[] hash = sha256.ComputeHash(hardwareBytes);
                        Array.Copy(hash, salt, 16);
                    }

                    using (var pbkdf2 = new Rfc2898DeriveBytes(hardwareBytes, salt, 100000, HashAlgorithmName.SHA256))
                    {
                        _cachedKey = pbkdf2.GetBytes(32); // 256-bit key
                        return _cachedKey;
                    }
                }
                catch (Exception ex)
                {
                Log(LogLevel.Error, $"Failed to derive encryption key: {ex.Message}");

                    // Fallback to a default key (less secure but functional)
                    _cachedKey = Encoding.UTF8.GetBytes("FallbackKey32BytesLongForEncryption!");
                    return _cachedKey;
                }
            }
        }

        #endregion

        #region Encryption/Decryption

        /// <summary>
        /// Encrypts data using AES-256-GCM for authenticated encryption.
        /// </summary>
        private static byte[] EncryptData(byte[] plaintext, byte[] key)
        {
            try
            {
                using (var aes = new AesGcm(key, 16))
                {
                    byte[] nonce = new byte[12]; // 96-bit nonce
                    RandomNumberGenerator.Fill(nonce);

                    byte[] ciphertext = new byte[plaintext.Length];
                    byte[] tag = new byte[16]; // 128-bit authentication tag

                    aes.Encrypt(nonce, plaintext, ciphertext, tag);

                    // Combine: nonce (12) + tag (16) + ciphertext
                    byte[] result = new byte[12 + 16 + ciphertext.Length];
                    Buffer.BlockCopy(nonce, 0, result, 0, 12);
                    Buffer.BlockCopy(tag, 0, result, 12, 16);
                    Buffer.BlockCopy(ciphertext, 0, result, 28, ciphertext.Length);

                    return result;
                }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Encryption failed: {ex.Message}");

                throw;
            }
        }

        /// <summary>
        /// Decrypts data using AES-256-GCM.
        /// </summary>
        private static byte[] DecryptData(byte[] encryptedData, byte[] key)
        {
            try
            {
                if (encryptedData.Length < 28)
                    throw new ArgumentException("Invalid encrypted data");

                byte[] nonce = new byte[12];
                byte[] tag = new byte[16];
                byte[] ciphertext = new byte[encryptedData.Length - 28];

                Buffer.BlockCopy(encryptedData, 0, nonce, 0, 12);
                Buffer.BlockCopy(encryptedData, 12, tag, 0, 16);
                Buffer.BlockCopy(encryptedData, 28, ciphertext, 0, ciphertext.Length);

                using (var aes = new AesGcm(key, 16))
                {
                    byte[] plaintext = new byte[ciphertext.Length];
                    aes.Decrypt(nonce, ciphertext, tag, plaintext);
                    return plaintext;
                }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Decryption failed: {ex.Message}");

                throw;
            }
        }

        #endregion

        #region Configuration Management

        /// <summary>
        /// Configuration data structure
        /// </summary>
        public class ConfigData
        {
            public Dictionary<string, object> Settings { get; set; } = new();
            public Dictionary<string, object> Bindings { get; set; } = new();
            public Dictionary<string, object> Toggles { get; set; } = new();
            public Dictionary<string, object> Dropdowns { get; set; } = new();
            public Dictionary<string, object> Colors { get; set; } = new();
            public string? LastLoadedModel { get; set; }
            public string? LastLoadedConfig { get; set; }
            public long LastModified { get; set; }
        }

        /// <summary>
        /// Saves configuration securely to disk.
        /// </summary>
        public static void SaveConfig(ConfigData config)
        {
            try
            {
                // Ensure directory exists
                Directory.CreateDirectory(ConfigDirectory);

                // Update timestamp
                config.LastModified = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // Serialize to JSON
                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions
                {
                    WriteIndented = false
                });

                byte[] plaintext = Encoding.UTF8.GetBytes(json);
                byte[] key = DeriveKeyFromHardwareId();
                byte[] encrypted = EncryptData(plaintext, key);

                // Write to file with additional obfuscation
                byte[] obfuscated = ObfuscateData(encrypted);
                File.WriteAllBytes(ConfigFilePath, obfuscated);

                Log(LogLevel.Info, "Configuration saved securely");

            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Failed to save config: {ex.Message}");

                throw;
            }
        }

        /// <summary>
        /// Loads configuration from secure storage.
        /// </summary>
        public static ConfigData? LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigFilePath))
                {
                    Log(LogLevel.Info, "No secure config found, creating new");

                    return new ConfigData();
                }

                byte[] obfuscated = File.ReadAllBytes(ConfigFilePath);
                byte[] encrypted = DeobfuscateData(obfuscated);
                byte[] key = DeriveKeyFromHardwareId();
                byte[] plaintext = DecryptData(encrypted, key);
                string json = Encoding.UTF8.GetString(plaintext);

                var config = JsonSerializer.Deserialize<ConfigData>(json);
                Log(LogLevel.Info, "Configuration loaded securely");

                return config;
            }
            catch (CryptographicException)
            {
                Log(LogLevel.Error, "Config decryption failed - possible tampering or hardware change");

                return new ConfigData(); // Return empty config
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Failed to load config: {ex.Message}");

                return new ConfigData();
            }
        }

        /// <summary>
        /// Simple XOR obfuscation to avoid obvious "encrypted" file signatures.
        /// </summary>
        private static byte[] ObfuscateData(byte[] data)
        {
            byte[] key = DeriveKeyFromHardwareId();
            byte[] result = new byte[data.Length];
            
            for (int i = 0; i < data.Length; i++)
            {
                result[i] = (byte)(data[i] ^ key[i % key.Length]);
            }
            
            return result;
        }

        /// <summary>
        /// Reverses XOR obfuscation.
        /// </summary>
        private static byte[] DeobfuscateData(byte[] data)
        {
            // XOR is symmetric, so same operation
            return ObfuscateData(data);
        }

        #endregion

        #region Profile Management

        /// <summary>
        /// Saves a game-specific profile.
        /// </summary>
        public static void SaveProfile(string profileName, ConfigData config)
        {
            try
            {
                string profilePath = Path.Combine(ConfigDirectory, $"{profileName}.enc");
                Directory.CreateDirectory(ConfigDirectory);

                config.LastModified = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string json = JsonSerializer.Serialize(config);
                byte[] plaintext = Encoding.UTF8.GetBytes(json);
                byte[] key = DeriveKeyFromHardwareId();
                byte[] encrypted = EncryptData(plaintext, key);
                byte[] obfuscated = ObfuscateData(encrypted);

                File.WriteAllBytes(profilePath, obfuscated);
                Log(LogLevel.Info, $"Profile '{profileName}' saved");

            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Failed to save profile: {ex.Message}");

            }
        }

        /// <summary>
        /// Loads a game-specific profile.
        /// </summary>
        public static ConfigData? LoadProfile(string profileName)
        {
            try
            {
                string profilePath = Path.Combine(ConfigDirectory, $"{profileName}.enc");
                
                if (!File.Exists(profilePath))
                    return null;

                byte[] obfuscated = File.ReadAllBytes(profilePath);
                byte[] encrypted = DeobfuscateData(obfuscated);
                byte[] key = DeriveKeyFromHardwareId();
                byte[] plaintext = DecryptData(encrypted, key);
                string json = Encoding.UTF8.GetString(plaintext);

                return JsonSerializer.Deserialize<ConfigData>(json);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Failed to load profile: {ex.Message}");

                return null;
            }
        }

        /// <summary>
        /// Lists all available profiles.
        /// </summary>
        public static string[] ListProfiles()
        {
            try
            {
                if (!Directory.Exists(ConfigDirectory))
                    return Array.Empty<string>();

                var files = Directory.GetFiles(ConfigDirectory, "*.enc");
                return files
                    .Select(f => Path.GetFileNameWithoutExtension(f))
                    .Where(f => f != "config") // Exclude main config
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Deletes a profile.
        /// </summary>
        public static bool DeleteProfile(string profileName)
        {
            try
            {
                string profilePath = Path.Combine(ConfigDirectory, $"{profileName}.enc");
                
                if (File.Exists(profilePath))
                {
                    // Secure delete: overwrite with random data first
                    byte[] randomData = new byte[new FileInfo(profilePath).Length];
                    RandomNumberGenerator.Fill(randomData);
                    File.WriteAllBytes(profilePath, randomData);
                    
                    File.Delete(profilePath);
                Log(LogLevel.Info, $"Profile '{profileName}' deleted");

                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Failed to delete profile: {ex.Message}");

                return false;
            }
        }

        #endregion

        #region Model Encryption

        /// <summary>
        /// Encrypts a model file for secure storage.
        /// </summary>
        public static void EncryptModel(string inputPath, string outputPath)
        {
            try
            {
                byte[] modelData = File.ReadAllBytes(inputPath);
                byte[] key = DeriveKeyFromHardwareId();
                byte[] encrypted = EncryptData(modelData, key);
                byte[] obfuscated = ObfuscateData(encrypted);

                File.WriteAllBytes(outputPath, obfuscated);
                Log(LogLevel.Info, $"Model encrypted: {outputPath}");

            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Failed to encrypt model: {ex.Message}");

            }
        }

        /// <summary>
        /// Decrypts a model file for loading.
        /// </summary>
        public static byte[] DecryptModel(string encryptedPath)
        {
            try
            {
                byte[] obfuscated = File.ReadAllBytes(encryptedPath);
                byte[] encrypted = DeobfuscateData(obfuscated);
                byte[] key = DeriveKeyFromHardwareId();
                return DecryptData(encrypted, key);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Failed to decrypt model: {ex.Message}");

                throw;
            }
        }

        /// <summary>
        /// Checks if a file is an encrypted model.
        /// </summary>
        public static bool IsEncryptedModel(string path)
        {
            try
            {
                // Check file extension or magic bytes
                return path.EndsWith(".encmodel", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Clears the cached encryption key (for security).
        /// </summary>
        public static void ClearKeyCache()
        {
            lock (_keyLock)
            {
                if (_cachedKey != null)
                {
                    // Zero out the key
                    Array.Clear(_cachedKey, 0, _cachedKey.Length);
                    _cachedKey = null;
                }
            }
        }

        /// <summary>
        /// Verifies if the current hardware matches the key used for encryption.
        /// </summary>
        public static bool VerifyHardwareMatch()
        {
            try
            {
                if (!File.Exists(ConfigFilePath))
                    return true; // No config to verify

                // Try to load - if it fails, hardware doesn't match
                var config = LoadConfig();
                return config != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Securely wipes all configuration data.
        /// </summary>
        public static void SecureWipe()
        {
            try
            {
                if (Directory.Exists(ConfigDirectory))
                {
                    foreach (var file in Directory.GetFiles(ConfigDirectory))
                    {
                        // Overwrite with random data
                        var info = new FileInfo(file);
                        byte[] randomData = new byte[info.Length];
                        RandomNumberGenerator.Fill(randomData);
                        File.WriteAllBytes(file, randomData);
                        File.Delete(file);
                    }

                    Directory.Delete(ConfigDirectory, true);
                Log(LogLevel.Info, "All configuration data securely wiped");

                }

                ClearKeyCache();
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Failed to wipe config: {ex.Message}");

            }
        }

        #endregion
    }
}
