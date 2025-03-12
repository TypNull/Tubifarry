using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Tubifarry.Core.Utilities
{
    public interface IPluginSettings
    {
        T GetValue<T>(string key, T? defaultValue = default);
        void SetValue<T>(string key, T value);
        bool HasKey(string key);
        void RemoveKey(string key);
        void Save();
        void Load();
        event EventHandler<SettingChangedEventArgs> SettingChanged;
    }

    public class SettingChangedEventArgs : EventArgs
    {
        public string Key { get; }
        public object? OldValue { get; }
        public object? NewValue { get; }

        public SettingChangedEventArgs(string key, object? oldValue, object? newValue)
        {
            Key = key;
            OldValue = oldValue;
            NewValue = newValue;
        }
    }

    public class PluginSettings : IPluginSettings
    {
        private readonly string _settingsPath;
        private readonly ConcurrentDictionary<string, string> _settings;
        private readonly ReaderWriterLockSlim _lock;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly bool _autoSave;

        public event EventHandler<SettingChangedEventArgs>? SettingChanged;

        public PluginSettings(IAppFolderInfo appFolderInfo, bool autoSave = true)
        {
            _settingsPath = Path.Combine(appFolderInfo.GetPluginPath(), PluginInfo.Author, PluginInfo.Name, "settings.resx");
            _settings = new ConcurrentDictionary<string, string>();
            _lock = new ReaderWriterLockSlim();
            _autoSave = autoSave;

            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };

            Load();
        }

        public T GetValue<T>(string key, T? defaultValue = default)
        {
            _lock.EnterReadLock();
            try
            {
                return _settings.TryGetValue(key, out string? json) ? TryDeserialize(json, defaultValue)! : defaultValue!;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        private T? TryDeserialize<T>(string json, T? defaultValue)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(json, _jsonOptions) is T result ? result : defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }

        public void SetValue<T>(string key, T value)
        {
            _lock.EnterWriteLock();
            try
            {
                string json = JsonSerializer.Serialize(value, _jsonOptions);
                T? oldValue = _settings.TryGetValue(key, out string? oldJson) && oldJson != null
                    ? TryDeserialize<T>(oldJson, default)
                    : default;

                _settings[key] = json;

                OnSettingChanged(key, oldValue, value);

                if (_autoSave)
                    Save();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public bool HasKey(string key)
        {
            _lock.EnterReadLock();
            try
            {
                return _settings.ContainsKey(key);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public void RemoveKey(string key)
        {
            _lock.EnterWriteLock();
            try
            {
                if (_settings.TryRemove(key, out string? oldJson) && oldJson != null)
                {
                    object? oldValue = TryDeserialize<object>(oldJson, null);
                    OnSettingChanged(key, oldValue, null);

                    if (_autoSave)
                    {
                        Save();
                    }
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public void Save()
        {
            _lock.EnterReadLock();
            try
            {
                string? directory = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonSerializer.Serialize(_settings, _jsonOptions);
                string obfuscated = ObfuscateString(json);
                File.WriteAllText(_settingsPath, obfuscated);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public void Load()
        {
            if (!File.Exists(_settingsPath))
            {
                return;
            }

            _lock.EnterWriteLock();
            try
            {
                string obfuscated = File.ReadAllText(_settingsPath);
                string json = DeobfuscateString(obfuscated);
                Dictionary<string, string>? loadedSettings = JsonSerializer.Deserialize<Dictionary<string, string>>(json, _jsonOptions);

                _settings.Clear();
                if (loadedSettings != null)
                {
                    foreach (KeyValuePair<string, string> pair in loadedSettings)
                    {
                        _settings[pair.Key] = pair.Value;
                    }
                }
            }
            catch
            {
                _settings.Clear();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public int GetInt(string key, int defaultValue = 0) => GetValue(key, defaultValue);
        public bool GetBool(string key, bool defaultValue = false) => GetValue(key, defaultValue);
        public string GetString(string key, string defaultValue = "") => GetValue(key, defaultValue) ?? defaultValue;
        public double GetDouble(string key, double defaultValue = 0.0) => GetValue(key, defaultValue);

        public void SetValues<T>(Dictionary<string, T> values)
        {
            _lock.EnterWriteLock();
            try
            {
                values.ToList().ForEach(pair =>
                {
                    T? oldValue = _settings.TryGetValue(pair.Key, out string? oldJson) && oldJson != null
                        ? TryDeserialize<T>(oldJson, default)
                        : default;

                    _settings[pair.Key] = JsonSerializer.Serialize(pair.Value, _jsonOptions);
                    OnSettingChanged(pair.Key, oldValue, pair.Value);
                });

                if (_autoSave)
                    Save();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public void Clear()
        {
            _lock.EnterWriteLock();
            try
            {
                _settings.Clear();

                if (_autoSave)
                    Save();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        protected virtual void OnSettingChanged(string key, object? oldValue, object? newValue) =>
            SettingChanged?.Invoke(this, new SettingChangedEventArgs(key, oldValue, newValue));

        private static string ObfuscateString(string input) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(input).Select((b, i) =>
            {
                byte key = (byte)((i % 256) ^ 0x5F);
                return (byte)((b ^ key) + 1);
            }).ToArray());

        private static string DeobfuscateString(string input) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(input).Select((b, i) =>
            {
                byte key = (byte)((i % 256) ^ 0x5F);
                return (byte)((b - 1) ^ key);
            }).ToArray());
    }
}