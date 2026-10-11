// SPDX-License-Identifier: GPL-3.0-only
using NCWE.Mcp;

var options = ServerOptions.Parse(args);
if (options.Help) { Console.Error.WriteLine(ServerOptions.Usage); return 0; }
Log.Open(options.LogFile);
using var studio = new StudioLink(options);
var tools = new ToolCatalog(studio, options);
if (options.ListTools) { Console.WriteLine(tools.Describe()); return 0; }
var server = new McpServer(tools);
Log.Write($"NCWE MCP server {McpServer.Version} on pipe {options.Pipe}");
await server.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());
return 0;
