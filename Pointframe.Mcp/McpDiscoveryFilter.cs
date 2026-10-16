using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Pointframe.Mcp;

internal static class McpDiscoveryFilter
{
    internal static McpRequestFilter<ListToolsRequestParams, ListToolsResult> Create()
    {
        return next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            return Apply(result);
        };
    }

    internal static ListToolsResult Apply(ListToolsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Tools is null)
        {
            return result;
        }

        return new ListToolsResult
        {
            Tools = result.Tools
                .Select(tool => new Tool
                {
                    Name = tool.Name,
                    Title = tool.Title,
                    Description = tool.Description,
                    InputSchema = tool.InputSchema,
                    Annotations = tool.Annotations,
                    Icons = tool.Icons,
                    Meta = tool.Meta,
                    OutputSchema = null,
                })
                .ToList(),
            TimeToLive = result.TimeToLive,
            CacheScope = result.CacheScope,
            NextCursor = result.NextCursor,
            Meta = result.Meta,
            ResultType = result.ResultType,
        };
    }
}
