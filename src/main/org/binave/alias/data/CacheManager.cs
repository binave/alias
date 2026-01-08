// Copyright (c) 2026 bin jin.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using org.binave.alias.entity;

namespace org.binave.alias.data;

/// <summary>缓存管理器</summary>
/// <remarks>
/// 使用 SQLite 嵌入式数据库缓存已解析的路径以提升性能。
/// 数据库文件: %USERPROFILE%\.alias.db
/// 缓存失效条件：
/// 1. 缓存数据库文件不存在
/// 2. 配置文件修改时间晚于缓存行修改时间 "update_at"
/// 3. 缓存的文件路径 "value" 不存在
/// </remarks>
internal static class CacheManager {
    /// <summary>获取数据库文件路径</summary>
    /// <returns>%USERPROFILE%\.alias.db</returns>
    private static string GetDatabasePath() {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".alias.db");
    }

    /// <summary>获取数据库连接字符串</summary>
    private static string GetConnectionString() {
        return $"Data Source={GetDatabasePath()};Mode=ReadWriteCreate;Cache=Shared";
    }

    /// <summary>获取文件的真实修改时间（解析软链接）</summary>
    private static DateTime GetRealLastWriteTimeUtc(string path) {
        var fileInfo = new FileInfo(path);
        // 如果是软链接，获取目标文件的修改时间
        if (fileInfo.LinkTarget != null) {
            string? target = fileInfo.ResolveLinkTarget(true)?.FullName;
            if (target != null && File.Exists(target)) {
                return File.GetLastWriteTimeUtc(target);
            }
        }
        return File.GetLastWriteTimeUtc(path);
    }

    /// <summary>检查缓存是否有效（比较 .alias 和 .alias.db 修改时间）</summary>
    public static bool IsCacheValid(string configPath) {
        string dbPath = GetDatabasePath();
        if (!File.Exists(dbPath)) return false;
        return GetRealLastWriteTimeUtc(dbPath) >= GetRealLastWriteTimeUtc(configPath);
    }

    /// <summary>初始化数据库，创建表（如果不存在）</summary>
    private static void InitializeDatabase(SqliteConnection conn) {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS cache (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                args TEXT,
                env TEXT,
                exec_mode INTEGER DEFAULT -1,
                prefix TEXT,
                prefix_cond TEXT,
                charset_conv TEXT,
                charset_conv_cond TEXT,
                update_at INTEGER NOT NULL
            ) WITHOUT ROWID;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>将环境变量字典序列化为 \0 分隔的字符串</summary>
    /// <param name="env">环境变量字典</param>
    /// <returns>格式: KEY=VALUE\0KEY=VALUE\0...</returns>
    public static string SerializeEnv(Dictionary<string, string> env) {
        if (env == null || env.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var kv in env) {
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        }
        return sb.ToString();
    }

    /// <summary>将 \0 分隔的字符串反序列化为环境变量字典</summary>
    /// <param name="env">格式: KEY=VALUE\0KEY=VALUE\0...</param>
    /// <returns>环境变量字典</returns>
    public static Dictionary<string, string> DeserializeEnv(string? env) {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(env)) return result;

        var pairs = env.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs) {
            int eq = pair.IndexOf('=');
            if (eq > 0) {
                result[pair[..eq]] = pair[(eq + 1)..];
            }
        }
        return result;
    }

    /// <summary>从缓存数据库中获取缓存条目（快速路径，不检查有效性）</summary>
    /// <param name="key">缓存键（别名名称，小写）</param>
    /// <returns>缓存条目，未找到返回 null</returns>
    public static CacheEntry? GetCachedEntry(string key) {
        string dbPath = GetDatabasePath();
        if (!File.Exists(dbPath)) return null;

        try {
            using var conn = new SqliteConnection(GetConnectionString());
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value, args, env, exec_mode, prefix, prefix_cond, charset_conv, charset_conv_cond FROM cache WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);

            using var reader = cmd.ExecuteReader();
            if (reader.Read()) {
                var entry = new CacheEntry {
                    Key = reader.GetString(0),
                    Value = reader.GetString(1),
                    Args = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Env = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ExecMode = reader.IsDBNull(4) ? -1 : reader.GetInt32(4),
                    Prefix = reader.IsDBNull(5) ? null : reader.GetString(5),
                    PrefixCondition = reader.IsDBNull(6) ? null : reader.GetString(6),
                    CharsetConv = reader.IsDBNull(7) ? null : reader.GetString(7),
                    CharsetConvCondition = reader.IsDBNull(8) ? null : reader.GetString(8)
                };

                // 检查缓存的文件是否仍然存在
                if (!File.Exists(entry.Value)) {
                    return null;
                }

                return entry;
            }
        } catch (SqliteException) {
            // 数据库可能损坏，尝试删除并重建
            try {
                File.Delete(dbPath);
            } catch {
                // 忽略删除错误
            }
        } catch {
            // 忽略其他缓存读取错误
        }
        return null;
    }

    /// <summary>更新缓存数据库，添加或更新指定键的值</summary>
    /// <param name="key">缓存键（别名命令字符串）</param>
    /// <param name="value">要缓存的值（解析后的完整路径）</param>
    /// <param name="args">解析后的参数（通配符已展开）</param>
    /// <param name="envDelimited">环境变量（\0 分隔格式）</param>
    /// <param name="execMode">exec 模式</param>
    /// <param name="prefix">输出前缀格式</param>
    /// <param name="prefixCond">前缀条件正则表达式</param>
    /// <param name="charsetConv">字符集转换配置</param>
    /// <param name="charsetConvCond">字符集转换条件正则表达式</param>
    public static void UpdateCache(string key, string value, string? args, string? envDelimited, int execMode = -1, string? prefix = null, string? prefixCond = null, string? charsetConv = null, string? charsetConvCond = null) {
        try {
            using var conn = new SqliteConnection(GetConnectionString());
            conn.Open();
            InitializeDatabase(conn);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO cache (key, value, args, env, exec_mode, prefix, prefix_cond, charset_conv, charset_conv_cond, update_at)
                VALUES (@key, @value, @args, @env, @execMode, @prefix, @prefixCond, @charsetConv, @charsetConvCond, @updateAt)";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.Parameters.AddWithValue("@args", (object?)args ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@env", (object?)envDelimited ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@execMode", execMode);
            cmd.Parameters.AddWithValue("@prefix", (object?)prefix ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@prefixCond", (object?)prefixCond ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@charsetConv", (object?)charsetConv ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@charsetConvCond", (object?)charsetConvCond ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@updateAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        } catch {
            // 忽略缓存写入错误
        }
    }

    /// <summary>获取所有缓存条目（用于 -p 参数）</summary>
    /// <returns>所有缓存条目列表</returns>
    public static List<CacheEntry> GetAllEntries() {
        var entries = new List<CacheEntry>();
        string dbPath = GetDatabasePath();
        if (!File.Exists(dbPath)) return entries;

        try {
            using var conn = new SqliteConnection(GetConnectionString());
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value, args, update_at FROM cache ORDER BY key";

            using var reader = cmd.ExecuteReader();
            while (reader.Read()) {
                entries.Add(new CacheEntry {
                    Key = reader.GetString(0),
                    Value = reader.GetString(1),
                    Args = reader.IsDBNull(2) ? null : reader.GetString(2),
                    UpdateAt = reader.GetInt64(3)
                });
            }
        } catch {
            // 忽略错误
        }
        return entries;
    }

    /// <summary>删除缓存数据库文件（用于 -r 参数）</summary>
    public static void DeleteDatabase() {
        try {
            string dbPath = GetDatabasePath();
            if (File.Exists(dbPath)) {
                File.Delete(dbPath);
            }
        } catch {
            // 忽略删除错误
        }
    }
}
