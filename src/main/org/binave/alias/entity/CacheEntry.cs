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

namespace org.binave.alias.entity;

/// <summary>缓存条目 - 存储在 SQLite 数据库中的缓存记录</summary>
/// <remarks>
/// 对应数据库表结构：
/// CREATE TABLE cache (key TEXT PRIMARY KEY, value TEXT NOT NULL, args TEXT, env TEXT, exec_mode INTEGER, prefix TEXT, prefix_cond TEXT, charset_conv TEXT, charset_conv_cond TEXT, update_at INTEGER NOT NULL) WITHOUT ROWID;
/// </remarks>
internal sealed class CacheEntry {
    /// <summary>缓存键（别名名称，小写）</summary>
    public string Key { get; set; } = "";

    /// <summary>缓存值（解析后的可执行文件完整路径）</summary>
    public string Value { get; set; } = "";

    /// <summary>解析后的参数（通配符已展开）</summary>
    public string? Args { get; set; }

    /// <summary>环境变量（使用 \0 分隔的键值对字符串：KEY=VALUE\0KEY=VALUE\0...）</summary>
    public string? Env { get; set; }

    /// <summary>exec 模式设置（-1=禁用，0=立即退出，>0=延迟秒数后退出）</summary>
    public int ExecMode { get; set; } = -1;

    /// <summary>输出前缀格式</summary>
    public string? Prefix { get; set; }

    /// <summary>前缀条件正则表达式</summary>
    public string? PrefixCondition { get; set; }

    /// <summary>字符集转换配置（如 UTF-8,GBK）</summary>
    public string? CharsetConv { get; set; }

    /// <summary>字符集转换条件正则表达式</summary>
    public string? CharsetConvCondition { get; set; }

    /// <summary>更新时间（Unix 时间戳，秒）</summary>
    public long UpdateAt { get; set; }
}
