using System.Text.Json;
using Nexus.Core.Services;
using Nexus.Sync.Models;

namespace Nexus.Sync.Services;

public class SubagentMapper {
    private readonly LogService _log;

    private static readonly HashSet<string> ValidAgentCodes = new() {
        "dev", "tech-lead", "tester", "test-creation-dev",
        "code-reviewer", "database-specialist", "security-auditor", "doc-writer"
    };

    private static readonly HashSet<string> SkipTypes = new() {
        "Explore", "Plan", "claude-code-guide", "Bash",
        "general-purpose", "statusline-setup"
    };

    public SubagentMapper( LogService log ) {
        _log = log;
    }

    public List<SubagentInfo> MapSubagents(string fileContent) {
        var results = new List<SubagentInfo>();

        var toolUseToType = new Dictionary<string, string>();

        var agentIdToToolUse = new Dictionary<string, string>();

        var lines = fileContent.Split('\n');

        foreach ( var line in lines ) {
            if ( string.IsNullOrWhiteSpace(line) ) continue;

            try {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if ( ! root.TryGetProperty("message", out var message) ) continue;
                if ( ! message.TryGetProperty("role", out var roleProp) ) continue;
                var role = roleProp.GetString();

                if ( ! message.TryGetProperty("content", out var content) ) continue;
                if ( content.ValueKind != JsonValueKind.Array ) continue;

                foreach ( var block in content.EnumerateArray() ) {
                    if ( ! block.TryGetProperty("type", out var typeProp) ) continue;
                    var blockType = typeProp.GetString();

                    if ( role == "assistant" && blockType == "tool_use" ) {
                        if ( block.TryGetProperty("name", out var nameProp) && nameProp.GetString() == "Task" ) {
                            if ( block.TryGetProperty("id", out var idProp) && block.TryGetProperty("input", out var inputProp) ) {
                                if ( inputProp.TryGetProperty("subagent_type", out var subTypeProp) ) {
                                    var subType = subTypeProp.GetString();
                                    var id = idProp.GetString();
                                    if ( id != null && subType != null && ! SkipTypes.Contains(subType) ) {
                                        toolUseToType[id] = subType;
                                    }
                                }
                            }
                        }
                    }

                    if ( role == "user" && blockType == "tool_result" ) {
                        if ( block.TryGetProperty("tool_use_id", out var toolUseIdProp) ) {
                            var toolUseId = toolUseIdProp.GetString();
                            if ( toolUseId == null ) continue;

                            if ( block.TryGetProperty("content", out var resultContent) ) {
                                var resultStr = resultContent.ValueKind == JsonValueKind.String
                                    ? resultContent.GetString()
                                    : resultContent.GetRawText();

                                if ( resultStr != null ) {
                                    var match = System.Text.RegularExpressions.Regex.Match(
                                        resultStr, @"agentId:\s*([a-f0-9]+)"
                                    );

                                    if ( match.Success ) {
                                        var agentId = match.Groups[1].Value;
                                        agentIdToToolUse[agentId] = toolUseId;
                                    }
                                }
                            }
                        }
                    }
                }
            } catch {
                // Skip malformed lines
            }
        }

        foreach ( var (agentId, toolUseId) in agentIdToToolUse ) {
            if ( toolUseToType.TryGetValue(toolUseId, out var subagentType) ) {
                if ( ValidAgentCodes.Contains(subagentType) ) {
                    results.Add(new SubagentInfo {
                        AgentId = agentId,
                        SubagentType = subagentType,
                        ToolUseId = toolUseId
                    });
                }
            }
        }

        if ( results.Count > 0 ) {
            _log.Write($"Found {results.Count} subagents");
        }

        return results;
    }
}
