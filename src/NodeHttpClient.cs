using System.Diagnostics;
using System.Text;

namespace DshLauncher;

/// <summary>
/// 通过 Node.js 内置 https/http 模块读取公开 JSON 接口。
/// Windows 某些环境的 .NET Schannel 可能无法建立 TLS 连接，而 dsh/npm 本身依赖 Node TLS；
/// 使用 Node 作为兜底可让版本检查与实际安装链路保持一致。
/// </summary>
internal static class NodeHttpClient
{
    private const string FetchScript = """
const http = require('http');
const https = require('https');
const target = process.argv[1];
const userAgent = process.argv[2] || 'DshLauncher';
const client = target.startsWith('https:') ? https : http;
const request = client.get(target, { headers: { 'User-Agent': userAgent, 'Accept': 'application/json' } }, response => {
  let body = '';
  response.setEncoding('utf8');
  response.on('data', chunk => body += chunk);
  response.on('end', () => {
    if (response.statusCode < 200 || response.statusCode >= 300) {
      console.error('HTTP ' + response.statusCode + ': ' + body.slice(0, 300));
      process.exit(2);
      return;
    }
    process.stdout.write(body);
  });
});
request.setTimeout(12000, () => request.destroy(new Error('request timeout')));
request.on('error', error => {
  console.error(error.message);
  process.exit(1);
});
""";

    public static async Task<string> GetStringAsync(
        string url,
        string userAgent,
        CancellationToken ct = default)
    {
        var (node, _) = HostSupervisor.ResolveDshPaths();
        var psi = new ProcessStartInfo
        {
            FileName = node ?? "node.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(FetchScript);
        psi.ArgumentList.Add(url);
        psi.ArgumentList.Add(userAgent);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 Node.js 进行网络请求");
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
            {
                var error = errorTask.Result.Trim();
                throw new HttpRequestException(string.IsNullOrWhiteSpace(error)
                    ? $"Node.js 请求失败（exit {process.ExitCode}）"
                    : error);
            }
            return outputTask.Result;
        }
        catch
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
            throw;
        }
    }
}
