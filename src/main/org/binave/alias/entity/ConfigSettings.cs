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

using System.Collections.Generic;

namespace org.binave.alias.entity;

/// <summary>配置设置 - 存储从 .alias 解析的配置</summary>
/// <remarks>
/// 这个类封装了所有从配置文件中读取的配置项：
/// - AliasCommand: 别名对应的实际命令
/// - EnvironmentVars: 需要设置的环境变量
/// - ExecMode: 进程替换模式（-1=禁用，0=立即退出，>0=延迟秒数后退出）
/// - ExclArg: 排除通配符处理的参数索引集合
/// - CharsetConv: 字符集转换设置
/// </remarks>
internal sealed class ConfigSettings {
    /// <summary>别名对应的实际命令字符串</summary>
    public string? AliasCommand { get; set; }

    /// <summary>别名定义在配置文件中的行号</summary>
    public int LineNumber { get; set; } = 0;

    /// <summary>需要设置的环境变量字典（变量名 -> 变量值）</summary>
    public Dictionary<string, string> EnvironmentVars { get; } = new();

    /// <summary>exec 模式设置（-1=禁用，0=立即退出，>0=延迟秒数后退出）</summary>
    public int ExecMode { get; set; } = -1;

    /// <summary>排除通配符处理的参数索引集合（如 EXCL_ARG=2 表示第2个参数不处理通配符）</summary>
    public HashSet<int> ExclArgs { get; } = new();

    /// <summary>字符集转换配置（如 UTF-8,GBK）</summary>
    public string? CharsetConv { get; set; }

    /// <summary>字符集转换条件正则表达式</summary>
    public string? CharsetConvCondition { get; set; }

    /// <summary>输出前缀格式（支持 %F %T 等 date 格式）</summary>
    public string? Prefix { get; set; }

    /// <summary>前缀条件正则表达式（匹配参数时才启用前缀）</summary>
    public string? PrefixCondition { get; set; }
}
