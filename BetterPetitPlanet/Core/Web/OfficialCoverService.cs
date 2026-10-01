using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BetterPetitPlanet.Core.Web;

public sealed class OfficialCoverVisuals
{
    public string BackgroundUrl { get; set; } = "https://planet.mihoyo.com/_nuxt/img/bg-1.6a2cfc2.jpg";
    public string ForegroundUrl { get; set; } = "https://planet.mihoyo.com/_nuxt/img/character-foreground.0888a19.png";
    public string StarrySkyUrl { get; set; } = "https://planet.mihoyo.com/_nuxt/img/starry-sky@2x.c86a4f8.png";
}

public class OfficialCoverService
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    private readonly ILogger<OfficialCoverService>? _logger;

    public OfficialCoverService(ILogger<OfficialCoverService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// 动态解析官网首页最新的主视觉插画与前景人物远程链接。
    /// 当官网更换测试阶段或版本主KV时，自动提取最新图片地址，不下载到本地直接通过远程链接流式加载。
    /// 若网络不可达或超时，自动回退到可靠的官方预设链接。
    /// </summary>
    public async Task<OfficialCoverVisuals> FetchLatestCoverVisualsAsync(CancellationToken cancellationToken = default)
    {
        var result = new OfficialCoverVisuals();
        try
        {
            const string baseUrl = "https://planet.mihoyo.com";
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/home");
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using var resp = await _httpClient.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                return result;
            }

            var html = await resp.Content.ReadAsStringAsync(cancellationToken);
            var scriptMatches = Regex.Matches(html, @"<script src=""(/_nuxt/[^""]+\.js)""");
            if (scriptMatches.Count == 0)
            {
                return result;
            }

            var chunkMap = new Dictionary<int, string>();
            var homeChunkIds = new List<int>();

            foreach (Match sm in scriptMatches)
            {
                var scriptPath = sm.Groups[1].Value;
                var scriptUrl = $"{baseUrl}{scriptPath}";
                var scriptContent = await _httpClient.GetStringAsync(scriptUrl, cancellationToken);

                // 解析 Nuxt chunk 映射字典 {0:"hash0", 1:"hash1", ...}
                var mapMatch = Regex.Match(scriptContent, @"\{(\d+:""[a-f0-9]+""(?:,\d+:""[a-f0-9]+"")*)\}");
                if (mapMatch.Success)
                {
                    foreach (Match pair in Regex.Matches(mapMatch.Groups[1].Value, @"(\d+):""([a-f0-9]+)"""))
                    {
                        if (int.TryParse(pair.Groups[1].Value, out var cid))
                        {
                            chunkMap[cid] = pair.Groups[2].Value;
                        }
                    }
                }

                // 解析 /home 路由对应的分块加载清单
                var homeMatch = Regex.Match(scriptContent, @"path:""/home""[^}]*Promise\.all\(\[([^\]]+)\]\)");
                if (!homeMatch.Success)
                {
                    homeMatch = Regex.Match(scriptContent, @"Promise\.all\(\[([^\]]+)\]\)[^}]*name:""home""");
                }
                if (homeMatch.Success)
                {
                    foreach (Match em in Regex.Matches(homeMatch.Groups[1].Value, @"\.e\((\d+)\)"))
                    {
                        if (int.TryParse(em.Groups[1].Value, out var id))
                        {
                            homeChunkIds.Add(id);
                        }
                    }
                }
            }

            foreach (var cid in homeChunkIds)
            {
                if (chunkMap.TryGetValue(cid, out var hash))
                {
                    var chunkUrl = $"{baseUrl}/_nuxt/{hash}.js";
                    var chunkContent = await _httpClient.GetStringAsync(chunkUrl, cancellationToken);

                    var mBg = Regex.Match(chunkContent, @"img/(bg-[^""'\s]+\.(?:jpg|png|webp))");
                    if (mBg.Success)
                    {
                        result.BackgroundUrl = $"{baseUrl}/_nuxt/img/{mBg.Groups[1].Value}";
                    }

                    var mFg = Regex.Match(chunkContent, @"img/(character-foreground[^""'\s]+\.(?:jpg|png|webp))");
                    if (mFg.Success)
                    {
                        result.ForegroundUrl = $"{baseUrl}/_nuxt/img/{mFg.Groups[1].Value}";
                    }

                    var mSky = Regex.Match(chunkContent, @"img/(starry-sky[^""'\s]+\.(?:jpg|png|webp))");
                    if (mSky.Success)
                    {
                        result.StarrySkyUrl = $"{baseUrl}/_nuxt/img/{mSky.Groups[1].Value}";
                    }
                }
            }

            _logger?.LogInformation("OfficialCoverService resolved live visuals: Bg={Bg}, Fg={Fg}",
                result.BackgroundUrl, result.ForegroundUrl);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to resolve live visuals from official website, using reliable fallback URLs.");
        }

        return result;
    }
}
