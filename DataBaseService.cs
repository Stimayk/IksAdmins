using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Dapper;
using IksAdminApi;
using Microsoft.Extensions.Localization;
using MySqlConnector;

namespace IksAdmins
{
    public class DataBaseService(IStringLocalizer localizer)
    {
        private readonly string _connectionString = AdminModule.Api.DbConnectionString;

        public async Task ReloadCacheAsync()
        {
            await LoadAdminDataCacheAsync();
        }

        public class AdminData
        {
            public string? Auth { get; set; }
            public string? Contact { get; set; }
            public int Likes { get; set; }
            public int Dislikes { get; set; }
        }

        public Dictionary<string, AdminData> _adminDataCache = [];

        private async Task<MySqlConnection> GetOpenConnectionAsync()
        {
            MySqlConnection connection = new(_connectionString);
            await connection.OpenAsync();

            return connection;
        }

        /* 
            This method iterates through a list of predefined tables and checks if each table exists in the database.
            If a table doesn't exist, it creates the table using the corresponding SQL query.
            It is recommended not to use files for SQL queries as it can pose security risks.
        */
        public async Task TestAndCheckDataBaseTableAsync()
        {

            await using MySqlConnection connection = await GetOpenConnectionAsync();

            await using MySqlTransaction transaction = await connection.BeginTransactionAsync();

            try
            {
                string[] tables = ["iks_adminslist", "iks_adminslist_users"];
                foreach (string table in tables)
                {
                    bool tableExists = await connection.QueryFirstOrDefaultAsync<string>(
                        $"SHOW TABLES LIKE @table;", new { table }, transaction: transaction) != null;

                    if (!tableExists)
                    {
                        string createTableQuery = table switch
                        {
                            "iks_adminslist" => @"
        CREATE TABLE IF NOT EXISTS `iks_adminslist` (
            `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
            `auth` VARCHAR(32) NOT NULL UNIQUE,
            `contact` VARCHAR(32),
            `likes` INT NOT NULL DEFAULT '0',
            `dislikes` INT NOT NULL DEFAULT '0'
        ) ENGINE=InnoDB CHARSET=utf8 COLLATE utf8_general_ci;",

                            "iks_adminslist_users" => @"
        CREATE TABLE IF NOT EXISTS `iks_adminslist_users` (
            `id` INT AUTO_INCREMENT PRIMARY KEY,
            `user` VARCHAR(32) NOT NULL,
            `admin` VARCHAR(32) NOT NULL,
            `isput` INT NOT NULL,
            UNIQUE KEY `user_admin_vote` (`user`, `admin`)
        ) ENGINE=InnoDB CHARSET=utf8 COLLATE utf8_general_ci;",

                            _ => throw new InvalidOperationException($"Unknown table: {table}")
                        };

                        _ = await connection.ExecuteAsync(createTableQuery, transaction: transaction);
                    }
                }

                await transaction.CommitAsync();
                await LoadAdminDataCacheAsync();
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        private async Task LoadAdminDataCacheAsync()
        {
            const string query = "SELECT auth AS Auth, contact AS Contact, likes AS Likes, dislikes AS Dislikes FROM iks_adminslist";

            await using MySqlConnection connection = await GetOpenConnectionAsync();
            IEnumerable<AdminData> admins = await connection.QueryAsync<AdminData>(query);
            _adminDataCache = admins
                .Where(admin => admin.Auth != null)
                .ToDictionary(admin => admin.Auth!, admin => admin);
        }

        public (int Likes, int Dislikes)? GetPlayerRep(string steamId)
        {
            return _adminDataCache.TryGetValue(steamId, out AdminData? data)
                ? (data.Likes, data.Dislikes)
                : null;
        }

        public async Task<int?> GetPlayerAccessRepAsync(string userSteamID, string adminSteamID)
        {
            const string query = "SELECT isput FROM iks_adminslist_users WHERE admin = @AdminSteamID and user = @UserSteamID";
            return await ExecuteSqlAsync<int?>(query, new { AdminSteamID = adminSteamID, UserSteamID = userSteamID });
        }

        public string? GetAdminContact(string SteamID)
        {
            return _adminDataCache.TryGetValue(SteamID, out AdminData? data)
                ? data.Contact
                : null;
        }

        public async Task UpdateAdminDataCacheAsync(string steamId)
        {
            const string query = "SELECT auth AS Auth, contact AS Contact, likes AS Likes, dislikes AS Dislikes FROM iks_adminslist WHERE auth = @SteamId";
            await using MySqlConnection connection = await GetOpenConnectionAsync();
            AdminData? adminData = await connection.QueryFirstOrDefaultAsync<AdminData>(query, new { SteamId = steamId });
            if (adminData != null)
            {
                _adminDataCache[steamId] = adminData;
            }
            else
            {
                _ = _adminDataCache.Remove(steamId);
            }
        }

        public async Task SetReputationAsync(CCSPlayerController player, Admin admin, bool isLike)
        {
            string? steamIdUser = player.AuthorizedSteamID?.SteamId64.ToString();
            if (string.IsNullOrEmpty(steamIdUser))
            {
                return;
            }

            string steamIdAdmin = admin.SteamId;

            await using MySqlConnection connection = await GetOpenConnectionAsync();
            await using MySqlTransaction transaction = await connection.BeginTransactionAsync();

            string checkQuery = "SELECT isput FROM iks_adminslist_users WHERE `user` = @User AND `admin` = @Admin;";
            int? currentIspu = await connection.QueryFirstOrDefaultAsync<int?>(checkQuery, new
            {
                User = steamIdUser,
                Admin = steamIdAdmin
            }, transaction);

            int newIspu = isLike ? 1 : 0;

            if (currentIspu == newIspu)
            {
                await transaction.RollbackAsync();
                Server.NextFrame(() => { AdminUtils.Print(player, localizer["rep.vote.already", admin.CurrentName]); });
                return;
            }

            string updateRepQuery = isLike
                ? currentIspu == 0
                    ? "UPDATE iks_adminslist SET likes = likes + 1, dislikes = GREATEST(0, dislikes - 1) WHERE auth = @Admin;"
                    : "UPDATE iks_adminslist SET likes = likes + 1 WHERE auth = @Admin;"
                : currentIspu == 1
                    ? "UPDATE iks_adminslist SET dislikes = dislikes + 1, likes = GREATEST(0, likes - 1) WHERE auth = @Admin;"
                    : "UPDATE iks_adminslist SET dislikes = dislikes + 1 WHERE auth = @Admin;";
            _ = await connection.ExecuteAsync(updateRepQuery, new { Admin = steamIdAdmin }, transaction);

            string updateUserQuery = @"
            INSERT INTO iks_adminslist_users (`user`, `admin`, `isput`)
            VALUES (@User, @Admin, @NewIspu)
            ON DUPLICATE KEY UPDATE `isput` = @NewIspu;";

            _ = await connection.ExecuteAsync(updateUserQuery, new
            {
                User = steamIdUser,
                Admin = steamIdAdmin,
                NewIspu = newIspu
            }, transaction);

            await transaction.CommitAsync();
            await UpdateAdminDataCacheAsync(steamIdAdmin);
            Server.NextFrame(() =>
            {
                string feedbackMessage = isLike
                    ? localizer["rep.vote.like", admin.CurrentName]
                    : localizer["rep.vote.dislike", admin.CurrentName];

                string notifyMessage = isLike
                    ? localizer["rep.notify.received.like", player.PlayerName]
                    : localizer["rep.notify.received.dislike", player.PlayerName];

                if (player?.IsValid ?? false)
                {
                    AdminUtils.Print(player, feedbackMessage);
                }

                if (admin.Controller != null && admin.Controller.IsValid)
                {
                    AdminUtils.Print(admin.Controller, notifyMessage);
                }
            });
        }

        public async Task SetContactAsync(ulong steamId, string contact)
        {
            await using MySqlConnection connection = await GetOpenConnectionAsync();
            await using MySqlTransaction transaction = await connection.BeginTransactionAsync();

            const string query = "UPDATE iks_adminslist SET contact = @Contact WHERE auth = @SteamID";
            _ = await connection.ExecuteAsync(query, new { Contact = contact, SteamID = steamId }, transaction);

            await transaction.CommitAsync();
            await UpdateAdminDataCacheAsync(steamId.ToString());
        }

        public async Task AddAdminInListAsync(string steamId)
        {
            await using MySqlConnection connection = await GetOpenConnectionAsync();
            await using MySqlTransaction transaction = await connection.BeginTransactionAsync();

            const string checkQuery = "SELECT COUNT(1) FROM `iks_adminslist` WHERE `auth` = @SteamID;";
            bool exists = await connection.ExecuteScalarAsync<bool>(checkQuery, new { SteamID = steamId }, transaction);

            if (exists)
            {
                await transaction.RollbackAsync();
                return;
            }

            const string insertQuery = "INSERT INTO `iks_adminslist` (`auth`, `contact`, `likes`, `dislikes`) VALUES (@SteamID, NULL, 0, 0);";
            _ = await connection.ExecuteAsync(insertQuery, new { SteamID = steamId }, transaction);

            await transaction.CommitAsync();
            _adminDataCache[steamId] = new AdminData
            {
                Auth = steamId,
                Contact = null,
                Likes = 0,
                Dislikes = 0
            };
        }

        public async Task<T?> ExecuteSqlAsync<T>(string sql, object param)
        {
            await using MySqlConnection connection = await GetOpenConnectionAsync();

            IEnumerable<T> result = await connection.QueryAsync<T>(sql, param);

            return !result.Any() ? default : result.FirstOrDefault();
        }
    }
}