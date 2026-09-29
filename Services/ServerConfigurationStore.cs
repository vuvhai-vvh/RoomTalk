using System.IO;
using System.Text.Json;
using RoomTalk.Models;

namespace RoomTalk.Services;

internal sealed class ServerConfigurationStore
{
    private readonly object _sync = new();
    private readonly string _filePath;
    private ServerConfigurationData _data;

    public ServerConfigurationStore()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RoomTalk",
            "Server");
        Directory.CreateDirectory(root);
        _filePath = Path.Combine(root, "server-data.json");
        _data = LoadOrCreate();
    }

    public int Port
    {
        get { lock (_sync) return _data.Port; }
        set
        {
            lock (_sync)
            {
                _data.Port = Math.Clamp(value, 1, 65535);
                SaveUnsafe();
            }
        }
    }

    public IReadOnlyList<ServerAccountItem> GetAccountItems()
    {
        lock (_sync)
        {
            return _data.Accounts
                .Select(account => new ServerAccountItem
                {
                    Username = account.Username,
                    Role = account.Role,
                    Enabled = account.Enabled
                })
                .OrderBy(account => account.Role)
                .ThenBy(account => account.Username, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public IReadOnlyList<ServerAccountRecord> GetCommunicationAccounts()
    {
        lock (_sync)
        {
            return _data.Accounts
                .Where(account => account.Role != AccountRole.Server)
                .Select(CloneAccount)
                .OrderBy(account => account.Username, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public ServerAccountRecord? FindAccount(string username)
    {
        lock (_sync)
        {
            ServerAccountRecord? account = _data.Accounts.FirstOrDefault(item =>
                item.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase));
            return account is null ? null : CloneAccount(account);
        }
    }

    public ServerAccountRecord? Authenticate(string username, string password)
    {
        lock (_sync)
        {
            ServerAccountRecord? account = _data.Accounts.FirstOrDefault(item =>
                item.Enabled && item.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase));

            return account is not null && PasswordHasher.Verify(password, account.PasswordHash)
                ? CloneAccount(account)
                : null;
        }
    }

    public void UpsertAccount(
        string username,
        string? password,
        AccountRole role,
        bool enabled)
    {
        username = username.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException("Tên tài khoản không được để trống.");
        }

        if (username.Length > 50)
        {
            throw new InvalidOperationException("Tên tài khoản không được dài quá 50 ký tự.");
        }

        lock (_sync)
        {
            ServerAccountRecord? existing = _data.Accounts.FirstOrDefault(account =>
                account.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                if (string.IsNullOrWhiteSpace(password))
                {
                    throw new InvalidOperationException("Tài khoản mới phải có mật khẩu.");
                }

                if (role == AccountRole.Server && _data.Accounts.Any(account => account.Role == AccountRole.Server))
                {
                    throw new InvalidOperationException("Hệ thống chỉ sử dụng một tài khoản máy chủ.");
                }

                _data.Accounts.Add(new ServerAccountRecord
                {
                    Username = username,
                    PasswordHash = PasswordHasher.Hash(password),
                    Role = role,
                    AssignedRoom = string.Empty,
                    Enabled = enabled
                });
            }
            else
            {
                if (existing.Role == AccountRole.Server && role != AccountRole.Server)
                {
                    throw new InvalidOperationException("Không thể đổi tài khoản máy chủ sang vai trò khác.");
                }

                if (existing.Role != AccountRole.Server && role == AccountRole.Server)
                {
                    throw new InvalidOperationException("Không thể đổi tài khoản sử dụng thành tài khoản máy chủ.");
                }

                existing.Role = role;
                existing.AssignedRoom = string.Empty;
                existing.Enabled = enabled;
                if (!string.IsNullOrWhiteSpace(password))
                {
                    existing.PasswordHash = PasswordHasher.Hash(password);
                }
            }

            _data.Version = 2;
            _data.Rooms.Clear();
            SaveUnsafe();
        }
    }

    public void DeleteAccount(string username)
    {
        lock (_sync)
        {
            ServerAccountRecord? account = _data.Accounts.FirstOrDefault(item =>
                item.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                return;
            }

            if (account.Role == AccountRole.Server)
            {
                throw new InvalidOperationException("Không thể xóa tài khoản máy chủ.");
            }

            _data.Accounts.Remove(account);
            SaveUnsafe();
        }
    }

    private ServerConfigurationData LoadOrCreate()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                string json = File.ReadAllText(_filePath);
                ServerConfigurationData? loaded = JsonSerializer.Deserialize<ServerConfigurationData>(json);
                if (loaded is not null && loaded.Accounts.Count > 0)
                {
                    MigrateToVersion2(loaded);
                    _data = loaded;
                    SaveUnsafe();
                    return loaded;
                }
            }
        }
        catch
        {
            // Tạo lại cấu hình mặc định khi file lỗi.
        }

        var data = new ServerConfigurationData
        {
            Version = 2,
            Port = 5000,
            Rooms = [],
            Accounts =
            [
                new ServerAccountRecord
                {
                    Username = "server",
                    PasswordHash = PasswordHasher.Hash("123"),
                    Role = AccountRole.Server,
                    Enabled = true
                },
                new ServerAccountRecord
                {
                    Username = "admin",
                    PasswordHash = PasswordHasher.Hash("123"),
                    Role = AccountRole.Admin,
                    Enabled = true
                },
                new ServerAccountRecord
                {
                    Username = "user01",
                    PasswordHash = PasswordHasher.Hash("123"),
                    Role = AccountRole.User,
                    Enabled = true
                },
                new ServerAccountRecord
                {
                    Username = "user02",
                    PasswordHash = PasswordHasher.Hash("123"),
                    Role = AccountRole.User,
                    Enabled = true
                }
            ]
        };

        _data = data;
        SaveUnsafe();
        return data;
    }

    private static void MigrateToVersion2(ServerConfigurationData data)
    {
        data.Version = 2;
        data.Rooms ??= [];
        data.Accounts ??= [];
        data.Rooms.Clear();

        foreach (ServerAccountRecord account in data.Accounts)
        {
            account.Username = account.Username.Trim();
            account.AssignedRoom = string.Empty;
        }
    }

    private void SaveUnsafe()
    {
        string json = JsonSerializer.Serialize(_data, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        string temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _filePath, true);
    }

    private static ServerAccountRecord CloneAccount(ServerAccountRecord account) => new()
    {
        Username = account.Username,
        PasswordHash = account.PasswordHash,
        Role = account.Role,
        AssignedRoom = string.Empty,
        Enabled = account.Enabled
    };
}
